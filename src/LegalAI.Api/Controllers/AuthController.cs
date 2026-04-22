using System.Security.Claims;
using LegalAI.Api.Security;
using LegalAI.Domain.Entities;
using LegalAI.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LegalAI.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AuthController : ControllerBase
{
    private readonly IUserStore _users;
    private readonly IUserDomainGrantStore _domainGrants;
    private readonly IJwtTokenService _tokens;
    private readonly IAuditService _audit;

    public AuthController(
        IUserStore users,
        IUserDomainGrantStore domainGrants,
        IJwtTokenService tokens,
        IAuditService audit)
    {
        _users = users;
        _domainGrants = domainGrants;
        _tokens = tokens;
        _audit = audit;
    }

    [HttpPost("bootstrap-admin")]
    [AllowAnonymous]
    public async Task<IActionResult> BootstrapAdmin([FromBody] BootstrapAdminRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { error = "Username and password are required." });
        }

        var count = await _users.GetCountAsync(ct);
        if (count > 0)
        {
            return Conflict(new { error = "Bootstrap is only allowed when no users exist." });
        }

        var user = new UserAccount
        {
            Id = Guid.NewGuid().ToString("N"),
            Username = request.Username.Trim().ToLowerInvariant(),
            PasswordHash = PasswordHashingService.HashPassword(request.Password),
            Role = UserRole.Admin,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _users.UpsertAsync(user, ct);
        await _audit.LogAsync("ADMIN_BOOTSTRAPPED", $"Admin user created: {user.Username}", user.Id, ct);

        return Ok(new { message = "Admin user created." });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { error = "Username and password are required." });
        }

        var username = request.Username.Trim().ToLowerInvariant();
        var user = await _users.GetByUsernameAsync(username, ct);
        if (user is null || user.IsDisabled)
        {
            await _audit.LogAsync("LOGIN_DENIED", $"Unknown or disabled user login attempt: {username}", username, ct);
            return Unauthorized(new { error = "Invalid credentials." });
        }

        if (!PasswordHashingService.VerifyPassword(request.Password, user.PasswordHash))
        {
            await _audit.LogAsync("LOGIN_DENIED", $"Invalid password for user: {username}", user.Id, ct);
            return Unauthorized(new { error = "Invalid credentials." });
        }

        var now = DateTimeOffset.UtcNow;
        await _users.SetLastLoginAsync(user.Id, now, ct);
        await _audit.LogAsync("LOGIN_SUCCESS", $"User login: {user.Username}", user.Id, ct);

        var grants = await _domainGrants.GetForUserAsync(user.Id, ct);
        var token = _tokens.CreateToken(user, grants);
        return Ok(new LoginResponse
        {
            AccessToken = token,
            ExpiresAt = _tokens.GetExpiration(),
            UserId = user.Id,
            Username = user.Username,
            Role = user.Role.ToString(),
            DomainGrants = grants.Select(MapGrant).ToList()
        });
    }

    [HttpGet("users")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetUsers(CancellationToken ct)
    {
        var users = await _users.GetAllAsync(ct);
        return Ok(users.Select(u => new UserDto
        {
            Id = u.Id,
            Username = u.Username,
            Role = u.Role.ToString(),
            IsDisabled = u.IsDisabled,
            CreatedAt = u.CreatedAt,
            LastLoginAt = u.LastLoginAt
        }));
    }

    [HttpPost("users")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { error = "Username and password are required." });
        }

        if (!Enum.TryParse<UserRole>(request.Role, true, out var parsedRole))
        {
            return BadRequest(new { error = "Invalid role. Use Admin, Analyst, or Viewer." });
        }

        var username = request.Username.Trim().ToLowerInvariant();
        var existing = await _users.GetByUsernameAsync(username, ct);
        if (existing is not null)
        {
            return Conflict(new { error = "Username already exists." });
        }

        var user = new UserAccount
        {
            Id = Guid.NewGuid().ToString("N"),
            Username = username,
            PasswordHash = PasswordHashingService.HashPassword(request.Password),
            Role = parsedRole,
            IsDisabled = false,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _users.UpsertAsync(user, ct);

        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _audit.LogAsync("USER_CREATED", $"User {user.Username} created with role {user.Role}", actorId, ct);

        return CreatedAtAction(nameof(GetUsers), new { id = user.Id }, new { user.Id, user.Username, Role = user.Role.ToString() });
    }

    [HttpPost("users/{id}/disable")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DisableUser(string id, [FromBody] DisableUserRequest request, CancellationToken ct)
    {
        var existing = await _users.GetByIdAsync(id, ct);
        if (existing is null)
        {
            return NotFound(new { error = "User not found." });
        }

        await _users.SetDisabledAsync(id, request.Disabled, ct);

        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _audit.LogAsync(
            request.Disabled ? "USER_DISABLED" : "USER_ENABLED",
            $"User {existing.Username} set disabled={request.Disabled}",
            actorId,
            ct);

        return Ok(new { message = "User status updated." });
    }

    [HttpGet("users/{id}/domain-grants")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetUserDomainGrants(string id, CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(id, ct);
        if (user is null)
        {
            return NotFound(new { error = "User not found." });
        }

        var grants = await _domainGrants.GetForUserAsync(id, ct);
        return Ok(grants.Select(MapGrant));
    }

    [HttpPost("users/{id}/domain-grants")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GrantUserDomainAccess(
        string id,
        [FromBody] GrantDomainAccessRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DomainId))
        {
            return BadRequest(new { error = "DomainId is required." });
        }

        var user = await _users.GetByIdAsync(id, ct);
        if (user is null)
        {
            return NotFound(new { error = "User not found." });
        }

        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var grant = new UserDomainGrant
        {
            UserId = id,
            DomainId = request.DomainId.Trim().ToLowerInvariant(),
            DatasetScope = NormalizeScope(request.DatasetScope),
            GrantedAt = DateTimeOffset.UtcNow,
            GrantedBy = actorId
        };

        await _domainGrants.UpsertAsync(grant, ct);
        await _audit.LogAsync(
            "USER_DOMAIN_GRANTED",
            $"Granted {grant.DomainId}:{grant.DatasetScope ?? "*"} to user {user.Username}",
            actorId,
            ct);

        return Ok(MapGrant(grant));
    }

    [HttpDelete("users/{id}/domain-grants")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> RevokeUserDomainAccess(
        string id,
        [FromQuery] string domainId,
        [FromQuery] string? datasetScope,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(domainId))
        {
            return BadRequest(new { error = "domainId is required." });
        }

        var user = await _users.GetByIdAsync(id, ct);
        if (user is null)
        {
            return NotFound(new { error = "User not found." });
        }

        var normalizedDomainId = domainId.Trim().ToLowerInvariant();
        var normalizedScope = NormalizeScope(datasetScope);

        await _domainGrants.RemoveAsync(id, normalizedDomainId, normalizedScope, ct);

        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await _audit.LogAsync(
            "USER_DOMAIN_REVOKED",
            $"Revoked {normalizedDomainId}:{normalizedScope ?? "*"} from user {user.Username}",
            actorId,
            ct);

        return Ok(new { message = "Domain access revoked." });
    }

    [HttpGet("me/domain-grants")]
    [Authorize]
    public async Task<IActionResult> GetMyDomainGrants(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new { error = "User identity not found." });
        }

        var grants = await _domainGrants.GetForUserAsync(userId, ct);
        return Ok(grants.Select(MapGrant));
    }

    private static DomainGrantDto MapGrant(UserDomainGrant grant)
    {
        return new DomainGrantDto
        {
            DomainId = grant.DomainId,
            DatasetScope = grant.DatasetScope,
            GrantedAt = grant.GrantedAt,
            GrantedBy = grant.GrantedBy
        };
    }

    private static string? NormalizeScope(string? datasetScope)
    {
        return string.IsNullOrWhiteSpace(datasetScope)
            ? null
            : datasetScope.Trim().ToLowerInvariant();
    }
}

public sealed class BootstrapAdminRequest
{
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
}

public sealed class LoginRequest
{
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
}

public sealed class LoginResponse
{
    public required string AccessToken { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string UserId { get; init; }
    public required string Username { get; init; }
    public required string Role { get; init; }
    public List<DomainGrantDto> DomainGrants { get; init; } = [];
}

public sealed class CreateUserRequest
{
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string Role { get; init; } = "Viewer";
}

public sealed class DisableUserRequest
{
    public bool Disabled { get; init; } = true;
}

public sealed class GrantDomainAccessRequest
{
    public string DomainId { get; init; } = "";
    public string? DatasetScope { get; init; }
}

public sealed class DomainGrantDto
{
    public required string DomainId { get; init; }
    public string? DatasetScope { get; init; }
    public DateTimeOffset GrantedAt { get; init; }
    public string? GrantedBy { get; init; }
}

public sealed class UserDto
{
    public required string Id { get; init; }
    public required string Username { get; init; }
    public required string Role { get; init; }
    public bool IsDisabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastLoginAt { get; init; }
}
