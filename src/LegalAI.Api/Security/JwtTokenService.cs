using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LegalAI.Domain.Entities;
using Microsoft.IdentityModel.Tokens;

namespace LegalAI.Api.Security;

public interface IJwtTokenService
{
    string CreateToken(UserAccount user, IReadOnlyList<UserDomainGrant>? domainGrants = null);
    DateTimeOffset GetExpiration();
}

public sealed class JwtTokenService : IJwtTokenService
{
    private readonly SymmetricSecurityKey _securityKey;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _expiryMinutes;

    public JwtTokenService(IConfiguration configuration)
    {
        _issuer = configuration["Auth:Jwt:Issuer"] ?? "LegalAI";
        _audience = configuration["Auth:Jwt:Audience"] ?? "LegalAI.Client";
        _expiryMinutes = configuration.GetValue("Auth:Jwt:ExpiryMinutes", 120);

        var signingKey = configuration["Auth:Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey) || signingKey.Length < 32)
        {
            throw new InvalidOperationException(
                "Auth:Jwt:SigningKey must be configured and at least 32 characters long.");
        }

        _securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
    }

    public string CreateToken(UserAccount user, IReadOnlyList<UserDomainGrant>? domainGrants = null)
    {
        var credentials = new SigningCredentials(_securityKey, SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role.ToString())
        };

        if (domainGrants is not null)
        {
            foreach (var grant in domainGrants)
            {
                var normalizedDomain = grant.DomainId.Trim().ToLowerInvariant();
                var scopeValue = string.IsNullOrWhiteSpace(grant.DatasetScope)
                    ? normalizedDomain
                    : $"{normalizedDomain}:{grant.DatasetScope.Trim().ToLowerInvariant()}";

                claims.Add(new Claim("akp_scope", scopeValue));
            }
        }

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(_expiryMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public DateTimeOffset GetExpiration()
    {
        return DateTimeOffset.UtcNow.AddMinutes(_expiryMinutes);
    }
}
