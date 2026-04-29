using System.IO;
using FluentAssertions;
using Poseidon.Desktop;
using Poseidon.Desktop.Services;
using Poseidon.Desktop.ViewModels;
using Poseidon.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Poseidon.UnitTests.ViewModels;

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
        _tempDir = Path.Combine(Path.GetTempPath(), $"Poseidon_Settings_{Guid.NewGuid():N}");
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
        vm.StrictModeLockReason.Should().Contain("locked", "lock reason should describe immutable strict mode");
    }

    [Fact]
    public async Task SaveSettings_DoesNotWriteStrictModePolicy()
    {
        var vm = CreateVm();
        vm.StrictMode = false; // Attempt to set to false

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        // Read saved config
        var configPath = Path.Combine(_tempDir, "appsettings.user.json");
        File.Exists(configPath).Should().BeTrue();

        var json = File.ReadAllText(configPath);
        json.Should().NotContain("StrictMode", "user config must not override strict-mode policy");
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
            "Ingestion": {
                "WatchDirectory": "C:\\WatchTest",
                "MaxParallelFiles": 8
            },
            "Ui": {
                "Culture": "en-US"
            }
        }
        """;
        File.WriteAllText(configPath, json);

        var vm = CreateVm();

        vm.WatchDirectory.Should().Be("C:\\WatchTest");
        vm.MaxParallelFiles.Should().Be(8);
        vm.UiCulture.Should().Be("en-US");
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
        vm.MaxParallelFiles = 5;

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        var configPath = Path.Combine(_tempDir, "appsettings.user.json");
        File.Exists(configPath).Should().BeTrue();

        var json = File.ReadAllText(configPath);
        json.Should().Contain("\"MaxParallelFiles\": 5");
        json.Should().Contain("\"Ui\"");
    }

    [Fact]
    public async Task SaveSettings_PersistsOnlyAllowedUserSections()
    {
        var vm = CreateVm();
        vm.MaxParallelFiles = 6;

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        var configPath = Path.Combine(_tempDir, "appsettings.user.json");
        var json = File.ReadAllText(configPath);

        json.Should().NotContain("\"Llm\"");
        json.Should().NotContain("\"Embedding\"");
        json.Should().NotContain("\"Retrieval\"");
        json.Should().NotContain("\"Security\"");
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
        vm.SaveStatus.Should().Contain("saved successfully", "status message should confirm success");
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
    public async Task SaveSettings_DoesNotPersistEncryptionSecretsInUserConfig()
    {
        var vm = CreateVm();
        vm.EncryptionPassphrase = "my-secure-pass";
        vm.EncryptionEnabled = true;

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        var configPath = Path.Combine(_tempDir, "appsettings.user.json");
        var json = File.ReadAllText(configPath);
        json.Should().NotContain("my-secure-pass");
        json.Should().NotContain("ProtectedPassphrase");
        json.Should().NotContain("EncryptionPassphrase");
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


