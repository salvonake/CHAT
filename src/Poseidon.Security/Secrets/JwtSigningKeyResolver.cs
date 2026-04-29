using Microsoft.Extensions.Configuration;
using Poseidon.Security.Configuration;

namespace Poseidon.Security.Secrets;

public sealed record JwtSigningKeyMaterial(
    ResolvedSecret Primary,
    ResolvedSecret? Secondary,
    string PrimaryKeyVersion,
    string? SecondaryKeyVersion)
{
    public IEnumerable<ResolvedSecret> AllKeys
    {
        get
        {
            yield return Primary;
            if (Secondary is not null)
                yield return Secondary;
        }
    }
}

public static class JwtSigningKeyResolver
{
    public static JwtSigningKeyMaterial Resolve(IConfiguration config, SecurityValidationContext context)
    {
        var primary = ConfigurationSecretResolver.ResolveRequiredSecret(
            config,
            context,
            "Auth:Jwt:SigningKey",
            "Auth:Jwt:PrimarySigningKeyRef",
            32);

        var secondary = ConfigurationSecretResolver.ResolveOptionalSecret(
            config,
            context,
            "Auth:Jwt:SecondarySigningKey",
            "Auth:Jwt:SecondarySigningKeyRef",
            32);

        return new JwtSigningKeyMaterial(
            primary,
            secondary,
            config["Auth:Jwt:PrimaryKeyVersion"] ?? primary.Version ?? "primary",
            config["Auth:Jwt:SecondaryKeyVersion"] ?? secondary?.Version);
    }
}
