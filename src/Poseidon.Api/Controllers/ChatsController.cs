using System.Security.Claims;
using System.Text.Json;
using Poseidon.Api.Localization;
using Poseidon.Application.Queries;
using Poseidon.Domain.Entities;
using Poseidon.Domain.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Poseidon.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class ChatsController : ControllerBase
{
    private readonly IChatStore _chatStore;
    private readonly IMediator _mediator;
    private readonly IUserDomainGrantStore _domainGrants;
    private readonly IDomainModuleRegistry _domainRegistry;
    private readonly IAuditService _audit;
    private readonly ApiTextLocalizer _text;

    public ChatsController(
        IChatStore chatStore,
        IMediator mediator,
        IUserDomainGrantStore domainGrants,
        IDomainModuleRegistry domainRegistry,
        IAuditService audit,
        ApiTextLocalizer text)
    {
        _chatStore = chatStore;
        _mediator = mediator;
        _domainGrants = domainGrants;
        _domainRegistry = domainRegistry;
        _audit = audit;
        _text = text;
    }

    [HttpGet]
    public async Task<IActionResult> GetChats([FromQuery] bool includeArchived = false, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var chats = await _chatStore.GetSessionsForUserAsync(userId, includeArchived, ct);

        return Ok(chats.Select(c => new ChatSessionDto
        {
            Id = c.Id,
            Title = c.Title,
            DomainId = c.DomainId,
            DatasetScope = c.DatasetScope,
            CaseNamespace = c.CaseNamespace,
            StrictMode = c.StrictMode,
            TopK = c.TopK,
            IsArchived = c.IsArchived,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt
        }));
    }

    [HttpPost]
    public async Task<IActionResult> CreateChat([FromBody] CreateChatRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        var title = string.IsNullOrWhiteSpace(request.Title)
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "Discussion {0:yyyy-MM-dd HH:mm}", DateTimeOffset.UtcNow)
            : request.Title.Trim();

        var resolvedDomainId = NormalizeDomain(request.DomainId) ?? _domainRegistry.ActiveDomainId;
        var resolvedDatasetScope = NormalizeScope(request.DatasetScope);

        if (!User.IsInRole("Admin"))
        {
            var hasAccess = await _domainGrants.HasAccessAsync(
                userId,
                resolvedDomainId,
                resolvedDatasetScope,
                ct);

            if (!hasAccess)
            {
                return Forbid();
            }
        }

        var session = new ChatSession
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Title = title,
            DomainId = resolvedDomainId,
            DatasetScope = resolvedDatasetScope,
            CaseNamespace = request.CaseNamespace,
            StrictMode = request.StrictMode ?? true,
            TopK = request.TopK ?? 10,
            IsArchived = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _chatStore.CreateSessionAsync(session, ct);
        await _audit.LogAsync("CHAT_CREATED", $"Chat {session.Id} created", userId, ct);

        return Ok(new { session.Id, session.Title, session.DomainId, session.DatasetScope });
    }

    [HttpPatch("{sessionId}/rename")]
    public async Task<IActionResult> RenameChat(string sessionId, [FromBody] RenameChatRequest request, CancellationToken ct)
    {
        var language = _text.ResolveLanguage(HttpContext);

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new { error = _text.T("TitleRequired", language) });
        }

        var session = await RequireOwnedSession(sessionId, ct);
        if (session is null)
        {
            return NotFound(new { error = _text.T("ChatSessionNotFound", language) });
        }

        await _chatStore.RenameSessionAsync(sessionId, request.Title.Trim(), ct);
        await _audit.LogAsync("CHAT_RENAMED", $"Chat {sessionId} renamed", GetUserId(), ct);
        return Ok(new { message = _text.T("ChatRenamed", language) });
    }

    [HttpPost("{sessionId}/archive")]
    public async Task<IActionResult> ArchiveChat(string sessionId, [FromBody] ArchiveChatRequest request, CancellationToken ct)
    {
        var language = _text.ResolveLanguage(HttpContext);

        var session = await RequireOwnedSession(sessionId, ct);
        if (session is null)
        {
            return NotFound(new { error = _text.T("ChatSessionNotFound", language) });
        }

        await _chatStore.ArchiveSessionAsync(sessionId, request.Archived, ct);
        await _audit.LogAsync(
            request.Archived ? "CHAT_ARCHIVED" : "CHAT_UNARCHIVED",
            $"Chat {sessionId} archived={request.Archived}",
            GetUserId(),
            ct);

        return Ok(new { message = _text.T("ChatArchiveStateUpdated", language) });
    }

    [HttpGet("{sessionId}/messages")]
    public async Task<IActionResult> GetMessages(string sessionId, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var language = _text.ResolveLanguage(HttpContext);

        var session = await RequireOwnedSession(sessionId, ct);
        if (session is null)
        {
            return NotFound(new { error = _text.T("ChatSessionNotFound", language) });
        }

        var messages = await _chatStore.GetMessagesAsync(sessionId, limit, ct);
        return Ok(messages.Select(m => new ChatMessageDto
        {
            Id = m.Id,
            Role = m.Role,
            Content = m.Content,
            MetadataJson = m.MetadataJson,
            CreatedAt = m.CreatedAt
        }));
    }

    [HttpPost("{sessionId}/ask")]
    [Authorize(Policy = "CanQuery")]
    public async Task<IActionResult> Ask(string sessionId, [FromBody] ChatAskRequest request, CancellationToken ct)
    {
        var language = _text.ResolveLanguage(HttpContext, request.Language);

        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return BadRequest(new { error = _text.T("QuestionRequired", language) });
        }

        var session = await RequireOwnedSession(sessionId, ct);
        if (session is null)
        {
            return NotFound(new { error = _text.T("ChatSessionNotFound", language) });
        }

        var userId = GetUserId();
        var resolvedDomainId = NormalizeDomain(request.DomainId) ?? session.DomainId ?? _domainRegistry.ActiveDomainId;
        var resolvedDatasetScope = NormalizeScope(request.DatasetScope) ?? session.DatasetScope;

        if (!User.IsInRole("Admin"))
        {
            var hasAccess = await _domainGrants.HasAccessAsync(
                userId,
                resolvedDomainId,
                resolvedDatasetScope,
                ct);

            if (!hasAccess)
            {
                return Forbid();
            }
        }

        await _chatStore.AddMessageAsync(new ChatMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            UserId = userId,
            Role = "user",
            Content = request.Question.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);

        var answer = await _mediator.Send(new AskLegalQuestionQuery
        {
            Question = request.Question.Trim(),
            DomainId = resolvedDomainId,
            DatasetScope = resolvedDatasetScope,
            CaseNamespace = request.CaseNamespace ?? session.CaseNamespace,
            StrictMode = request.StrictMode ?? session.StrictMode,
            TopK = request.TopK ?? session.TopK,
            UserId = userId
        }, ct);

        var metadata = JsonSerializer.Serialize(new
        {
            answer.ConfidenceScore,
            answer.RetrievedChunksUsed,
            answer.RetrievalSimilarityAvg,
            answer.IsAbstention,
            answer.AbstentionReason,
            answer.GenerationLatencyMs,
            answer.RetrievalLatencyMs,
            CitationCount = answer.Citations.Count
        });

        await _chatStore.AddMessageAsync(new ChatMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            UserId = userId,
            Role = "assistant",
            Content = answer.Answer,
            MetadataJson = metadata,
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);

        await _audit.LogAsync("CHAT_ASK", $"Chat ask in session {sessionId}", userId, ct);

        return Ok(new
        {
            answer.Answer,
            answer.Citations,
            answer.ConfidenceScore,
            answer.IsAbstention,
            answer.AbstentionReason,
            answer.Warnings,
            answer.GenerationLatencyMs,
            answer.RetrievalLatencyMs
        });
    }

    private async Task<ChatSession?> RequireOwnedSession(string sessionId, CancellationToken ct)
    {
        var session = await _chatStore.GetSessionAsync(sessionId, ct);
        if (session is null)
        {
            return null;
        }

        var userId = GetUserId();
        var role = User.FindFirstValue(ClaimTypes.Role);
        var isAdmin = string.Equals(role, UserRole.Admin.ToString(), StringComparison.OrdinalIgnoreCase);

        if (!isAdmin && !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return null;
        }

        return session;
    }

    private string GetUserId()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new UnauthorizedAccessException(_text.T("MissingUserIdentity", _text.ResolveLanguage(HttpContext)));
        }

        return userId;
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

public sealed class CreateChatRequest
{
    public string? Title { get; init; }
    public string? DomainId { get; init; }
    public string? DatasetScope { get; init; }
    public string? CaseNamespace { get; init; }
    public bool? StrictMode { get; init; }
    public int? TopK { get; init; }
}

public sealed class RenameChatRequest
{
    public string Title { get; init; } = "";
}

public sealed class ArchiveChatRequest
{
    public bool Archived { get; init; } = true;
}

public sealed class ChatAskRequest
{
    public string Question { get; init; } = "";
    public string? Language { get; init; }
    public string? DomainId { get; init; }
    public string? DatasetScope { get; init; }
    public string? CaseNamespace { get; init; }
    public bool? StrictMode { get; init; }
    public int? TopK { get; init; }
}

public sealed class ChatSessionDto
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? DomainId { get; init; }
    public string? DatasetScope { get; init; }
    public string? CaseNamespace { get; init; }
    public bool StrictMode { get; init; }
    public int TopK { get; init; }
    public bool IsArchived { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class ChatMessageDto
{
    public required string Id { get; init; }
    public required string Role { get; init; }
    public required string Content { get; init; }
    public string? MetadataJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

