using LegalAI.Application.Commands;
using LegalAI.Domain.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace LegalAI.Api.Controllers;

/// <summary>
/// Document ingestion and management endpoints.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class DocumentsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IDocumentStore _documentStore;
    private readonly IVectorStore _vectorStore;

    public DocumentsController(
        IMediator mediator,
        IDocumentStore documentStore,
        IVectorStore vectorStore)
    {
        _mediator = mediator;
        _documentStore = documentStore;
        _vectorStore = vectorStore;
    }

    /// <summary>
    /// Lists all indexed documents with their status.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetDocuments(CancellationToken ct)
    {
        var docs = await _documentStore.GetAllAsync(ct);
        return Ok(docs.Select(d => new DocumentDto
        {
            Id = d.Id,
            FileName = d.FileName,
            Status = d.Status.ToString(),
            PageCount = d.PageCount,
            ChunkCount = d.ChunkCount,
            IndexedAt = d.IndexedAt,
            FileSizeBytes = d.FileSizeBytes,
            CaseNamespace = d.CaseNamespace,
            ErrorMessage = d.ErrorMessage
        }));
    }

    /// <summary>
    /// Ingest a single PDF document by file path.
    /// </summary>
    [HttpPost("ingest")]
    public async Task<IActionResult> IngestDocument([FromBody] IngestRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.FilePath))
            return BadRequest(new { error = "مسار الملف مطلوب / File path is required" });

        if (!System.IO.File.Exists(request.FilePath))
            return BadRequest(new { error = "الملف غير موجود / File not found" });

        var result = await _mediator.Send(new IngestDocumentCommand
        {
            FilePath = request.FilePath,
            CaseNamespace = request.CaseNamespace,
            UserId = request.UserId
        }, ct);

        if (result.Success)
            return Ok(new
            {
                result.DocumentId,
                result.ChunksCreated,
                result.LatencyMs,
                message = "تم فهرسة الوثيقة بنجاح / Document indexed successfully"
            });

        return UnprocessableEntity(new { result.Error, result.LatencyMs });
    }

    /// <summary>
    /// Ingest all PDF documents from a directory.
    /// </summary>
    [HttpPost("ingest/directory")]
    public async Task<IActionResult> IngestDirectory([FromBody] IngestDirectoryRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DirectoryPath))
            return BadRequest(new { error = "مسار المجلد مطلوب / Directory path is required" });

        if (!Directory.Exists(request.DirectoryPath))
            return BadRequest(new { error = "المجلد غير موجود / Directory not found" });

        var result = await _mediator.Send(new IngestDirectoryCommand
        {
            DirectoryPath = request.DirectoryPath,
            CaseNamespace = request.CaseNamespace,
            UserId = request.UserId,
            Recursive = request.Recursive ?? true
        }, ct);

        return Ok(new
        {
            result.TotalFiles,
            result.SuccessCount,
            result.FailedCount,
            result.SkippedCount,
            result.FailedFiles,
            result.TotalLatencyMs,
            message = $"تم فهرسة {result.SuccessCount} من {result.TotalFiles} وثيقة / Indexed {result.SuccessCount} of {result.TotalFiles} documents"
        });
    }

    /// <summary>
    /// Get quarantined documents.
    /// </summary>
    [HttpGet("quarantine")]
    public async Task<IActionResult> GetQuarantine(CancellationToken ct)
    {
        var records = await _documentStore.GetQuarantineRecordsAsync(ct);
        return Ok(records);
    }

    /// <summary>
    /// Get index statistics.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        var docCount = await _documentStore.GetDocumentCountAsync(ct);
        var vectorCount = await _vectorStore.GetVectorCountAsync(ct);
        var health = await _vectorStore.GetHealthAsync(ct);

        return Ok(new
        {
            DocumentCount = docCount,
            VectorCount = vectorCount,
            VectorStoreHealthy = health.IsHealthy,
            VectorStoreStatus = health.Status
        });
    }
}

public sealed class DocumentDto
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required string Status { get; init; }
    public int PageCount { get; init; }
    public int ChunkCount { get; init; }
    public DateTimeOffset IndexedAt { get; init; }
    public long FileSizeBytes { get; init; }
    public string? CaseNamespace { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class IngestRequest
{
    public string FilePath { get; init; } = "";
    public string? CaseNamespace { get; init; }
    public string? UserId { get; init; }
}

public sealed class IngestDirectoryRequest
{
    public string DirectoryPath { get; init; } = "";
    public string? CaseNamespace { get; init; }
    public string? UserId { get; init; }
    public bool? Recursive { get; init; }
}
