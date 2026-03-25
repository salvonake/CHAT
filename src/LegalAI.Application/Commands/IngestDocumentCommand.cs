using LegalAI.Domain.Entities;
using LegalAI.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace LegalAI.Application.Commands;

/// <summary>
/// Command to ingest a single PDF document into the system.
/// </summary>
public sealed class IngestDocumentCommand : IRequest<IngestDocumentResult>
{
    public required string FilePath { get; init; }
    public string? CaseNamespace { get; init; }
    public string? UserId { get; init; }
}

public sealed class IngestDocumentResult
{
    public bool Success { get; init; }
    public string? DocumentId { get; init; }
    public int ChunksCreated { get; init; }
    public string? Error { get; init; }
    public double LatencyMs { get; init; }
}

public sealed class IngestDocumentHandler : IRequestHandler<IngestDocumentCommand, IngestDocumentResult>
{
    private readonly IPdfExtractor _pdfExtractor;
    private readonly IDocumentChunker _chunker;
    private readonly IEmbeddingService _embedder;
    private readonly IVectorStore _vectorStore;
    private readonly IDocumentStore _documentStore;
    private readonly IAuditService _audit;
    private readonly IMetricsCollector _metrics;
    private readonly ILogger<IngestDocumentHandler> _logger;

    public IngestDocumentHandler(
        IPdfExtractor pdfExtractor,
        IDocumentChunker chunker,
        IEmbeddingService embedder,
        IVectorStore vectorStore,
        IDocumentStore documentStore,
        IAuditService audit,
        IMetricsCollector metrics,
        ILogger<IngestDocumentHandler> logger)
    {
        _pdfExtractor = pdfExtractor;
        _chunker = chunker;
        _embedder = embedder;
        _vectorStore = vectorStore;
        _documentStore = documentStore;
        _audit = audit;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<IngestDocumentResult> Handle(IngestDocumentCommand request, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Ingesting document: {FilePath}", request.FilePath);

            // Compute file hash
            var fileBytes = await File.ReadAllBytesAsync(request.FilePath, ct);
            var contentHash = ComputeHash(fileBytes);

            // Check if already indexed with same hash
            var existing = await _documentStore.GetByFilePathAsync(request.FilePath, ct);
            if (existing is not null && existing.ContentHash == contentHash &&
                existing.Status == DocumentStatus.Indexed)
            {
                _logger.LogInformation("Document already indexed with same hash, skipping: {FilePath}",
                    request.FilePath);
                return new IngestDocumentResult
                {
                    Success = true,
                    DocumentId = existing.Id,
                    ChunksCreated = existing.ChunkCount,
                    LatencyMs = sw.Elapsed.TotalMilliseconds
                };
            }

            // Create or update document record
            var document = new LegalDocument
            {
                Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
                FilePath = request.FilePath,
                FileName = Path.GetFileName(request.FilePath),
                ContentHash = contentHash,
                FileSizeBytes = fileBytes.Length,
                Status = DocumentStatus.Indexing,
                CaseNamespace = request.CaseNamespace,
                LastModified = File.GetLastWriteTimeUtc(request.FilePath)
            };

            await _documentStore.UpsertAsync(document, ct);

            // If re-indexing, delete old vectors
            if (existing is not null)
            {
                await _vectorStore.DeleteByDocumentIdAsync(existing.Id, ct);
            }

            // Step 1: Extract PDF
            var extraction = await _pdfExtractor.ExtractAsync(request.FilePath, ct);
            if (!extraction.Success)
            {
                return await HandleFailureAsync(document, extraction.Error!, sw, ct);
            }

            document.PageCount = extraction.PageCount;

            // Step 2: Chunk document
            var chunks = _chunker.ChunkDocument(
                document.Id, document.FileName, extraction, request.CaseNamespace);

            if (chunks.Count == 0)
            {
                return await HandleFailureAsync(document,
                    "No chunks generated — document may be empty or unreadable", sw, ct);
            }

            // Step 3: Generate embeddings in batches
            var batchSize = 32;
            for (var i = 0; i < chunks.Count; i += batchSize)
            {
                var batch = chunks.Skip(i).Take(batchSize).ToList();
                var texts = batch.Select(c => c.Content).ToList();
                var embeddings = await _embedder.EmbedBatchAsync(texts, ct);

                for (var j = 0; j < batch.Count; j++)
                {
                    batch[j].Embedding = embeddings[j];
                }
            }

            // Step 4: Upsert to vector store
            await _vectorStore.UpsertAsync(chunks, ct);

            // Step 5: Update document record
            document.Status = DocumentStatus.Indexed;
            document.ChunkCount = chunks.Count;
            await _documentStore.UpsertAsync(document, ct);

            sw.Stop();

            _metrics.IncrementCounter("documents_indexed");
            _metrics.SetGauge("total_chunks", await _vectorStore.GetVectorCountAsync(ct));
            _metrics.RecordLatency("indexing_latency", sw.Elapsed.TotalMilliseconds);

            await _audit.LogAsync("DOCUMENT_INDEXED",
                $"Indexed {document.FileName}: {chunks.Count} chunks, {extraction.PageCount} pages, {sw.Elapsed.TotalMilliseconds:F0}ms",
                request.UserId, ct);

            _logger.LogInformation(
                "Successfully indexed {FileName}: {ChunkCount} chunks, {PageCount} pages in {Elapsed}ms",
                document.FileName, chunks.Count, extraction.PageCount, sw.Elapsed.TotalMilliseconds);

            return new IngestDocumentResult
            {
                Success = true,
                DocumentId = document.Id,
                ChunksCreated = chunks.Count,
                LatencyMs = sw.Elapsed.TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ingest document: {FilePath}", request.FilePath);
            _metrics.IncrementCounter("documents_failed");
            return new IngestDocumentResult
            {
                Success = false,
                Error = ex.Message,
                LatencyMs = sw.Elapsed.TotalMilliseconds
            };
        }
    }

    private async Task<IngestDocumentResult> HandleFailureAsync(
        LegalDocument document, string error, System.Diagnostics.Stopwatch sw, CancellationToken ct)
    {
        document.FailureCount++;
        document.ErrorMessage = error;

        if (document.FailureCount >= 3)
        {
            document.Status = DocumentStatus.Quarantined;
            await _documentStore.AddQuarantineRecordAsync(new QuarantineRecord
            {
                DocumentId = document.Id,
                FilePath = document.FilePath,
                Reason = error,
                FailureCount = document.FailureCount,
                ContentHash = document.ContentHash
            }, ct);
            _metrics.IncrementCounter("documents_quarantined");
            _logger.LogWarning("Document quarantined after {FailureCount} failures: {FilePath}",
                document.FailureCount, document.FilePath);
        }
        else
        {
            document.Status = DocumentStatus.Failed;
        }

        await _documentStore.UpsertAsync(document, ct);
        _metrics.IncrementCounter("documents_failed");

        sw.Stop();
        return new IngestDocumentResult
        {
            Success = false,
            DocumentId = document.Id,
            Error = error,
            LatencyMs = sw.Elapsed.TotalMilliseconds
        };
    }

    private static string ComputeHash(byte[] data)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
