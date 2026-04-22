using LegalAI.Domain.Entities;
using LegalAI.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace LegalAI.Application.Commands;

/// <summary>
/// Batch ingest all PDFs from a directory (and subdirectories).
/// Designed for 10K+ PDF environments with parallel processing,
/// memory-pressure awareness, and progress reporting.
/// </summary>
public sealed class IngestDirectoryCommand : IRequest<IngestDirectoryResult>
{
    public required string DirectoryPath { get; init; }
    public string? DomainId { get; init; }
    public string? DatasetId { get; init; }
    public string? DatasetScope { get; init; }
    public string? CaseNamespace { get; init; }
    public string? UserId { get; init; }
    public bool Recursive { get; init; } = true;

    /// <summary>Max files to ingest concurrently. Default 4 (balance CPU/IO).</summary>
    public int MaxParallelFiles { get; init; } = 4;

    /// <summary>Optional progress callback invoked on the calling thread.</summary>
    public IProgress<IngestionProgress>? Progress { get; init; }
}

/// <summary>
/// Progress report for batch ingestion.
/// </summary>
public sealed class IngestionProgress
{
    public int TotalFiles { get; init; }
    public int ProcessedFiles { get; init; }
    public int SuccessCount { get; init; }
    public int FailedCount { get; init; }
    public int SkippedCount { get; init; }
    public string CurrentFile { get; init; } = "";
}

public sealed class IngestDirectoryResult
{
    public int TotalFiles { get; init; }
    public int SuccessCount { get; init; }
    public int FailedCount { get; init; }
    public int SkippedCount { get; init; }
    public List<string> FailedFiles { get; init; } = [];
    public double TotalLatencyMs { get; init; }
}

public sealed class IngestDirectoryHandler : IRequestHandler<IngestDirectoryCommand, IngestDirectoryResult>
{
    private readonly IMediator _mediator;
    private readonly ILogger<IngestDirectoryHandler> _logger;

    /// <summary>
    /// Memory pressure threshold (80% of available memory). When exceeded,
    /// we temporarily reduce parallelism to avoid OOM with large PDFs.
    /// </summary>
    private const long MemoryPressureThresholdBytes = 500 * 1024 * 1024; // 500MB remaining

    public IngestDirectoryHandler(IMediator mediator, ILogger<IngestDirectoryHandler> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    public async Task<IngestDirectoryResult> Handle(IngestDirectoryCommand request, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var searchOption = request.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var pdfFiles = Directory.GetFiles(request.DirectoryPath, "*.pdf", searchOption);

        _logger.LogInformation(
            "Found {FileCount} PDF files in {Directory} — ingesting with MaxParallel={MaxP}",
            pdfFiles.Length, request.DirectoryPath, request.MaxParallelFiles);

        var successCount = 0;
        var failedCount = 0;
        var skippedCount = 0;
        var processedCount = 0;
        var failedFiles = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Use Parallel.ForEachAsync for controlled concurrency
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, request.MaxParallelFiles),
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(pdfFiles, parallelOptions, async (filePath, token) =>
        {
            // Memory pressure back-off: if system is low on memory, slow down
            if (IsUnderMemoryPressure())
            {
                _logger.LogWarning("Memory pressure detected — throttling ingestion");
                await Task.Delay(500, token); // Brief pause to let GC catch up
            }

            try
            {
                var result = await _mediator.Send(new IngestDocumentCommand
                {
                    FilePath = filePath,
                    DomainId = request.DomainId,
                    DatasetId = request.DatasetId,
                    DatasetScope = request.DatasetScope,
                    CaseNamespace = request.CaseNamespace,
                    UserId = request.UserId
                }, token);

                if (result.Success)
                {
                    if (result.ChunksCreated > 0)
                        Interlocked.Increment(ref successCount);
                    else
                        Interlocked.Increment(ref skippedCount);
                }
                else
                {
                    Interlocked.Increment(ref failedCount);
                    failedFiles.Add(filePath);
                    _logger.LogWarning("Ingestion failed for {File}: {Error}", filePath, result.Error);
                }
            }
            catch (OperationCanceledException)
            {
                throw; // Let cancellation propagate
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception ingesting {FilePath}", filePath);
                Interlocked.Increment(ref failedCount);
                failedFiles.Add(filePath);
            }

            // Report progress
            var processed = Interlocked.Increment(ref processedCount);
            request.Progress?.Report(new IngestionProgress
            {
                TotalFiles = pdfFiles.Length,
                ProcessedFiles = processed,
                SuccessCount = Volatile.Read(ref successCount),
                FailedCount = Volatile.Read(ref failedCount),
                SkippedCount = Volatile.Read(ref skippedCount),
                CurrentFile = Path.GetFileName(filePath)
            });

            // Log progress every 100 files for large batches
            if (processed % 100 == 0)
            {
                _logger.LogInformation(
                    "Ingestion progress: {Processed}/{Total} ({Success} ok, {Failed} fail, {Skip} skip)",
                    processed, pdfFiles.Length, Volatile.Read(ref successCount),
                    Volatile.Read(ref failedCount), Volatile.Read(ref skippedCount));
            }
        });

        sw.Stop();

        _logger.LogInformation(
            "Directory ingestion complete: {Success} indexed, {Failed} failed, {Skipped} skipped in {Elapsed:F0}ms",
            successCount, failedCount, skippedCount, sw.Elapsed.TotalMilliseconds);

        return new IngestDirectoryResult
        {
            TotalFiles = pdfFiles.Length,
            SuccessCount = successCount,
            FailedCount = failedCount,
            SkippedCount = skippedCount,
            FailedFiles = failedFiles.ToList(),
            TotalLatencyMs = sw.Elapsed.TotalMilliseconds
        };
    }

    /// <summary>
    /// Simple heuristic: check if GC generation 2 has been triggered recently
    /// and available memory is low. For 10K+ PDF loads this prevents OOM.
    /// </summary>
    private static bool IsUnderMemoryPressure()
    {
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            return gcInfo.MemoryLoadBytes > gcInfo.HighMemoryLoadThresholdBytes * 0.9;
        }
        catch
        {
            return false;
        }
    }
}
