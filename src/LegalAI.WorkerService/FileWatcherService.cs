using System.Collections.Concurrent;
using System.Security.Cryptography;
using LegalAI.Application.Commands;
using LegalAI.Domain.Entities;
using LegalAI.Domain.Interfaces;
using MediatR;

namespace LegalAI.WorkerService;

/// <summary>
/// Background service that watches a directory for PDF file changes
/// and automatically triggers incremental indexing.
/// 
/// Features:
/// - Hash-based change detection (only re-index modified files)
/// - Indexing queue with prioritization
/// - Auto-quarantine after repeated failures
/// - Throttling for large batch imports
/// - File churn rate tracking
/// </summary>
public sealed class FileWatcherService : BackgroundService
{
    private readonly ILogger<FileWatcherService> _logger;
    private readonly IMediator _mediator;
    private readonly IMetricsCollector _metrics;
    private readonly IAuditService _audit;
    private readonly IIngestionJobStore _jobs;
    private readonly string _watchPath;
    private readonly string _quarantinePath;
    private readonly int _maxRetryAttempts;
    private readonly int _retryBackoffSeconds;
    private readonly ConcurrentQueue<FileChangeEvent> _indexingQueue = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentChanges = new();
    private FileSystemWatcher? _watcher;

    // Throttling: max files to process per batch interval
    private const int MaxFilesPerBatch = 10;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DebouncePeriod = TimeSpan.FromSeconds(2);

    public FileWatcherService(
        ILogger<FileWatcherService> logger,
        IMediator mediator,
        IMetricsCollector metrics,
        IAuditService audit,
        IIngestionJobStore jobs,
        IConfiguration config)
    {
        _logger = logger;
        _mediator = mediator;
        _metrics = metrics;
        _audit = audit;
        _jobs = jobs;
        _watchPath = config.GetValue("Data:PdfWatchDirectory", "pdfs")!;
        _quarantinePath = config.GetValue("Data:QuarantineDirectory", Path.Combine(_watchPath, "..", "quarantine"))!;
        _maxRetryAttempts = Math.Clamp(config.GetValue("Ingestion:MaxRetryAttempts", 3), 1, 10);
        _retryBackoffSeconds = Math.Clamp(config.GetValue("Ingestion:RetryBackoffSeconds", 5), 1, 600);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Directory.Exists(_watchPath))
        {
            Directory.CreateDirectory(_watchPath);
            _logger.LogInformation("Created PDF watch directory: {Path}", _watchPath);
        }

        if (!Directory.Exists(_quarantinePath))
        {
            Directory.CreateDirectory(_quarantinePath);
            _logger.LogInformation("Created quarantine directory: {Path}", _quarantinePath);
        }

        // Initial scan
        await InitialScanAsync(stoppingToken);

        // Start file watcher
        StartWatcher();

        _logger.LogInformation("File watcher started on: {Path}", _watchPath);
        await _audit.LogAsync("FILE_WATCHER_STARTED", $"Watching: {_watchPath}", ct: stoppingToken);

        // Processing loop
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessQueueAsync(stoppingToken);
            await Task.Delay(BatchInterval, stoppingToken);
        }

        _watcher?.Dispose();
    }

    /// <summary>
    /// Scans all existing PDFs in the watch directory for initial indexing.
    /// </summary>
    private Task InitialScanAsync(CancellationToken ct)
    {
        var files = Directory.GetFiles(_watchPath, "*.pdf", SearchOption.AllDirectories);
        _logger.LogInformation("Initial scan found {Count} PDF files", files.Length);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            _indexingQueue.Enqueue(new FileChangeEvent
            {
                FilePath = file,
                ChangeType = WatcherChangeTypes.Created,
                Timestamp = DateTimeOffset.UtcNow,
                Priority = IndexPriority.Normal
            });
        }

        return Task.CompletedTask;
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(_watchPath, "*.pdf")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        _watcher.Created += (_, e) => OnFileChange(e.FullPath, WatcherChangeTypes.Created, IndexPriority.High);
        _watcher.Changed += (_, e) => OnFileChange(e.FullPath, WatcherChangeTypes.Changed, IndexPriority.Normal);
        _watcher.Deleted += (_, e) => OnFileChange(e.FullPath, WatcherChangeTypes.Deleted, IndexPriority.Low);
        _watcher.Renamed += (_, e) =>
        {
            OnFileChange(e.OldFullPath, WatcherChangeTypes.Deleted, IndexPriority.Low);
            OnFileChange(e.FullPath, WatcherChangeTypes.Created, IndexPriority.High);
        };

        _watcher.Error += (_, e) =>
        {
            _logger.LogError(e.GetException(), "File watcher error");
        };
    }

    private void OnFileChange(string filePath, WatcherChangeTypes changeType, IndexPriority priority)
    {
        // Debounce: ignore rapid repeated changes to the same file
        var now = DateTimeOffset.UtcNow;
        if (_recentChanges.TryGetValue(filePath, out var lastChange) &&
            now - lastChange < DebouncePeriod)
        {
            return;
        }

        _recentChanges[filePath] = now;

        _indexingQueue.Enqueue(new FileChangeEvent
        {
            FilePath = filePath,
            ChangeType = changeType,
            Timestamp = now,
            Priority = priority
        });

        _metrics.SetGauge("indexing_queue_depth", _indexingQueue.Count);
        _metrics.IncrementCounter("file_changes_detected");

        _logger.LogDebug("Queued {ChangeType}: {FilePath}", changeType, filePath);
    }

    /// <summary>
    /// Processes the indexing queue in priority order with throttling.
    /// </summary>
    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        if (_indexingQueue.IsEmpty) return;

        // Dequeue up to MaxFilesPerBatch items, prioritized
        var batch = new List<FileChangeEvent>();
        while (batch.Count < MaxFilesPerBatch && _indexingQueue.TryDequeue(out var evt))
        {
            batch.Add(evt);
        }

        if (batch.Count == 0) return;

        // Sort by priority (high first)
        batch.Sort((a, b) => b.Priority.CompareTo(a.Priority));

        _logger.LogInformation("Processing {Count} file change events", batch.Count);

        foreach (var evt in batch)
        {
            ct.ThrowIfCancellationRequested();

            IngestionJob? job = null;
            var attempt = 0;

            try
            {
                if (evt.ChangeType == WatcherChangeTypes.Deleted)
                {
                    _logger.LogInformation("File deleted, will be cleaned on next integrity check: {Path}", evt.FilePath);
                    continue;
                }

                // Wait for file to be fully written
                await WaitForFileReady(evt.FilePath, ct);

                var contentHash = await ComputeFileHashAsync(evt.FilePath, ct);
                job = await _jobs.GetByFileAndHashAsync(evt.FilePath, contentHash, ct);

                if (job is not null && job.Status == IngestionJobStatus.Succeeded)
                {
                    _logger.LogDebug("Skipping already succeeded job for {Path}", evt.FilePath);
                    continue;
                }

                if (job is null)
                {
                    job = await _jobs.CreateAsync(new IngestionJob
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        FilePath = evt.FilePath,
                        ContentHash = contentHash,
                        Status = IngestionJobStatus.Queued,
                        AttemptCount = 0,
                        MaxAttempts = _maxRetryAttempts,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    }, ct);
                }

                if (job.NextAttemptAt is not null && job.NextAttemptAt > DateTimeOffset.UtcNow)
                {
                    _indexingQueue.Enqueue(evt with { Priority = IndexPriority.Low, Timestamp = DateTimeOffset.UtcNow });
                    continue;
                }

                attempt = job.AttemptCount + 1;
                await _jobs.MarkRunningAsync(job.Id, attempt, ct);

                var result = await _mediator.Send(new IngestDocumentCommand
                {
                    FilePath = evt.FilePath
                }, ct);

                if (result.Success)
                {
                    await _jobs.MarkSucceededAsync(job.Id, ct);
                    _logger.LogInformation(
                        "Auto-indexed {FilePath}: {Chunks} chunks in {Latency}ms",
                        evt.FilePath, result.ChunksCreated, result.LatencyMs);
                    _metrics.IncrementCounter("ingestion_jobs_succeeded");
                }
                else
                {
                    await HandleFailureAsync(
                        job,
                        attempt,
                        result.Error ?? "Unknown ingestion failure",
                        evt,
                        ct);
                }
            }
            catch (Exception ex)
            {
                if (job is not null)
                {
                    var safeAttempt = attempt > 0 ? attempt : Math.Max(1, job.AttemptCount + 1);
                    await HandleFailureAsync(job, safeAttempt, ex.Message, evt, ct);
                }

                _logger.LogError(ex, "Error processing file change: {FilePath}", evt.FilePath);
            }
        }

        _metrics.SetGauge("indexing_queue_depth", _indexingQueue.Count);
    }

    /// <summary>
    /// Waits for a file to be fully written and unlocked.
    /// </summary>
    private static async Task WaitForFileReady(string filePath, CancellationToken ct)
    {
        const int maxRetries = 10;
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                using var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
                return; // File is ready
            }
            catch (IOException)
            {
                await Task.Delay(500, ct);
            }
        }
    }

    private async Task HandleFailureAsync(
        IngestionJob job,
        int attempt,
        string error,
        FileChangeEvent evt,
        CancellationToken ct)
    {
        if (attempt < job.MaxAttempts)
        {
            var nextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(_retryBackoffSeconds * Math.Pow(2, attempt - 1));
            await _jobs.MarkFailedAsync(job.Id, error, nextAttemptAt, ct);

            _indexingQueue.Enqueue(evt with { Priority = IndexPriority.Low, Timestamp = DateTimeOffset.UtcNow });
            _metrics.IncrementCounter("ingestion_jobs_retry");

            _logger.LogWarning(
                "Auto-index failed for {FilePath} (attempt {Attempt}/{MaxAttempts}). Retrying at {NextAttempt}: {Error}",
                evt.FilePath,
                attempt,
                job.MaxAttempts,
                nextAttemptAt,
                error);

            return;
        }

        var quarantineTarget = await MoveToQuarantineAsync(evt.FilePath, ct);
        await _jobs.MarkQuarantinedAsync(job.Id, quarantineTarget ?? string.Empty, error, ct);
        await _audit.LogAsync("INGESTION_QUARANTINED", $"File moved to quarantine: {evt.FilePath}", ct: ct);
        _metrics.IncrementCounter("ingestion_jobs_quarantined");

        _logger.LogWarning(
            "Auto-index exhausted retries for {FilePath} and was quarantined. Error: {Error}",
            evt.FilePath,
            error);
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<string?> MoveToQuarantineAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        var fileName = Path.GetFileName(filePath);
        var targetName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}_{fileName}";
        var targetPath = Path.Combine(_quarantinePath, targetName);

        await Task.Run(() => File.Move(filePath, targetPath), ct);
        return targetPath;
    }

    private sealed record FileChangeEvent
    {
        public required string FilePath { get; init; }
        public WatcherChangeTypes ChangeType { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public IndexPriority Priority { get; init; }
    }

    private enum IndexPriority
    {
        Low = 0,
        Normal = 1,
        High = 2
    }
}
