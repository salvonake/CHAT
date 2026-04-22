namespace LegalAI.Domain.Entities;

public enum UserRole
{
    Viewer = 0,
    Analyst = 1,
    Admin = 2
}

public sealed class UserAccount
{
    public required string Id { get; init; }
    public required string Username { get; init; }
    public required string PasswordHash { get; init; }
    public UserRole Role { get; init; } = UserRole.Viewer;
    public bool IsDisabled { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}
