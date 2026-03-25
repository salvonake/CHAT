using CommunityToolkit.Mvvm.ComponentModel;
using FluentAssertions;
using LegalAI.Desktop;
using LegalAI.Desktop.Services;
using LegalAI.Desktop.ViewModels;
using LegalAI.Domain.Entities;
using LegalAI.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace LegalAI.UnitTests.ViewModels;

/// <summary>
/// Tests for <see cref="MainViewModel"/>: root ViewModel managing navigation,
/// encryption warning banner, model status, fail-closed state, and vector count.
/// </summary>
public sealed class MainViewModelTests
{
    // ── Shared mock dependencies ──
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IDocumentStore> _docStore = new();
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<IDispatcherService> _dispatcher = new();
    private readonly Mock<IEncryptionService> _encryption = new();
    private readonly Mock<ILlmService> _llm = new();
    private readonly Mock<IMetricsCollector> _metrics = new();
    private readonly Mock<IAuditService> _audit = new();
    private readonly Mock<ILogger<MainViewModel>> _logger = new();

    private readonly Mock<ILogger<FailClosedGuard>> _guardLogger = new();
    private readonly Mock<ILogger<ModelIntegrityService>> _misLogger = new();

    private readonly DataPaths _paths;

    public MainViewModelTests()
    {
        _paths = new DataPaths
        {
            DataDirectory = "C:\\temp",
            ModelsDirectory = "C:\\temp\\models",
            VectorDbPath = "C:\\temp\\vectors.db",
            HnswIndexPath = "C:\\temp\\vectors.hnsw",
            DocumentDbPath = "C:\\temp\\docs.db",
            AuditDbPath = "C:\\temp\\audit.db",
            WatchDirectory = "C:\\temp\\watch"
        };

        _encryption.Setup(e => e.IsEnabled).Returns(false);
        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _vectorStore.Setup(v => v.GetVectorCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);
        _vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VectorStoreHealth { IsHealthy = true, VectorCount = 42 });
        _audit.Setup(a => a.VerifyChainIntegrityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _dispatcher.Setup(d => d.InvokeAsync(It.IsAny<Action>()))
            .Callback<Action>(a => a())
            .Returns(Task.CompletedTask);
        _dispatcher.Setup(d => d.Invoke(It.IsAny<Action>()))
            .Callback<Action>(a => a());

        _docStore.Setup(d => d.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LegalDocument>());
        _docStore.Setup(d => d.GetQuarantineRecordsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QuarantineRecord>());

        _metrics.Setup(m => m.GetSnapshot()).Returns(new LegalAI.Domain.ValueObjects.SystemMetrics());
    }

    private (MainViewModel vm, FailClosedGuard guard) CreateVm(bool encryptionEnabled = false)
    {
        _encryption.Setup(e => e.IsEnabled).Returns(encryptionEnabled);

        var configData = new Dictionary<string, string?>
        {
            ["Llm:Provider"] = "LLamaSharp",
            ["Embedding:Provider"] = "ONNX"
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var mis = new ModelIntegrityService(config, _paths, _misLogger.Object);

        var guard = new FailClosedGuard(
            _llm.Object, _vectorStore.Object, mis, _guardLogger.Object);

        var askVm = new AskViewModel(
            _mediator.Object, _llm.Object, guard, _dispatcher.Object,
            Mock.Of<ILogger<AskViewModel>>());

        var chatVm = new ChatViewModel(
            _mediator.Object, _llm.Object, guard, _dispatcher.Object,
            Mock.Of<ILogger<ChatViewModel>>());

        var docsVm = new DocumentsViewModel(
            _mediator.Object, _docStore.Object, _vectorStore.Object,
            _dispatcher.Object, _paths, Mock.Of<ILogger<DocumentsViewModel>>());

        var settingsVm = new SettingsViewModel(
            _encryption.Object, guard, _paths,
            Mock.Of<ILogger<SettingsViewModel>>());

        var healthVm = new HealthViewModel(
            _vectorStore.Object, _llm.Object, _audit.Object,
            _docStore.Object, _metrics.Object, _encryption.Object,
            mis, _dispatcher.Object, Mock.Of<ILogger<HealthViewModel>>());

        var vm = new MainViewModel(
            askVm, chatVm, docsVm, settingsVm, healthVm,
            _encryption.Object, mis, guard,
            _vectorStore.Object, _logger.Object);

        return (vm, guard);
    }

    // ─── Navigation ───────────────────────────────────────────────

    [Fact]
    public async Task Constructor_DefaultView_IsAskViewModel()
    {
        var (vm, _) = CreateVm();
        await Task.Delay(200);

        vm.CurrentView.Should().BeOfType<AskViewModel>();
        vm.CurrentViewTitle.Should().Contain("استعلام");
    }

    [Fact]
    public async Task NavigateToChat_SwitchesToChatView()
    {
        var (vm, _) = CreateVm();
        await Task.Delay(200);

        vm.NavigateToChatCommand.Execute(null);

        vm.CurrentView.Should().BeOfType<ChatViewModel>();
        vm.CurrentViewTitle.Should().Contain("المحادثة");
    }

    [Fact]
    public async Task NavigateToDocuments_SwitchesToDocumentsView()
    {
        var (vm, _) = CreateVm();
        await Task.Delay(200);

        vm.NavigateToDocumentsCommand.Execute(null);

        vm.CurrentView.Should().BeOfType<DocumentsViewModel>();
        vm.CurrentViewTitle.Should().Contain("الوثائق");
    }

    [Fact]
    public async Task NavigateToSettings_SwitchesToSettingsView()
    {
        var (vm, _) = CreateVm();
        await Task.Delay(200);

        vm.NavigateToSettingsCommand.Execute(null);

        vm.CurrentView.Should().BeOfType<SettingsViewModel>();
        vm.CurrentViewTitle.Should().Contain("الإعدادات");
    }

    [Fact]
    public async Task NavigateToHealth_SwitchesToHealthView()
    {
        var (vm, _) = CreateVm();
        await Task.Delay(200);

        vm.NavigateToHealthCommand.Execute(null);

        vm.CurrentView.Should().BeOfType<HealthViewModel>();
        vm.CurrentViewTitle.Should().Contain("حالة النظام");
    }

    [Fact]
    public async Task NavigateBackToAsk_RestoresAskView()
    {
        var (vm, _) = CreateVm();
        await Task.Delay(200);

        vm.NavigateToChatCommand.Execute(null);
        vm.NavigateToAskCommand.Execute(null);

        vm.CurrentView.Should().BeOfType<AskViewModel>();
    }

    // ─── Encryption Warning ───────────────────────────────────────

    [Fact]
    public async Task EncryptionDisabled_ShowsWarningBanner()
    {
        var (vm, _) = CreateVm(encryptionEnabled: false);
        await Task.Delay(200);

        vm.ShowEncryptionWarning.Should().BeTrue();
        vm.EncryptionWarningText.Should().Contain("التشفير");
    }

    [Fact]
    public async Task EncryptionEnabled_HidesWarningBanner()
    {
        var (vm, _) = CreateVm(encryptionEnabled: true);
        await Task.Delay(200);

        vm.ShowEncryptionWarning.Should().BeFalse();
    }

    // ─── Enable Encryption Command ────────────────────────────────

    [Fact]
    public async Task EnableEncryption_NavigatesToSettings()
    {
        var (vm, _) = CreateVm();
        await Task.Delay(200);

        vm.EnableEncryptionCommand.Execute(null);

        vm.CurrentView.Should().BeOfType<SettingsViewModel>();
    }

    // ─── Vector Count ─────────────────────────────────────────────

    [Fact]
    public async Task Constructor_LoadsVectorCount()
    {
        _vectorStore.Setup(v => v.GetVectorCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1234);

        var (vm, _) = CreateVm();
        await Task.Delay(300);

        vm.VectorCount.Should().Be(1234);
    }

    [Fact]
    public async Task RefreshVectorCount_UpdatesCount()
    {
        _vectorStore.Setup(v => v.GetVectorCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100);

        var (vm, _) = CreateVm();
        await Task.Delay(200);

        _vectorStore.Setup(v => v.GetVectorCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(200);

        await vm.RefreshVectorCountAsync(_vectorStore.Object);

        vm.VectorCount.Should().Be(200);
    }

    // ─── Child ViewModels ─────────────────────────────────────────

    [Fact]
    public async Task ChildViewModels_AreNotNull()
    {
        var (vm, _) = CreateVm();
        await Task.Delay(200);

        vm.AskVm.Should().NotBeNull();
        vm.ChatVm.Should().NotBeNull();
        vm.DocumentsVm.Should().NotBeNull();
        vm.SettingsVm.Should().NotBeNull();
        vm.HealthVm.Should().NotBeNull();
    }
}
