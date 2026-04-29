using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Poseidon.Security.Configuration;
using Poseidon.Security.Secrets;
using System.IO;

namespace Poseidon.UnitTests.Security;

public sealed class SecurityConfigurationValidatorTests
{
    [Fact]
    public void ValidateJwt_PlaceholderProductionSecret_Rejects()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["Auth:Jwt:Issuer"] = "Poseidon",
            ["Auth:Jwt:Audience"] = "Poseidon.Client",
            ["Auth:Jwt:PrimarySigningKeyRef"] = CreateProtectedSecret("Poseidon/Test/JwtPlaceholder", "CHANGE_THIS_TO_A_LONG_RANDOM_KEY_MIN_32_CHARS")
        });

        var act = () => SecurityConfigurationValidator.ValidateJwt(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*placeholder*");
    }

    [Fact]
    public void ValidateJwt_WeakProductionSecret_Rejects()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["Auth:Jwt:Issuer"] = "Poseidon",
            ["Auth:Jwt:Audience"] = "Poseidon.Client",
            ["Auth:Jwt:PrimarySigningKeyRef"] = CreateProtectedSecret("Poseidon/Test/JwtWeak", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!")
        });

        var act = () => SecurityConfigurationValidator.ValidateJwt(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*low entropy*");
    }

    [Fact]
    public void ValidateJwt_PlaintextProductionSecret_Rejects()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["Auth:Jwt:Issuer"] = "Poseidon",
            ["Auth:Jwt:Audience"] = "Poseidon.Client",
            ["Auth:Jwt:SigningKey"] = "P0seidon!Production.Jwt.Signing.Key.2026.$"
        });

        var act = () => SecurityConfigurationValidator.ValidateJwt(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*plaintext*");
    }

    [Fact]
    public void ValidateJwt_ProtectedProductionSecret_Accepts()
    {
        var reference = CreateProtectedSecret("Poseidon/Test/Jwt", "P0seidon!Production.Jwt.Signing.Key.2026.$");
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["Auth:Jwt:Issuer"] = "Poseidon",
            ["Auth:Jwt:Audience"] = "Poseidon.Client",
            ["Auth:Jwt:PrimarySigningKeyRef"] = reference,
            ["Auth:Jwt:PrimaryKeyVersion"] = "v-test"
        });

        var act = () => SecurityConfigurationValidator.ValidateJwt(config);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateJwt_DevelopmentExplicitInsecureMode_AllowsWeakSecret()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["Security:AllowInsecureDevelopmentSecrets"] = "true",
            ["Auth:Jwt:SigningKey"] = "dev"
        });

        var act = () => SecurityConfigurationValidator.ValidateJwt(config);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateManagementApi_EmptyProductionKeys_Rejects()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["Security:ManagementApiKey"] = "",
            ["Security:AgentApiKey"] = ""
        });

        var act = () => SecurityConfigurationValidator.ValidateManagementApi(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ManagementApiKey*");
    }

    [Fact]
    public void ValidateManagementApi_PlaceholderProductionKeys_Rejects()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["Security:ManagementApiKeyRef"] = CreateProtectedSecret("Poseidon/Test/ManagementPlaceholder", "dev-local"),
            ["Security:AgentApiKeyRef"] = CreateProtectedSecret("Poseidon/Test/Agent", "P0seidon!Production.Agent.Key.2026.$$$")
        });

        var act = () => SecurityConfigurationValidator.ValidateManagementApi(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*placeholder*");
    }

    [Fact]
    public void ValidateManagementApi_ProtectedProductionKeys_Accepts()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["Security:ManagementApiKeyRef"] = CreateProtectedSecret("Poseidon/Test/Management", "P0seidon!Production.Management.Key.2026.$$$"),
            ["Security:AgentApiKeyRef"] = CreateProtectedSecret("Poseidon/Test/Agent", "P0seidon!Production.Agent.Key.2026.$$$")
        });

        var act = () => SecurityConfigurationValidator.ValidateManagementApi(config);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateRuntimeConfiguration_ProductionLocalProviderMissingHashes_Rejects()
    {
        var config = BuildConfig(FullLocalRuntimeConfig(new Dictionary<string, string?>
        {
            ["ModelIntegrity:ExpectedLlmHash"] = "",
            ["ModelIntegrity:ExpectedEmbeddingHash"] = "",
            ["Security:EncryptionEnabled"] = "true",
            ["Security:EncryptionPassphrase"] = "P0seidon!Production.Encryption.Passphrase.2026.$"
        }));

        var act = () => SecurityConfigurationValidator.ValidateDesktop(config, "Production");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ExpectedLlmHash*");
    }

    [Fact]
    public void ValidateRuntimeConfiguration_ProductionEncryptionDisabled_Rejects()
    {
        var config = BuildConfig(FullLocalRuntimeConfig(new Dictionary<string, string?>
        {
            ["ModelIntegrity:ExpectedLlmHash"] = Sha256('a'),
            ["ModelIntegrity:ExpectedEmbeddingHash"] = Sha256('b'),
            ["Security:EncryptionEnabled"] = "false"
        }));

        var act = () => SecurityConfigurationValidator.ValidateDesktop(config, "Production");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*EncryptionEnabled=true*");
    }

    [Fact]
    public void ValidateUserConfigTrustBoundary_CorruptJson_Rejects()
    {
        using var temp = new TempJsonFile("{ invalid json");

        var result = SecurityConfigurationValidator.ValidateUserConfigTrustBoundary(temp.Path);

        result.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ValidateUserConfigTrustBoundary_ModelIntegrityOverride_Rejects()
    {
        using var temp = new TempJsonFile("""
        {
          "ModelIntegrity": {
            "ExpectedLlmHash": ""
          }
        }
        """);

        var result = SecurityConfigurationValidator.ValidateUserConfigTrustBoundary(temp.Path);

        result.Should().Contain("ModelIntegrity");
    }

    [Fact]
    public void ValidateUserConfigTrustBoundary_UiAndWatchFolder_Accepts()
    {
        using var temp = new TempJsonFile("""
        {
          "Ui": {
            "Culture": "fr-FR"
          },
          "Ingestion": {
            "WatchDirectory": "C:\\Poseidon\\Watch"
          }
        }
        """);

        var result = SecurityConfigurationValidator.ValidateUserConfigTrustBoundary(temp.Path);

        result.Should().BeNull();
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> data)
        => new ConfigurationBuilder().AddInMemoryCollection(data).Build();

    private static Dictionary<string, string?> FullLocalRuntimeConfig(Dictionary<string, string?> overrides)
    {
        var encryptionRef = CreateProtectedSecret("Poseidon/Test/Encryption", "P0seidon!Production.Encryption.Passphrase.2026.$");
        var data = new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["Llm:Provider"] = "llamasharp",
            ["Llm:ModelPath"] = @"C:\Poseidon\Models\model.gguf",
            ["Embedding:Provider"] = "onnx",
            ["Embedding:OnnxModelPath"] = @"C:\Poseidon\Models\arabert.onnx",
            ["Retrieval:StrictMode"] = "true",
            ["Security:EncryptionEnabled"] = "true",
            ["Security:EncryptionPassphraseRef"] = encryptionRef,
            ["ModelIntegrity:ExpectedLlmHash"] = Sha256('a'),
            ["ModelIntegrity:ExpectedEmbeddingHash"] = Sha256('b')
        };

        foreach (var item in overrides)
            data[item.Key] = item.Value;

        return data;
    }

    private static string Sha256(char c) => new(c, 64);

    private static string CreateProtectedSecret(string name, string value)
    {
        var reference = ProtectedSecretStore.CreateReference(
            $"{name}/{Guid.NewGuid():N}",
            "v-test",
            ProtectedSecretScope.CurrentUser);
        ProtectedSecretStore.Save(
            ProtectedSecretReference.TryParse(reference, out var parsed) ? parsed : throw new InvalidOperationException("Invalid test reference."),
            value);
        return reference;
    }

    private sealed class TempJsonFile : IDisposable
    {
        public TempJsonFile(string content)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"poseidon-security-{Guid.NewGuid():N}.json");
            File.WriteAllText(Path, content);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { File.Delete(Path); } catch { }
        }
    }
}
