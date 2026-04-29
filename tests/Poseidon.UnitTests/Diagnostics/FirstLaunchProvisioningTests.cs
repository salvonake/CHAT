using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Poseidon.Desktop;
using Poseidon.Desktop.Diagnostics;

namespace Poseidon.UnitTests.Diagnostics;

public sealed class FirstLaunchProvisioningTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "poseidon-first-launch", Guid.NewGuid().ToString("N"));

    public FirstLaunchProvisioningTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void CleanMachine_MissingLocalModels_ShowsWizard()
    {
        var paths = CreatePaths();
        var decision = FirstLaunchProvisioning.Evaluate(paths, CreateConfig());

        decision.Action.Should().Be(FirstLaunchProvisioningAction.ShowWizard);
    }

    [Fact]
    public void ExistingConfig_WithExplicitModels_Proceeds()
    {
        var llm = Path.Combine(_tempDir, "selected.gguf");
        var embedding = Path.Combine(_tempDir, "selected.onnx");
        File.WriteAllText(llm, "llm");
        File.WriteAllText(embedding, "embedding");

        var decision = FirstLaunchProvisioning.Evaluate(
            CreatePaths(),
            CreateConfig(new Dictionary<string, string?>
            {
                ["Llm:ModelPath"] = llm,
                ["Embedding:OnnxModelPath"] = embedding
            }));

        decision.Action.Should().Be(FirstLaunchProvisioningAction.Proceed);
    }

    [Fact]
    public void InstalledModelFallback_ProceedsWhenLocalAppDataIsEmpty()
    {
        var installedModels = Path.Combine(_tempDir, "installed", "Models");
        Directory.CreateDirectory(installedModels);
        File.WriteAllText(Path.Combine(installedModels, "qwen2.5-14b.Q5_K_M.gguf"), "llm");
        File.WriteAllText(Path.Combine(installedModels, "arabert.onnx"), "embedding");

        var paths = CreatePaths(installedModelsDirectory: installedModels);
        ModelPathResolver.ResolveLlmPath(CreateConfig(), paths).Should().StartWith(installedModels);
        ModelPathResolver.ResolveEmbeddingPath(CreateConfig(), paths).Should().StartWith(installedModels);

        var decision = FirstLaunchProvisioning.Evaluate(paths, CreateConfig());
        decision.Action.Should().Be(FirstLaunchProvisioningAction.Proceed);
    }

    [Fact]
    public void MissingEmbedding_ShowsWizard()
    {
        var paths = CreatePaths();
        File.WriteAllText(Path.Combine(paths.ModelsDirectory, "qwen2.5-14b.Q5_K_M.gguf"), "llm");

        var decision = FirstLaunchProvisioning.Evaluate(paths, CreateConfig());

        decision.Action.Should().Be(FirstLaunchProvisioningAction.ShowWizard);
    }

    [Fact]
    public void MissingLlm_ShowsWizard()
    {
        var paths = CreatePaths();
        File.WriteAllText(Path.Combine(paths.ModelsDirectory, "arabert.onnx"), "embedding");

        var decision = FirstLaunchProvisioning.Evaluate(paths, CreateConfig());

        decision.Action.Should().Be(FirstLaunchProvisioningAction.ShowWizard);
    }

    [Fact]
    public void CorruptedUserConfig_EntersRecovery()
    {
        var paths = CreatePaths();
        File.WriteAllText(paths.UserConfigPath, "{ not-json");

        var decision = FirstLaunchProvisioning.Evaluate(paths, CreateConfig());

        decision.Action.Should().Be(FirstLaunchProvisioningAction.Recovery);
        decision.Reason.Should().Contain("invalid");
    }

    [Fact]
    public void OllamaWithValidShape_ProceedsToRuntimeHealth()
    {
        var decision = FirstLaunchProvisioning.Evaluate(
            CreatePaths(),
            CreateConfig(new Dictionary<string, string?>
            {
                ["Llm:Provider"] = "ollama",
                ["Embedding:Provider"] = "ollama",
                ["Ollama:Url"] = "http://localhost:11434",
                ["Ollama:Model"] = "qwen2.5:14b",
                ["Embedding:Model"] = "nomic-embed-text"
            }));

        decision.Action.Should().Be(FirstLaunchProvisioningAction.Proceed);
    }

    [Fact]
    public void OllamaWithInvalidEndpoint_EntersRecovery()
    {
        var decision = FirstLaunchProvisioning.Evaluate(
            CreatePaths(),
            CreateConfig(new Dictionary<string, string?>
            {
                ["Llm:Provider"] = "ollama",
                ["Embedding:Provider"] = "ollama",
                ["Ollama:Url"] = "not-a-url",
                ["Ollama:Model"] = "qwen2.5:14b",
                ["Embedding:Model"] = "nomic-embed-text"
            }));

        decision.Action.Should().Be(FirstLaunchProvisioningAction.Recovery);
    }

    [Fact]
    public void OllamaLlmWithLocalOnnxEmbedding_RequiresEmbeddingModel()
    {
        var paths = CreatePaths();
        File.WriteAllText(Path.Combine(paths.ModelsDirectory, "arabert.onnx"), "embedding");

        var decision = FirstLaunchProvisioning.Evaluate(
            paths,
            CreateConfig(new Dictionary<string, string?>
            {
                ["Llm:Provider"] = "ollama",
                ["Embedding:Provider"] = "onnx",
                ["Ollama:Url"] = "http://localhost:11434",
                ["Ollama:Model"] = "qwen2.5:14b"
            }));

        decision.Action.Should().Be(FirstLaunchProvisioningAction.Proceed);
    }

    [Fact]
    public void LocalGgufLlmWithOllamaEmbedding_RequiresLlmModel()
    {
        var paths = CreatePaths();
        File.WriteAllText(Path.Combine(paths.ModelsDirectory, "qwen2.5-14b.Q5_K_M.gguf"), "llm");

        var decision = FirstLaunchProvisioning.Evaluate(
            paths,
            CreateConfig(new Dictionary<string, string?>
            {
                ["Llm:Provider"] = "llamasharp",
                ["Embedding:Provider"] = "ollama",
                ["Ollama:Url"] = "http://localhost:11434",
                ["Embedding:Model"] = "nomic-embed-text"
            }));

        decision.Action.Should().Be(FirstLaunchProvisioningAction.Proceed);
    }

    [Fact]
    public void ConfiguredMixedProvider_MissingLocalEmbedding_EntersRecovery()
    {
        var paths = CreatePaths();
        File.WriteAllText(paths.UserConfigPath, "{}");

        var decision = FirstLaunchProvisioning.Evaluate(
            paths,
            CreateConfig(new Dictionary<string, string?>
            {
                ["Llm:Provider"] = "ollama",
                ["Embedding:Provider"] = "onnx",
                ["Ollama:Url"] = "http://localhost:11434",
                ["Ollama:Model"] = "qwen2.5:14b"
            }));

        decision.Action.Should().Be(FirstLaunchProvisioningAction.Recovery);
        decision.Reason.Should().Contain("embedding");
    }

    [Fact]
    public void ConfiguredMixedProvider_MissingLocalLlm_EntersRecovery()
    {
        var paths = CreatePaths();
        File.WriteAllText(paths.UserConfigPath, "{}");

        var decision = FirstLaunchProvisioning.Evaluate(
            paths,
            CreateConfig(new Dictionary<string, string?>
            {
                ["Llm:Provider"] = "llamasharp",
                ["Embedding:Provider"] = "ollama",
                ["Ollama:Url"] = "http://localhost:11434",
                ["Embedding:Model"] = "nomic-embed-text"
            }));

        decision.Action.Should().Be(FirstLaunchProvisioningAction.Recovery);
        decision.Reason.Should().Contain("LLM");
    }

    private DataPaths CreatePaths(string? installedModelsDirectory = null)
    {
        var data = Path.Combine(_tempDir, "data");
        var models = Path.Combine(data, "Models");
        Directory.CreateDirectory(models);

        return new DataPaths
        {
            DataDirectory = data,
            ModelsDirectory = models,
            InstalledModelsDirectory = installedModelsDirectory ?? Path.Combine(_tempDir, "install", "Models"),
            VectorDbPath = Path.Combine(data, "vectors.db"),
            HnswIndexPath = Path.Combine(data, "hnsw.index"),
            DocumentDbPath = Path.Combine(data, "documents.db"),
            AuditDbPath = Path.Combine(data, "audit.db"),
            WatchDirectory = Path.Combine(data, "Watch"),
            UserConfigPath = Path.Combine(data, "appsettings.user.json"),
            LogsDirectory = Path.Combine(data, "Logs"),
            AppLogPath = Path.Combine(data, "Logs", "app.log"),
            StartupLogPath = Path.Combine(data, "Logs", "startup.log")
        };
    }

    private static IConfigurationRoot CreateConfig(Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Llm:Provider"] = "llamasharp",
            ["Llm:ModelPath"] = "",
            ["Embedding:Provider"] = "onnx",
            ["Embedding:OnnxModelPath"] = "",
            ["Ollama:Url"] = "http://localhost:11434",
            ["Ollama:Model"] = "qwen2.5:14b",
            ["Embedding:Model"] = "nomic-embed-text"
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
                values[key] = value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }
}
