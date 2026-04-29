using Poseidon.Application.Commands;
using Poseidon.Application.Services;
using Poseidon.Api.Localization;
using Poseidon.Domain.Entities;
using Poseidon.Domain.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Poseidon.Api.Controllers;

/// <summary>
/// Document ingestion and management endpoints.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class DocumentsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IDocumentStore _documentStore;
    private readonly IVectorStore _vectorStore;
    private readonly IUserDomainGrantStore _domainGrants;
    private readonly IDatasetStore _datasets;
    private readonly IDomainModuleRegistry _domainRegistry;
    private readonly ApiTextLocalizer _text;

    public DocumentsController(
        IMediator mediator,
        IDocumentStore documentStore,
        IVectorStore vectorStore,
        IUserDomainGrantStore domainGrants,
        IDatasetStore datasets,
        IDomainModuleRegistry domainRegistry,
        ApiTextLocalizer text)
    {
        _mediator = mediator;
        _documentStore = documentStore;
        _vectorStore = vectorStore;
        _domainGrants = domainGrants;
        _datasets = datasets;
        _domainRegistry = domainRegistry;
        _text = text;
    }

    /// <summary>
    /// Lists all indexed documents with their status.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = "CanRead")]
    public async Task<IActionResult> GetDocuments(CancellationToken ct)
    {
        var language = _text.ResolveLanguage(HttpContext);
        var docs = await _documentStore.GetAllAsync(ct);

        if (User.IsInRole("Admin"))
        {
            return Ok(docs.Select(MapDocument));
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new { error = _text.T("UserIdentityNotFound", language) });
        }

        var grants = await _domainGrants.GetForUserAsync(userId, ct);
        if (grants.Count == 0)
        {
            return Ok(Array.Empty<DocumentDto>());
        }

        var datasetLookup = await BuildDatasetLookupAsync(ct);
        var filteredDocs = docs.Where(d =>
            IsDocumentAllowed(d, grants, datasetLookup, _domainRegistry.ActiveDomainId));

        return Ok(filteredDocs.Select(MapDocument));
    }

    private static DocumentDto MapDocument(LegalDocument d)
    {
        return new DocumentDto
        {
            Id = d.Id,
            FileName = d.FileName,
            Status = d.Status.ToString(),
            PageCount = d.PageCount,
            ChunkCount = d.ChunkCount,
            IndexedAt = d.IndexedAt,
            FileSizeBytes = d.FileSizeBytes,
            DomainId = d.DomainId,
            DatasetId = d.DatasetId,
            CaseNamespace = d.CaseNamespace,
            ErrorMessage = d.ErrorMessage
        };
    }

    /// <summary>
    /// Ingest a single PDF document by file path.
    /// </summary>
    [HttpPost("ingest")]
    [Authorize(Policy = "CanIngest")]
    public async Task<IActionResult> IngestDocument([FromBody] IngestRequest request, CancellationToken ct)
    {
        var language = _text.ResolveLanguage(HttpContext, request.Language);

        if (string.IsNullOrWhiteSpace(request.FilePath))
            return BadRequest(new { error = _text.T("FilePathRequired", language) });

        if (!System.IO.File.Exists(request.FilePath))
            return BadRequest(new { error = _text.T("FileNotFound", language) });

        var userId = HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? request.UserId;
        var isAdmin = User.IsInRole("Admin");
        var resolvedScope = await ResolveIngestionScopeAsync(request.DomainId, request.DatasetId, language, ct);
        if (resolvedScope.Error is not null)
        {
            return resolvedScope.Error;
        }

        if (!isAdmin)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized(new { error = _text.T("UserIdentityNotFound", language) });
            }

            var hasAccess = await _domainGrants.HasAccessAsync(
                userId,
                resolvedScope.DomainId,
                resolvedScope.DatasetScope,
                ct);

            if (!hasAccess)
            {
                return Forbid();
            }
        }

        var caseNamespaceError = ResolveCaseNamespace(
            isAdmin,
            request.CaseNamespace,
            resolvedScope.DomainId,
            resolvedScope.DatasetScope,
            language,
            out var resolvedCaseNamespace);

        if (caseNamespaceError is not null)
        {
            return caseNamespaceError;
        }

        var result = await _mediator.Send(new IngestDocumentCommand
        {
            FilePath = request.FilePath,
            DomainId = resolvedScope.DomainId,
            DatasetId = resolvedScope.DatasetId,
            DatasetScope = resolvedScope.DatasetScope,
            CaseNamespace = resolvedCaseNamespace,
            UserId = userId
        }, ct);

        if (result.Success)
            return Ok(new
            {
                result.DocumentId,
                result.ChunksCreated,
                result.LatencyMs,
                message = _text.T("DocumentIndexedSuccessfully", language)
            });

        return UnprocessableEntity(new { result.Error, result.LatencyMs });
    }

    /// <summary>
    /// Ingest all PDF documents from a directory.
    /// </summary>
    [HttpPost("ingest/directory")]
    [Authorize(Policy = "CanIngest")]
    public async Task<IActionResult> IngestDirectory([FromBody] IngestDirectoryRequest request, CancellationToken ct)
    {
        var language = _text.ResolveLanguage(HttpContext, request.Language);

        if (string.IsNullOrWhiteSpace(request.DirectoryPath))
            return BadRequest(new { error = _text.T("DirectoryPathRequired", language) });

        if (!Directory.Exists(request.DirectoryPath))
            return BadRequest(new { error = _text.T("DirectoryNotFound", language) });

        var userId = HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? request.UserId;
        var isAdmin = User.IsInRole("Admin");
        var resolvedScope = await ResolveIngestionScopeAsync(request.DomainId, request.DatasetId, language, ct);
        if (resolvedScope.Error is not null)
        {
            return resolvedScope.Error;
        }

        if (!isAdmin)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized(new { error = _text.T("UserIdentityNotFound", language) });
            }

            var hasAccess = await _domainGrants.HasAccessAsync(
                userId,
                resolvedScope.DomainId,
                resolvedScope.DatasetScope,
                ct);

            if (!hasAccess)
            {
                return Forbid();
            }
        }

        var caseNamespaceError = ResolveCaseNamespace(
            isAdmin,
            request.CaseNamespace,
            resolvedScope.DomainId,
            resolvedScope.DatasetScope,
            language,
            out var resolvedCaseNamespace);

        if (caseNamespaceError is not null)
        {
            return caseNamespaceError;
        }

        var result = await _mediator.Send(new IngestDirectoryCommand
        {
            DirectoryPath = request.DirectoryPath,
            DomainId = resolvedScope.DomainId,
            DatasetId = resolvedScope.DatasetId,
            DatasetScope = resolvedScope.DatasetScope,
            CaseNamespace = resolvedCaseNamespace,
            UserId = userId,
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
            message = _text.T("DocumentsIndexedSummary", language, result.SuccessCount, result.TotalFiles)
        });
    }

    /// <summary>
    /// Get quarantined documents.
    /// </summary>
    [HttpGet("quarantine")]
    [Authorize(Policy = "CanRead")]
    public async Task<IActionResult> GetQuarantine(CancellationToken ct)
    {
        var records = await _documentStore.GetQuarantineRecordsAsync(ct);
        return Ok(records);
    }

    /// <summary>
    /// Get index statistics.
    /// </summary>
    [HttpGet("stats")]
    [Authorize(Policy = "CanRead")]
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

    private async Task<(string DomainId, string? DatasetId, string? DatasetScope, IActionResult? Error)> ResolveIngestionScopeAsync(
        string? requestedDomainId,
        string? requestedDatasetId,
        string language,
        CancellationToken ct)
    {
        var resolvedDomainId = NormalizeDomain(requestedDomainId) ?? _domainRegistry.ActiveDomainId;
        var normalizedDatasetId = NormalizeIdentifier(requestedDatasetId);

        if (string.IsNullOrWhiteSpace(normalizedDatasetId))
        {
            return (resolvedDomainId, null, null, null);
        }

        var dataset = await _datasets.GetByIdAsync(normalizedDatasetId, ct);
        if (dataset is null)
        {
            return (
                resolvedDomainId,
                null,
                null,
                BadRequest(new { error = _text.T("DatasetNotFound", language) }));
        }

        if (dataset.Lifecycle == DatasetLifecycle.Archived)
        {
            return (
                resolvedDomainId,
                null,
                null,
                BadRequest(new { error = _text.T("DatasetArchivedCannotIngest", language) }));
        }

        if (!string.IsNullOrWhiteSpace(requestedDomainId)
            && !string.Equals(resolvedDomainId, dataset.DomainId, StringComparison.OrdinalIgnoreCase))
        {
            return (
                resolvedDomainId,
                null,
                null,
                BadRequest(new { error = _text.T("DatasetDomainMismatch", language) }));
        }

        return (dataset.DomainId, normalizedDatasetId, dataset.Name, null);
    }

    private async Task<Dictionary<string, Dataset>> BuildDatasetLookupAsync(CancellationToken ct)
    {
        var datasets = await _datasets.GetAllAsync(includeArchived: true, ct);
        return datasets.ToDictionary(d => d.Id, d => d, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsDocumentAllowed(
        LegalDocument document,
        IReadOnlyCollection<UserDomainGrant> grants,
        IReadOnlyDictionary<string, Dataset> datasetLookup,
        string fallbackDomainId)
    {
        var effectiveDomain = string.IsNullOrWhiteSpace(document.DomainId)
            ? fallbackDomainId
            : document.DomainId;

        string? effectiveDatasetScope = null;
        if (!string.IsNullOrWhiteSpace(document.DatasetId)
            && datasetLookup.TryGetValue(document.DatasetId, out var dataset))
        {
            effectiveDomain = dataset.DomainId;
            effectiveDatasetScope = dataset.Name;
        }

        return grants.Any(g =>
            string.Equals(g.DomainId, effectiveDomain, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(g.DatasetScope)
                || (!string.IsNullOrWhiteSpace(effectiveDatasetScope)
                    && string.Equals(g.DatasetScope, effectiveDatasetScope, StringComparison.OrdinalIgnoreCase))));
    }

    private static string? NormalizeDomain(string? domainId)
    {
        return string.IsNullOrWhiteSpace(domainId)
            ? null
            : domainId.Trim().ToLowerInvariant();
    }

    private static string? NormalizeIdentifier(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private IActionResult? ResolveCaseNamespace(
        bool isAdmin,
        string? requestedCaseNamespace,
        string domainId,
        string? datasetScope,
        string language,
        out string? resolvedCaseNamespace)
    {
        var canonicalCaseNamespace = ScopeNamespaceBuilder.Build(domainId, datasetScope);

        if (!isAdmin
            && !string.IsNullOrWhiteSpace(requestedCaseNamespace)
            && !string.IsNullOrWhiteSpace(canonicalCaseNamespace)
            && !string.Equals(requestedCaseNamespace, canonicalCaseNamespace, StringComparison.OrdinalIgnoreCase))
        {
            resolvedCaseNamespace = null;
            return BadRequest(new { error = _text.T("CaseNamespaceMismatch", language) });
        }

        resolvedCaseNamespace = isAdmin
            ? requestedCaseNamespace ?? canonicalCaseNamespace
            : canonicalCaseNamespace;

        return null;
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
    public string? DomainId { get; init; }
    public string? DatasetId { get; init; }
    public string? CaseNamespace { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class IngestRequest
{
    public string FilePath { get; init; } = "";
    public string? Language { get; init; }
    public string? DomainId { get; init; }
    public string? DatasetId { get; init; }
    public string? CaseNamespace { get; init; }
    public string? UserId { get; init; }
}

public sealed class IngestDirectoryRequest
{
    public string DirectoryPath { get; init; } = "";
    public string? Language { get; init; }
    public string? DomainId { get; init; }
    public string? DatasetId { get; init; }
    public string? CaseNamespace { get; init; }
    public string? UserId { get; init; }
    public bool? Recursive { get; init; }
}

