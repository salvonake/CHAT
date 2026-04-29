namespace Poseidon.Domain.Entities;

public enum DatasetLifecycle
{
    Draft = 0,
    Active = 1,
    Frozen = 2,
    Archived = 3
}

public enum DatasetSensitivity
{
    Internal = 0,
    Confidential = 1,
    Restricted = 2
}

public sealed class Dataset
{
    public required string Id { get; init; }
    public required string DomainId { get; init; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DatasetLifecycle Lifecycle { get; set; } = DatasetLifecycle.Active;
    public DatasetSensitivity Sensitivity { get; set; } = DatasetSensitivity.Internal;
    public string? OwnerUserId { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

