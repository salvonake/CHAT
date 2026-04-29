namespace Poseidon.Domain.Entities;

public sealed class UserDomainGrant
{
    public required string UserId { get; init; }
    public required string DomainId { get; init; }
    public string? DatasetScope { get; init; }
    public DateTimeOffset GrantedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? GrantedBy { get; init; }
}

