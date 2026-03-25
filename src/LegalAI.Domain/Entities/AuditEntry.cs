namespace LegalAI.Domain.Entities;

/// <summary>
/// An entry in the append-only, HMAC-signed audit log.
/// </summary>
public sealed class AuditEntry
{
    public long Id { get; init; }
    public required string Action { get; init; }
    public string? UserId { get; set; }
    public required string Details { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public required string PreviousHash { get; init; }
    public required string Hmac { get; init; }
}

/// <summary>
/// Quarantine record for documents that repeatedly fail ingestion.
/// </summary>
public sealed class QuarantineRecord
{
    public required string DocumentId { get; init; }
    public required string FilePath { get; init; }
    public required string Reason { get; init; }
    public int FailureCount { get; init; }
    public DateTimeOffset QuarantinedAt { get; init; } = DateTimeOffset.UtcNow;
    public required string ContentHash { get; init; }
}
