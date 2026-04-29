using Poseidon.Application.Commands;
using Poseidon.Application.Queries;
using Poseidon.Api.Localization;
using Poseidon.Domain.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Poseidon.Api.Controllers;

/// <summary>
/// Core legal question-answering endpoint.
/// Implements the Evidence-Constrained RAG pipeline.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "CanQuery")]
public sealed class AskController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IUserDomainGrantStore _domainGrants;
    private readonly IDomainModuleRegistry _domainRegistry;
    private readonly ApiTextLocalizer _text;
    private readonly ILogger<AskController> _logger;

    public AskController(
        IMediator mediator,
        IUserDomainGrantStore domainGrants,
        IDomainModuleRegistry domainRegistry,
        ApiTextLocalizer text,
        ILogger<AskController> logger)
    {
        _mediator = mediator;
        _domainGrants = domainGrants;
        _domainRegistry = domainRegistry;
        _text = text;
        _logger = logger;
    }

    /// <summary>
    /// Submit a legal question. The system will retrieve evidence from indexed documents
    /// and generate a citation-backed answer.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(AskResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Ask([FromBody] AskRequest request, CancellationToken ct)
    {
        var language = _text.ResolveLanguage(HttpContext, request.Language);

        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = _text.T("QuestionRequired", language) });

        var resolvedUserId = HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? request.UserId;
        var resolvedDomainId = NormalizeDomain(request.DomainId) ?? _domainRegistry.ActiveDomainId;
        var resolvedDatasetScope = NormalizeScope(request.DatasetScope);

        if (!User.IsInRole("Admin"))
        {
            if (string.IsNullOrWhiteSpace(resolvedUserId))
            {
                return Unauthorized(new { error = _text.T("UserIdentityNotFound", language) });
            }

            var hasAccess = await _domainGrants.HasAccessAsync(
                resolvedUserId,
                resolvedDomainId,
                resolvedDatasetScope,
                ct);

            if (!hasAccess)
            {
                _logger.LogWarning(
                    "User {UserId} denied query access for domain {DomainId} dataset {DatasetScope}",
                    resolvedUserId,
                    resolvedDomainId,
                    resolvedDatasetScope ?? "*");

                return Forbid();
            }
        }

        var query = new AskLegalQuestionQuery
        {
            Question = request.Question,
            DomainId = resolvedDomainId,
            DatasetScope = resolvedDatasetScope,
            CaseNamespace = request.CaseNamespace,
            StrictMode = request.StrictMode ?? true,
            TopK = request.TopK ?? 10,
            UserId = resolvedUserId
        };

        var answer = await _mediator.Send(query, ct);

        return Ok(new AskResponse
        {
            Answer = answer.Answer,
            Citations = answer.Citations.Select(c => new CitationDto
            {
                Document = c.Document,
                Page = c.Page,
                Section = c.Section,
                Snippet = c.Snippet,
                ArticleReference = c.ArticleReference,
                CaseNumber = c.CaseNumber,
                SimilarityScore = c.SimilarityScore
            }).ToList(),
            ConfidenceScore = answer.ConfidenceScore,
            RetrievedChunksUsed = answer.RetrievedChunksUsed,
            RetrievalSimilarityAvg = answer.RetrievalSimilarityAvg,
            IsAbstention = answer.IsAbstention,
            AbstentionReason = answer.AbstentionReason,
            Warnings = answer.Warnings,
            GenerationLatencyMs = answer.GenerationLatencyMs,
            RetrievalLatencyMs = answer.RetrievalLatencyMs
        });
    }

    private static string? NormalizeDomain(string? domainId)
    {
        return string.IsNullOrWhiteSpace(domainId)
            ? null
            : domainId.Trim().ToLowerInvariant();
    }

    private static string? NormalizeScope(string? datasetScope)
    {
        return string.IsNullOrWhiteSpace(datasetScope)
            ? null
            : datasetScope.Trim();
    }
}

// Request/Response DTOs
public sealed class AskRequest
{
    public string Question { get; init; } = "";
    public string? Language { get; init; }
    public string? DomainId { get; init; }
    public string? DatasetScope { get; init; }
    public string? CaseNamespace { get; init; }
    public bool? StrictMode { get; init; }
    public int? TopK { get; init; }
    public string? UserId { get; init; }
}

public sealed class AskResponse
{
    public required string Answer { get; init; }
    public required List<CitationDto> Citations { get; init; }
    public double ConfidenceScore { get; init; }
    public int RetrievedChunksUsed { get; init; }
    public double RetrievalSimilarityAvg { get; init; }
    public bool IsAbstention { get; init; }
    public string? AbstentionReason { get; init; }
    public List<string> Warnings { get; init; } = [];
    public double GenerationLatencyMs { get; init; }
    public double RetrievalLatencyMs { get; init; }
}

public sealed class CitationDto
{
    public required string Document { get; init; }
    public int Page { get; init; }
    public string? Section { get; init; }
    public required string Snippet { get; init; }
    public string? ArticleReference { get; init; }
    public string? CaseNumber { get; init; }
    public double SimilarityScore { get; init; }
}

