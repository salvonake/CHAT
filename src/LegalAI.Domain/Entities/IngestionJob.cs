namespace LegalAI.Domain.Entities;

public enum IngestionJobStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Quarantined = 4
}

public sealed class IngestionJob
{
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public required string ContentHash { get; init; }
    public IngestionJobStatus Status { get; set; } = IngestionJobStatus.Queued;
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public string? LastError { get; set; }
    public string? QuarantinePath { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
}
