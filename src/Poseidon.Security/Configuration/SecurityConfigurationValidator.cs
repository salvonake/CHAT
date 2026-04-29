using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Poseidon.Security.Secrets;

namespace Poseidon.Security.Configuration;

public enum PoseidonRuntimeComponent
{
    Desktop,
    Api,
    Worker,
    ManagementApi,
    ProvisioningCheck
}

public sealed record SecurityValidationContext(
    string EnvironmentName,
    bool AllowInsecureDevelopmentSecrets)
{
    public bool IsDevelopment => string.Equals(EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);
    public bool AllowsInsecureDevelopment => IsDevelopment && AllowInsecureDevelopmentSecrets;
    public bool IsProductionLike => !IsDevelopment;

    public static SecurityValidationContext FromConfiguration(IConfiguration config, string? environmentName = null)
    {
        var resolvedEnvironment =
            environmentName ??
            config["ASPNETCORE_ENVIRONMENT"] ??
            config["DOTNET_ENVIRONMENT"] ??
            config["Instance:Environment"] ??
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
            "Production";

        return new SecurityValidationContext(
            resolvedEnvironment,
            bool.TryParse(config["Security:AllowInsecureDevelopmentSecrets"], out var allowInsecure) && allowInsecure);
    }
}

public static class SecurityConfigurationValidator
{
    private static readonly string[] PlaceholderFragments =
    [
        "change_this",
        "change_me",
        "changeme",
        "change-me",
        "poseidon_dev",
        "dev_signing_key",
        "placeholder",
        "your_secret",
        "your-secret",
        "password",
        "secret",
        "test-key",
        "dev_local",
        "dev-local"
    ];

    private static readonly HashSet<string> AllowedUserTopLevelSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ui",
        "Ingestion",
        "Diagnostics",
        "Logging",
        "Serilog"
    };

    public static void ValidateApi(IConfiguration config, string? environmentName = null)
    {
        var context = SecurityValidationContext.FromConfiguration(config, environmentName);
        ValidateJwt(config, context);
        ValidateRuntimeConfiguration(config, context, PoseidonRuntimeComponent.Api);
    }

    public static void ValidateWorker(IConfiguration config, string? environmentName = null)
    {
        var context = SecurityValidationContext.FromConfiguration(config, environmentName);
        ValidateRuntimeConfiguration(config, context, PoseidonRuntimeComponent.Worker);
    }

    public static void ValidateDesktop(IConfiguration config, string? environmentName = null)
    {
        var context = SecurityValidationContext.FromConfiguration(config, environmentName);
        ValidateRuntimeConfiguration(config, context, PoseidonRuntimeComponent.Desktop);
    }

    public static void ValidateManagementApi(IConfiguration config, string? environmentName = null)
    {
        var context = SecurityValidationContext.FromConfiguration(config, environmentName);
        ValidateProtectedOrDevelopmentSecret(config, context, "Security:ManagementApiKey", "Security:ManagementApiKeyRef");
        ValidateOptionalProtectedOrDevelopmentSecret(config, context, "Security:SecondaryManagementApiKey", "Security:SecondaryManagementApiKeyRef");
        ValidateProtectedOrDevelopmentSecret(config, context, "Security:AgentApiKey", "Security:AgentApiKeyRef");
        ValidateOptionalProtectedOrDevelopmentSecret(config, context, "Security:SecondaryAgentApiKey", "Security:SecondaryAgentApiKeyRef");
    }

    public static void ValidateProvisioning(
        IConfiguration config,
        string mode,
        string? environmentName = null,
        bool allowDeferredSecrets = false)
    {
        var context = SecurityValidationContext.FromConfiguration(config, environmentName);
        ValidateProviderConfiguration(config);
        ValidateMode(config, mode);
        ValidateLocalModelHashes(config, context);
        ValidateStrictMode(config);
        ValidateProvisioningSecretContract(config, context, allowDeferredSecrets);
    }

    public static void ValidateJwt(IConfiguration config, SecurityValidationContext? context = null)
    {
        context ??= SecurityValidationContext.FromConfiguration(config);

        var issuer = config["Auth:Jwt:Issuer"];
        var audience = config["Auth:Jwt:Audience"];
        if (context.IsProductionLike)
        {
            RequireNonPlaceholderValue(issuer, "Auth:Jwt:Issuer", context, 3);
            RequireNonPlaceholderValue(audience, "Auth:Jwt:Audience", context, 3);
        }

        JwtSigningKeyResolver.Resolve(config, context);
    }

    public static void ValidateSharedKey(
        string? value,
        string settingName,
        SecurityValidationContext context,
        int minimumBytes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (context.AllowsInsecureDevelopment)
                return;

            throw new InvalidOperationException($"{settingName} is required outside explicit insecure Development mode.");
        }

        if (context.AllowsInsecureDevelopment)
            return;

        RequireNonPlaceholderValue(value, settingName, context, minimumBytes);
        if (EstimateSecretBytes(value) < minimumBytes)
            throw new InvalidOperationException($"{settingName} must contain at least {minimumBytes} bytes of key material.");

        if (LooksLowEntropy(value))
            throw new InvalidOperationException($"{settingName} appears to be low entropy.");
    }

    public static string? ValidateUserConfigTrustBoundary(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return "User configuration root must be a JSON object.";

            foreach (var section in root.EnumerateObject())
            {
                if (!AllowedUserTopLevelSections.Contains(section.Name))
                    return $"User configuration may not override trusted section '{section.Name}'.";

                if (section.NameEquals("Retrieval"))
                    return "User configuration may not override retrieval security policy.";
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static void ValidateRuntimeConfiguration(
        IConfiguration config,
        SecurityValidationContext context,
        PoseidonRuntimeComponent component)
    {
        if (component is not PoseidonRuntimeComponent.ManagementApi)
        {
            ValidateProviderConfiguration(config);
            ValidateStrictMode(config);
            ValidateLocalModelHashes(config, context);
            ValidateEncryptionPolicy(config, context, component);
        }
    }

    public static void ValidateProviderConfiguration(IConfiguration config)
    {
        var llmProvider = Normalize(config["Llm:Provider"], "ollama");
        var embeddingProvider = Normalize(config["Embedding:Provider"], "ollama");

        if (llmProvider is not ("ollama" or "llamasharp"))
            throw new InvalidOperationException($"Unsupported LLM provider: {llmProvider}");

        if (embeddingProvider is not ("ollama" or "onnx"))
            throw new InvalidOperationException($"Unsupported embedding provider: {embeddingProvider}");

        if (llmProvider == "ollama")
        {
            RequireHttpUrl(config["Ollama:Url"], "Ollama:Url");
            RequireValue(config["Ollama:Model"], "Ollama:Model");
        }
        else
        {
            RequireValue(config["Llm:ModelPath"], "Llm:ModelPath");
        }

        if (embeddingProvider == "ollama")
        {
            RequireHttpUrl(config["Ollama:Url"], "Ollama:Url");
            RequireValue(config["Embedding:Model"], "Embedding:Model");
        }
        else
        {
            RequireValue(config["Embedding:OnnxModelPath"], "Embedding:OnnxModelPath");
        }
    }

    public static void ValidateMode(IConfiguration config, string mode)
    {
        var normalizedMode = Normalize(mode, "full");
        if (normalizedMode is not ("full" or "degraded" or "recovery"))
            throw new InvalidOperationException($"Unsupported runtime mode: {mode}");

        var llmProvider = Normalize(config["Llm:Provider"], "ollama");
        if (normalizedMode == "full" && llmProvider != "llamasharp")
            throw new InvalidOperationException("Full mode requires a local llamasharp LLM provider.");

        if (normalizedMode == "degraded" && llmProvider == "llamasharp")
            throw new InvalidOperationException("Degraded mode must not declare a local LLM provider.");
    }

    public static void ValidateLocalModelHashes(IConfiguration config, SecurityValidationContext context)
    {
        if (!context.IsProductionLike)
            return;

        var llmProvider = Normalize(config["Llm:Provider"], "ollama");
        var embeddingProvider = Normalize(config["Embedding:Provider"], "ollama");

        if (llmProvider == "llamasharp")
            RequireSha256(config["ModelIntegrity:ExpectedLlmHash"], "ModelIntegrity:ExpectedLlmHash");

        if (embeddingProvider == "onnx")
            RequireSha256(config["ModelIntegrity:ExpectedEmbeddingHash"], "ModelIntegrity:ExpectedEmbeddingHash");
    }

    private static void ValidateStrictMode(IConfiguration config)
    {
        var value = config["Retrieval:StrictMode"];
        if (value is null)
            return;

        if (!bool.TryParse(value, out var strictMode) || !strictMode)
            throw new InvalidOperationException("Retrieval:StrictMode must be true when configured.");
    }

    private static void ValidateEncryptionPolicy(
        IConfiguration config,
        SecurityValidationContext context,
        PoseidonRuntimeComponent component)
    {
        if (!context.IsProductionLike)
            return;

        if (config.GetValue("Security:AllowUnencryptedStorage", false))
            return;

        if (!config.GetValue("Security:EncryptionEnabled", false))
            throw new InvalidOperationException($"{component} production configuration requires Security:EncryptionEnabled=true.");

        ValidateProtectedOrDevelopmentSecret(config, context, "Security:EncryptionPassphrase", "Security:EncryptionPassphraseRef");
    }

    private static void ValidateProvisioningSecretContract(
        IConfiguration config,
        SecurityValidationContext context,
        bool allowDeferredSecrets)
    {
        if (!config.GetValue("Security:EncryptionEnabled", false))
            throw new InvalidOperationException("Provisioned machine config requires Security:EncryptionEnabled=true.");

        var reference = config["Security:EncryptionPassphraseRef"];
        var plaintext = config["Security:EncryptionPassphrase"];
        if (!string.IsNullOrWhiteSpace(reference))
        {
            if (!ProtectedSecretReference.TryParse(reference, out _))
                throw new InvalidOperationException("Security:EncryptionPassphraseRef is invalid.");

            if (!allowDeferredSecrets)
                ValidateProtectedOrDevelopmentSecret(config, context, "Security:EncryptionPassphrase", "Security:EncryptionPassphraseRef");

            return;
        }

        if (!context.AllowsInsecureDevelopment)
            throw new InvalidOperationException("Security:EncryptionPassphraseRef is required for production provisioning.");

        ValidateSharedKey(plaintext, "Security:EncryptionPassphrase", context, 32);
    }

    private static void ValidateProtectedOrDevelopmentSecret(
        IConfiguration config,
        SecurityValidationContext context,
        string plaintextKey,
        string referenceKey)
    {
        ConfigurationSecretResolver.ResolveRequiredSecret(config, context, plaintextKey, referenceKey, 32);
    }

    private static void ValidateOptionalProtectedOrDevelopmentSecret(
        IConfiguration config,
        SecurityValidationContext context,
        string plaintextKey,
        string referenceKey)
    {
        ConfigurationSecretResolver.ResolveOptionalSecret(config, context, plaintextKey, referenceKey, 32);
    }

    private static void RequireNonPlaceholderValue(
        string? value,
        string settingName,
        SecurityValidationContext context,
        int minimumBytes)
    {
        RequireValue(value, settingName);

        var normalized = NormalizeForPlaceholderCheck(value!);
        if (PlaceholderFragments.Any(normalized.Contains))
            throw new InvalidOperationException($"{settingName} contains a placeholder or development value.");

        if (EstimateSecretBytes(value!) < minimumBytes && settingName.Contains("Key", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{settingName} must contain at least {minimumBytes} bytes of key material.");
    }

    private static void RequireValue(string? value, string settingName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{settingName} is required.");
    }

    private static void RequireSha256(string? value, string settingName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length != 64 ||
            value.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new InvalidOperationException($"{settingName} must be a SHA-256 hex digest.");
        }
    }

    private static void RequireHttpUrl(string? value, string settingName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"{settingName} must be a valid HTTP(S) URL.");
    }

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();

    private static string NormalizeForPlaceholderCheck(string value)
        => value.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");

    private static int EstimateSecretBytes(string value)
    {
        var trimmed = value.Trim();
        try
        {
            if (trimmed.Length % 4 == 0 && trimmed.All(c => char.IsLetterOrDigit(c) || c is '+' or '/' or '='))
                return Convert.FromBase64String(trimmed).Length;
        }
        catch
        {
        }

        return System.Text.Encoding.UTF8.GetByteCount(trimmed);
    }

    private static bool LooksLowEntropy(string value)
    {
        var trimmed = value.Trim();
        var distinct = trimmed.Distinct().Count();
        if (distinct < 12)
            return true;

        if (trimmed.All(c => c == trimmed[0]))
            return true;

        var classes = 0;
        if (trimmed.Any(char.IsLower)) classes++;
        if (trimmed.Any(char.IsUpper)) classes++;
        if (trimmed.Any(char.IsDigit)) classes++;
        if (trimmed.Any(c => !char.IsLetterOrDigit(c))) classes++;

        return classes < 3;
    }
}
