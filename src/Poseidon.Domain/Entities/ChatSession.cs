namespace Poseidon.Domain.Entities;

public sealed class ChatSession
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string Title { get; set; }
    public string? DomainId { get; set; }
    public string? DatasetScope { get; set; }
    public string? CaseNamespace { get; set; }
    public bool StrictMode { get; set; } = true;
    public int TopK { get; set; } = 10;
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ChatMessage
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public required string Role { get; init; }
    public required string Content { get; init; }
    public string? MetadataJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

