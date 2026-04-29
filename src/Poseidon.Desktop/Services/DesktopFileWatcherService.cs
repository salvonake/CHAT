using System.IO;
using Poseidon.Application.Commands;
using Poseidon.Desktop.Diagnostics;
using Poseidon.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Poseidon.Desktop.Services;

/// <summary>
/// Background hosted service that watches a directory for PDF additions/changes
/// and automatically triggers ingestion. Runs inside the WPF application host.
///
/// Features:
///   - FileSystemWatcher for real-time detection
///   - Debounced processing (2s per file)
///   - Throttled batches (max 10 files per 5s interval)
///   - Startup scan of existing un-indexed files
///   - Events raised for UI progress updates
/// </summary>
public sealed class DesktopFileWatcherService : BackgroundService
{
    private readonly IMediator _mediator;
    private readonly IDocumentStore _docStore;
    private readonly DataPaths _paths;
    private readonly IConfiguration _configuration;
    private readonly RuntimeConfigurationService _runtimeConfiguration;
    private readonly ILogger<DesktopFileWatcherService> _logger;

    private FileSystemWatcher? _watcher;
    private string _activeWatchDirectory = "";
    private CancellationToken _stoppingToken;

    // Debounce/throttle
    private readonly Dictionary<string, DateTime> _pendingFiles = new();
    private readonly SemaphoreSlim _batchLock = new(1, 1);
    private const int DebounceMs = 2000;
    private const int BatchIntervalMs = 5000;
    private const int MaxFilesPerBatch = 10;

    // Events for UI binding
    public event Action<string, FileWatcherEventType>? FileEvent;
    public event Action<string, int, int>? IngestionProgress; // fileName, current, total

    public DesktopFileWatcherService(
        IMediator mediator,
        IDocumentStore docStore,
        DataPaths paths,
        IConfiguration configuration,
        RuntimeConfigurationService runtimeConfiguration,
        ILogger<DesktopFileWatcherService> logger)
    {
        _mediator = mediator;
        _docStore = docStore;
        _paths = paths;
        _configuration = configuration;
        _runtimeConfiguration = runtimeConfiguration;
        _logger = logger;
        _runtimeConfiguration.ConfigurationReloaded += OnConfigurationReloaded;
    }

    public DesktopFileWatcherService(
        IMediator mediator,
        IDocumentStore docStore,
        DataPaths paths,
        ILogger<DesktopFileWatcherService> logger)
        : this(
            mediator,
            docStore,
            paths,
            new ConfigurationBuilder().Build(),
            new RuntimeConfigurationService(
                new ConfigurationBuilder().Build(),
                paths,
                [],
                NullLogger<RuntimeConfigurationService>.Instance),
            logger)
    {
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        await ConfigureWatcherAsync(ResolveWatchDirectory(), stoppingToken);

        // Process loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(BatchIntervalMs, stoppingToken);
                await ProcessPendingBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in file watcher processing loop");
            }
        }
    }

    private Task ScanExistingFilesAsync(string directory, CancellationToken ct)
    {
        try
        {
            var pdfFiles = Directory.GetFiles(directory, "*.pdf", SearchOption.AllDirectories);
            _logger.LogInformation("Initial scan found {Count} PDF files", pdfFiles.Length);

            foreach (var file in pdfFiles)
            {
                if (ct.IsCancellationRequested) break;
                QueueFile(file);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning existing files");
        }

        return Task.CompletedTask;
    }

    private async void OnConfigurationReloaded(object? sender, ConfigurationReloadedEventArgs e)
    {
        if (!e.Success || _stoppingToken.IsCancellationRequested)
            return;

        var desiredDirectory = ResolveWatchDirectory();
        if (string.Equals(desiredDirectory, _activeWatchDirectory, StringComparison.OrdinalIgnoreCase))
            return;

        await ConfigureWatcherAsync(desiredDirectory, _stoppingToken);
    }

    private async Task ConfigureWatcherAsync(string watchDir, CancellationToken ct)
    {
        Directory.CreateDirectory(watchDir);

        _watcher?.Dispose();
        _watcher = null;
        _activeWatchDirectory = watchDir;

        _logger.LogInformation("File watcher configured on: {Dir}", watchDir);
        await ScanExistingFilesAsync(watchDir, ct);

        _watcher = new FileSystemWatcher(watchDir, "*.pdf")
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _watcher.Created += OnFileCreated;
        _watcher.Changed += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
        _watcher.Error += OnWatcherError;
    }

    private string ResolveWatchDirectory()
    {
        var configured = _configuration["Ingestion:WatchDirectory"];
        return string.IsNullOrWhiteSpace(configured)
            ? _paths.WatchDirectory
            : configured.Trim();
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        _logger.LogInformation("File detected: {Path}", e.FullPath);
        FileEvent?.Invoke(e.FullPath, FileWatcherEventType.Created);
        QueueFile(e.FullPath);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        _logger.LogInformation("File changed: {Path}", e.FullPath);
        FileEvent?.Invoke(e.FullPath, FileWatcherEventType.Changed);
        QueueFile(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        _logger.LogInformation("File renamed: {Old} â†’ {New}", e.OldFullPath, e.FullPath);
        FileEvent?.Invoke(e.FullPath, FileWatcherEventType.Renamed);
        QueueFile(e.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "FileSystemWatcher error");
    }

    private void QueueFile(string filePath)
    {
        lock (_pendingFiles)
        {
            _pendingFiles[filePath] = DateTime.UtcNow;
        }
    }

    private async Task ProcessPendingBatchAsync(CancellationToken ct)
    {
        List<string> filesToProcess;

        lock (_pendingFiles)
        {
            var now = DateTime.UtcNow;
            var cutoff = now.AddMilliseconds(-DebounceMs);

            filesToProcess = _pendingFiles
                .Where(kv => kv.Value <= cutoff)
                .Select(kv => kv.Key)
                .Take(MaxFilesPerBatch)
                .ToList();

            foreach (var f in filesToProcess)
                _pendingFiles.Remove(f);
        }

        if (filesToProcess.Count == 0) return;

        await _batchLock.WaitAsync(ct);
        try
        {
            var total = filesToProcess.Count;
            for (int i = 0; i < total; i++)
            {
                var filePath = filesToProcess[i];
                ct.ThrowIfCancellationRequested();

                // Wait for file to be ready (not locked by another process)
                if (!await WaitForFileReadyAsync(filePath, ct))
                {
                    _logger.LogWarning("File not ready after waiting: {Path}", filePath);
                    QueueFile(filePath); // Re-queue
                    continue;
                }

                try
                {
                    IngestionProgress?.Invoke(Path.GetFileName(filePath), i + 1, total);

                    var result = await _mediator.Send(new IngestDocumentCommand
                    {
                        FilePath = filePath,
                        UserId = "FileWatcher"
                    }, ct);

                    if (result.Success)
                    {
                        _logger.LogInformation("Auto-indexed: {File} ({Chunks} chunks)",
                            Path.GetFileName(filePath), result.ChunksCreated);
                        FileEvent?.Invoke(filePath, FileWatcherEventType.Indexed);
                    }
                    else
                    {
                        _logger.LogWarning("Ingestion failed: {File} â€” {Error}",
                            Path.GetFileName(filePath), result.Error);
                        FileEvent?.Invoke(filePath, FileWatcherEventType.Failed);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error ingesting {File}", filePath);
                    FileEvent?.Invoke(filePath, FileWatcherEventType.Failed);
                }
            }
        }
        finally
        {
            _batchLock.Release();
        }
    }

    private static async Task<bool> WaitForFileReadyAsync(string filePath, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return true;
            }
            catch (IOException)
            {
                await Task.Delay(500, ct);
            }
        }
        return false;
    }

    public override void Dispose()
    {
        _runtimeConfiguration.ConfigurationReloaded -= OnConfigurationReloaded;
        _watcher?.Dispose();
        _batchLock.Dispose();
        base.Dispose();
    }
}

public enum FileWatcherEventType
{
    Created,
    Changed,
    Renamed,
    Indexed,
    Failed
}

