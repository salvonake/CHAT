using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Poseidon.Domain.Entities;
using Poseidon.Security.Configuration;
using Poseidon.Security.Secrets;
using Microsoft.IdentityModel.Tokens;

namespace Poseidon.Api.Security;

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
    private readonly string _keyVersion;

    public JwtTokenService(IConfiguration configuration)
    {
        var context = SecurityValidationContext.FromConfiguration(configuration);
        SecurityConfigurationValidator.ValidateJwt(configuration, context);
        var keyMaterial = JwtSigningKeyResolver.Resolve(configuration, context);

        _issuer = configuration["Auth:Jwt:Issuer"] ?? "Poseidon";
        _audience = configuration["Auth:Jwt:Audience"] ?? "Poseidon.Client";
        _expiryMinutes = configuration.GetValue("Auth:Jwt:ExpiryMinutes", 120);
        _keyVersion = keyMaterial.PrimaryKeyVersion;
        _securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keyMaterial.Primary.Value))
        {
            KeyId = _keyVersion
        };
    }

    public string CreateToken(UserAccount user, IReadOnlyList<UserDomainGrant>? domainGrants = null)
    {
        var credentials = new SigningCredentials(_securityKey, SecurityAlgorithms.HmacSha256)
        {
            CryptoProviderFactory = _securityKey.CryptoProviderFactory
        };
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
        token.Header["kid"] = _keyVersion;

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public DateTimeOffset GetExpiration()
    {
        return DateTimeOffset.UtcNow.AddMinutes(_expiryMinutes);
    }
}

