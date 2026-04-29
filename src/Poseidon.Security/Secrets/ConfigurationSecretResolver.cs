using Microsoft.Extensions.Configuration;
using Poseidon.Security.Configuration;

namespace Poseidon.Security.Secrets;

public sealed record ResolvedSecret(
    string Value,
    string Source,
    string? Reference,
    string? Version);

public static class ConfigurationSecretResolver
{
    public static ResolvedSecret ResolveRequiredSecret(
        IConfiguration config,
        SecurityValidationContext context,
        string plaintextKey,
        string referenceKey,
        int minimumBytes)
    {
        var reference = config[referenceKey];
        if (!string.IsNullOrWhiteSpace(reference))
        {
            var loaded = ProtectedSecretStore.Load(reference);
            if (!loaded.Succeeded)
                throw new InvalidOperationException($"{referenceKey} could not be resolved: {loaded.Status}.");

            SecurityConfigurationValidator.ValidateSharedKey(
                loaded.Value,
                referenceKey,
                context,
                minimumBytes);

            return new ResolvedSecret(
                loaded.Value!,
                "protected",
                loaded.Reference,
                TryGetReferenceVersion(loaded.Reference));
        }

        var plaintext = config[plaintextKey];
        if (!string.IsNullOrWhiteSpace(plaintext))
        {
            if (!context.AllowsInsecureDevelopment)
                throw new InvalidOperationException($"{plaintextKey} cannot contain plaintext secrets outside explicit insecure Development mode. Use {referenceKey}.");

            SecurityConfigurationValidator.ValidateSharedKey(plaintext, plaintextKey, context, minimumBytes);
            return new ResolvedSecret(plaintext, "plaintext-development", null, null);
        }

        if (context.AllowsInsecureDevelopment)
            return new ResolvedSecret("", "missing-development", null, null);

        throw new InvalidOperationException($"{referenceKey} is required outside explicit insecure Development mode.");
    }

    public static ResolvedSecret? ResolveOptionalSecret(
        IConfiguration config,
        SecurityValidationContext context,
        string plaintextKey,
        string referenceKey,
        int minimumBytes)
    {
        var reference = config[referenceKey];
        var plaintext = config[plaintextKey];
        if (string.IsNullOrWhiteSpace(reference) && string.IsNullOrWhiteSpace(plaintext))
            return null;

        return ResolveRequiredSecret(config, context, plaintextKey, referenceKey, minimumBytes);
    }

    public static bool HasSecretReference(IConfiguration config, string referenceKey)
        => !string.IsNullOrWhiteSpace(config[referenceKey]);

    public static string DescribeSecretState(IConfiguration config, string plaintextKey, string referenceKey)
    {
        if (!string.IsNullOrWhiteSpace(config[referenceKey]))
        {
            var loaded = ProtectedSecretStore.Load(config[referenceKey]);
            return loaded.Status switch
            {
                ProtectedSecretLoadStatus.Loaded => "present",
                ProtectedSecretLoadStatus.MissingSecret => "missing",
                ProtectedSecretLoadStatus.CorruptSecret => "corrupt",
                ProtectedSecretLoadStatus.InvalidReference => "invalid-reference",
                ProtectedSecretLoadStatus.UnsupportedPlatform => "unsupported-platform",
                ProtectedSecretLoadStatus.UnsupportedProvider => "unsupported-provider",
                _ => "missing-reference"
            };
        }

        return string.IsNullOrWhiteSpace(config[plaintextKey])
            ? "missing"
            : "plaintext";
    }

    private static string? TryGetReferenceVersion(string? reference)
    {
        if (ProtectedSecretReference.TryParse(reference, out var parsed))
            return parsed.Version;

        return null;
    }
}
