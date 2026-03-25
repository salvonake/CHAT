using System.IO;
using FluentAssertions;
using LegalAI.Desktop;
using LegalAI.Desktop.Services;
using LegalAI.Desktop.ViewModels;
using LegalAI.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace LegalAI.UnitTests.ViewModels;

/// <summary>
/// Tests for <see cref="SettingsViewModel"/>. Ensures settings persistence,
/// fail-closed strict mode lock, and security-critical configuration.
/// </summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly Mock<IEncryptionService> _encryption = new();
    private readonly Mock<ILlmService> _llm = new();
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<ILogger<SettingsViewModel>> _logger = new();
    private readonly string _tempDir;
    private readonly DataPaths _paths;

    public SettingsViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LegalAI_Settings_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _paths = new DataPaths
        {
            DataDirectory = _tempDir,
            ModelsDirectory = Path.Combine(_tempDir, "Models"),
            VectorDbPath = Path.Combine(_tempDir, "vectors.db"),
            HnswIndexPath = Path.Combine(_tempDir, "hnsw.index"),
            DocumentDbPath = Path.Combine(_tempDir, "docs.db"),
            AuditDbPath = Path.Combine(_tempDir, "audit.db"),
            WatchDirectory = Path.Combine(_tempDir, "Watch")
        };

        _encryption.SetupGet(e => e.IsEnabled).Returns(false);

        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VectorStoreHealth { IsHealthy = true, VectorCount = 10 });
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private static Mock<ModelIntegrityService> CreateMockModelIntegrity()
    {
        return new Mock<ModelIntegrityService>(
            MockBehavior.Loose,
            Mock.Of<IConfiguration>(),
            new DataPaths
            {
                DataDirectory = Path.GetTempPath(),
                ModelsDirectory = Path.GetTempPath(),
                VectorDbPath = Path.Combine(Path.GetTempPath(), "x.db"),
                HnswIndexPath = Path.Combine(Path.GetTempPath(), "x.hnsw"),
                DocumentDbPath = Path.Combine(Path.GetTempPath(), "x-docs.db"),
                AuditDbPath = Path.Combine(Path.GetTempPath(), "x-audit.db"),
                WatchDirectory = Path.GetTempPath()
            },
            Mock.Of<ILogger<ModelIntegrityService>>());
    }

    private SettingsViewModel CreateVm()
    {
        var integrity = CreateMockModelIntegrity();
        integrity.SetupGet(m => m.LlmModelExists).Returns(true);
        integrity.SetupGet(m => m.LlmModelValid).Returns(true);
        integrity.SetupGet(m => m.EmbeddingModelExists).Returns(true);
        integrity.SetupGet(m => m.EmbeddingModelValid).Returns(true);

        var guard = new FailClosedGuard(
            _llm.Object, _vectorStore.Object,
            integrity.Object, Mock.Of<ILogger<FailClosedGuard>>());

        return new SettingsViewModel(_encryption.Object, guard, _paths, _logger.Object);
    }

    // ═══════════════════════════════════════
    //  Strict Mode — Fail-Closed Safety
    // ═══════════════════════════════════════

    [Fact]
    public void StrictMode_AlwaysTrue()
    {
        var vm = CreateVm();
        vm.StrictMode.Should().BeTrue("strict mode is permanently locked for life-critical system");
    }

    [Fact]
    public void StrictMode_CannotBeModified()
    {
        var vm = CreateVm();
        vm.CanModifyStrictMode.Should().BeFalse("strict mode toggle must be locked");
    }

    [Fact]
    public void StrictMode_LockReasonIsNotEmpty()
    {
        var vm = CreateVm();
        vm.StrictModeLockReason.Should().NotBeNullOrWhiteSpace();
        vm.StrictModeLockReason.Should().Contain("مقفل", "lock reason should be in Arabic");
    }

    [Fact]
    public async Task SaveSettings_StrictMode_AlwaysWrittenAsTrue()
    {
        var vm = CreateVm();
        vm.StrictMode = false; // Attempt to set to false

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        // Read saved config
        var configPath = Path.Combine(_tempDir, "appsettings.user.json");
        File.Exists(configPath).Should().BeTrue();

        var json = File.ReadAllText(configPath);
        json.Should().Contain("\"StrictMode\": true", "saved config must always have StrictMode=true");
    }

    // ═══════════════════════════════════════
    //  Initial State
    // ═══════════════════════════════════════

    [Fact]
    public void InitialState_DataDirectoryIsSet()
    {
        var vm = CreateVm();
        vm.DataDirectory.Should().Be(_tempDir);
    }

    [Fact]
    public void InitialState_WatchDirectoryIsSet()
    {
        var vm = CreateVm();
        vm.WatchDirectory.Should().Be(_paths.WatchDirectory);
    }

    [Fact]
    public void InitialState_EncryptionStatus_MatchesService()
    {
        _encryption.SetupGet(e => e.IsEnabled).Returns(true);
        var vm = CreateVm();
        vm.EncryptionEnabled.Should().BeTrue();

        _encryption.SetupGet(e => e.IsEnabled).Returns(false);
        var vm2 = CreateVm();
        vm2.EncryptionEnabled.Should().BeFalse();
    }

    [Fact]
    public void InitialState_DefaultRetrievalValues()
    {
        var vm = CreateVm();
        vm.TopK.Should().BeGreaterThan(0);
        vm.SimilarityThreshold.Should().BeGreaterThan(0).And.BeLessThan(1);
        vm.AbstentionThreshold.Should().BeGreaterThan(0).And.BeLessThan(1);
    }

    [Fact]
    public void InitialState_DefaultGpuValues()
    {
        var vm = CreateVm();
        vm.GpuLayers.Should().Be(-1, "default -1 means auto");
        vm.ContextSize.Should().BeGreaterThan(0);
    }

    // ═══════════════════════════════════════
    //  LoadUserConfig
    // ═══════════════════════════════════════

    [Fact]
    public void LoadUserConfig_ReadsExistingFile()
    {
        var configPath = Path.Combine(_tempDir, "appsettings.user.json");
        var json = """
        {
            "Llm": {
                "Provider": "ollama",
                "ModelPath": "C:\\models\\test.gguf",
                "GpuLayers": 42,
                "ContextSize": 4096
            },
            "Retrieval": {
                "TopK": 15,
                "SimilarityThreshold": 0.55,
                "AbstentionThreshold": 0.65,
                "StrictMode": true,
                "EnableDualPassValidation": false
            },
            "Ingestion": {
                "WatchDirectory": "C:\\WatchTest",
                "MaxParallelFiles": 8
            }
        }
        """;
        File.WriteAllText(configPath, json);

        var vm = CreateVm();

        vm.LlmProvider.Should().Be("ollama");
        vm.LlmModelPath.Should().Be("C:\\models\\test.gguf");
        vm.GpuLayers.Should().Be(42);
        vm.ContextSize.Should().Be(4096);
        vm.TopK.Should().Be(15);
        vm.SimilarityThreshold.Should().BeApproximately(0.55, 0.001);
        vm.AbstentionThreshold.Should().BeApproximately(0.65, 0.001);
        vm.EnableDualPass.Should().BeFalse();
        vm.MaxParallelFiles.Should().Be(8);
    }

    [Fact]
    public void LoadUserConfig_MissingFile_UsesDefaults()
    {
        var vm = CreateVm();

        vm.LlmProvider.Should().Be("llamasharp");
        vm.TopK.Should().Be(10);
        vm.EnableDualPass.Should().BeTrue();
    }

    [Fact]
    public void LoadUserConfig_MalformedJson_DoesNotThrow()
    {
        var configPath = Path.Combine(_tempDir, "appsettings.user.json");
        File.WriteAllText(configPath, "{ invalid json }}}");

        var act = () => CreateVm();
        act.Should().NotThrow("malformed config should be handled gracefully");
    }

    // ═══════════════════════════════════════
    //  SaveSettings
    // ═══════════════════════════════════════

    [Fact]
    public async Task SaveSettings_CreatesConfigFile()
    {
        var vm = CreateVm();
        vm.TopK = 25;

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        var configPath = Path.Combine(_tempDir, "appsettings.user.json");
        File.Exists(configPath).Should().BeTrue();

        var json = File.ReadAllText(configPath);
        json.Should().Contain("\"TopK\": 25");
    }

    [Fact]
    public async Task SaveSettings_PersistsAllSections()
    {
        var vm = CreateVm();
        vm.LlmProvider = "ollama";
        vm.EmbeddingProvider = "ollama";
        vm.TopK = 20;
        vm.SimilarityThreshold = 0.6;
        vm.AbstentionThreshold = 0.7;
        vm.EnableDualPass = false;
        vm.EncryptionEnabled = true;
        vm.MaxParallelFiles = 6;

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        var configPath = Path.Combine(_tempDir, "appsettings.user.json");
        var json = File.ReadAllText(configPath);

        json.Should().Contain("\"Provider\": \"ollama\"");
        json.Should().Contain("\"TopK\": 20");
        json.Should().Contain("\"SimilarityThreshold\": 0.6");
        json.Should().Contain("\"AbstentionThreshold\": 0.7");
        json.Should().Contain("\"EnableDualPassValidation\": false");
        json.Should().Contain("\"EncryptionEnabled\": true");
        json.Should().Contain("\"MaxParallelFiles\": 6");
    }

    [Fact]
    public async Task SaveSettings_ClearsUnsavedChangesFlag()
    {
        var vm = CreateVm();
        vm.HasUnsavedChanges = true;

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        vm.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task SaveSettings_SetsSaveStatusMessage()
    {
        var vm = CreateVm();

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        vm.SaveStatus.Should().NotBeNullOrWhiteSpace();
        vm.SaveStatus.Should().Contain("بنجاح", "status message should confirm success in Arabic");
    }

    [Fact]
    public async Task SaveSettings_TriggersGuardRecheck()
    {
        var vm = CreateVm();

        // SaveSettings calls ForceRecheckAsync — just ensure no exception
        var act = async () => await vm.SaveSettingsCommand.ExecuteAsync(null);
        await act.Should().NotThrowAsync();
    }

    // ═══════════════════════════════════════
    //  Security: Encryption Passphrase
    // ═══════════════════════════════════════

    [Fact]
    public async Task SaveSettings_IncludesEncryptionPassphrase()
    {
        var vm = CreateVm();
        vm.EncryptionPassphrase = "my-secure-pass";
        vm.EncryptionEnabled = true;

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        var configPath = Path.Combine(_tempDir, "appsettings.user.json");
        var json = File.ReadAllText(configPath);
        json.Should().Contain("\"EncryptionPassphrase\": \"my-secure-pass\"");
    }

    // ═══════════════════════════════════════
    //  OpenDataDirectory
    // ═══════════════════════════════════════

    [Fact]
    public void OpenDataDirectory_DoesNotThrow()
    {
        var vm = CreateVm();
        // OpenDataDirectory calls Process.Start which may fail in test env
        var act = () => vm.OpenDataDirectoryCommand.Execute(null);
        act.Should().NotThrow("error is caught internally");
    }
}
