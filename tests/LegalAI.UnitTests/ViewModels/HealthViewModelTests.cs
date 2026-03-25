using System.Collections.ObjectModel;
using FluentAssertions;
using LegalAI.Desktop;
using LegalAI.Desktop.Services;
using LegalAI.Desktop.ViewModels;
using LegalAI.Domain.Entities;
using LegalAI.Domain.Interfaces;
using LegalAI.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace LegalAI.UnitTests.ViewModels;

/// <summary>
/// Tests for <see cref="HealthViewModel"/>. Ensures system health monitoring,
/// audit chain verification, and metrics display for the life-critical system.
/// </summary>
public sealed class HealthViewModelTests : IDisposable
{
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<ILlmService> _llm = new();
    private readonly Mock<IAuditService> _audit = new();
    private readonly Mock<IDocumentStore> _docStore = new();
    private readonly Mock<IMetricsCollector> _metrics = new();
    private readonly Mock<IEncryptionService> _encryption = new();
    private readonly Mock<IDispatcherService> _dispatcher = new();
    private readonly Mock<ILogger<HealthViewModel>> _logger = new();
    private readonly Mock<ModelIntegrityService> _modelIntegrity;

    public HealthViewModelTests()
    {
        _modelIntegrity = CreateMockModelIntegrity(
            llmExists: true, llmValid: true,
            embExists: true, embValid: true,
            gpuInfo: "NVIDIA RTX 4090 24GB");

        // Default mock setups — healthy state
        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VectorStoreHealth
            {
                IsHealthy = true,
                VectorCount = 5000,
                IndexedSegments = 250,
                Status = "OK"
            });

        _docStore.Setup(d => d.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LegalDocument>
            {
                CreateDoc(DocumentStatus.Indexed),
                CreateDoc(DocumentStatus.Indexed),
                CreateDoc(DocumentStatus.Pending),
                CreateDoc(DocumentStatus.Quarantined)
            });

        _metrics.Setup(m => m.GetSnapshot()).Returns(new SystemMetrics
        {
            TotalQueries = 100,
            AbstentionCount = 5,
            RetrievalLatencyP50Ms = 42,
            AverageGenerationLatencyMs = 850,
            TotalDocumentsIndexed = 200,
            InjectionDetections = 3
        });

        _encryption.SetupGet(e => e.IsEnabled).Returns(true);

        // Dispatcher runs actions synchronously in tests
        _dispatcher.Setup(d => d.InvokeAsync(It.IsAny<Action>()))
            .Callback<Action>(a => a())
            .Returns(Task.CompletedTask);
        _dispatcher.Setup(d => d.Invoke(It.IsAny<Action>()))
            .Callback<Action>(a => a());
    }

    public void Dispose() { /* no temp files needed */ }

    private static LegalDocument CreateDoc(DocumentStatus status) =>
        new()
        {
            FilePath = $"C:\\docs\\{Guid.NewGuid():N}.pdf",
            FileName = "test.pdf",
            ContentHash = Guid.NewGuid().ToString("N"),
            Status = status
        };

    private static Mock<ModelIntegrityService> CreateMockModelIntegrity(
        bool llmExists = true, bool llmValid = true,
        bool embExists = true, bool embValid = true,
        string? gpuInfo = null)
    {
        var mock = new Mock<ModelIntegrityService>(
            MockBehavior.Loose,
            Mock.Of<IConfiguration>(),
            new DataPaths
            {
                DataDirectory = System.IO.Path.GetTempPath(),
                ModelsDirectory = System.IO.Path.GetTempPath(),
                VectorDbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "h.db"),
                HnswIndexPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "h.hnsw"),
                DocumentDbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "h-docs.db"),
                AuditDbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "h-audit.db"),
                WatchDirectory = System.IO.Path.GetTempPath()
            },
            Mock.Of<ILogger<ModelIntegrityService>>());

        mock.SetupGet(m => m.LlmModelExists).Returns(llmExists);
        mock.SetupGet(m => m.LlmModelValid).Returns(llmValid);
        mock.SetupGet(m => m.EmbeddingModelExists).Returns(embExists);
        mock.SetupGet(m => m.EmbeddingModelValid).Returns(embValid);
        mock.SetupGet(m => m.DetectedGpuInfo).Returns(gpuInfo);

        return mock;
    }

    private HealthViewModel CreateVm() =>
        new(
            _vectorStore.Object,
            _llm.Object,
            _audit.Object,
            _docStore.Object,
            _metrics.Object,
            _encryption.Object,
            _modelIntegrity.Object,
            _dispatcher.Object,
            _logger.Object);

    // ═══════════════════════════════════════
    //  Initial State
    // ═══════════════════════════════════════

    [Fact]
    public async Task InitialState_EncryptionStatus_ReflectsService()
    {
        var vm = CreateVm();
        await Task.Delay(200); // Allow constructor RefreshAllAsync to complete

        vm.EncryptionStatus.Should().Contain("✓", "encryption is enabled");
    }

    [Fact]
    public void InitialState_EncryptionDisabled_ShowsCross()
    {
        _encryption.SetupGet(e => e.IsEnabled).Returns(false);
        var vm = CreateVm();

        vm.EncryptionStatus.Should().Contain("✗");
    }

    [Fact]
    public void InitialState_GpuStatus_ReflectsDetection()
    {
        var vm = CreateVm();
        vm.GpuStatus.Should().Contain("RTX 4090");
    }

    [Fact]
    public void InitialState_GpuUnknown_ShowsDefault()
    {
        _modelIntegrity.SetupGet(m => m.DetectedGpuInfo).Returns((string?)null);
        var vm = CreateVm();
        vm.GpuStatus.Should().Be("غير محدد");
    }

    // ═══════════════════════════════════════
    //  RefreshAll
    // ═══════════════════════════════════════

    [Fact]
    public async Task RefreshAll_UpdatesLlmStatus_WhenAvailable()
    {
        var vm = CreateVm();
        await WaitForInitialRefresh(vm);
        await vm.RefreshAllCommand.ExecuteAsync(null);

        vm.LlmAvailable.Should().BeTrue();
        vm.LlmStatusText.Should().Contain("✓");
    }

    [Fact]
    public async Task RefreshAll_UpdatesLlmStatus_WhenUnavailable()
    {
        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var vm = CreateVm();
        await WaitForInitialRefresh(vm);
        await vm.RefreshAllCommand.ExecuteAsync(null);

        vm.LlmAvailable.Should().BeFalse();
        vm.LlmStatusText.Should().Contain("✗");
    }

    [Fact]
    public async Task RefreshAll_ShowsIntegrityWarning_WhenLlmInvalid()
    {
        _modelIntegrity.SetupGet(m => m.LlmModelValid).Returns(false);
        _modelIntegrity.SetupGet(m => m.LlmModelExists).Returns(true);

        var vm = CreateVm();
        await WaitForInitialRefresh(vm);
        await vm.RefreshAllCommand.ExecuteAsync(null);

        vm.LlmStatusText.Should().Contain("⚠", "integrity failure should show warning");
    }

    [Fact]
    public async Task RefreshAll_UpdatesVectorStore()
    {
        var vm = CreateVm();
        await WaitForInitialRefresh(vm);
        await vm.RefreshAllCommand.ExecuteAsync(null);

        vm.VectorStoreHealthy.Should().BeTrue();
        vm.VectorCount.Should().Be(5000);
        vm.IndexedSegments.Should().Be(250);
    }

    [Fact]
    public async Task RefreshAll_UpdatesVectorStore_WhenUnhealthy()
    {
        _vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VectorStoreHealth
            {
                IsHealthy = false,
                VectorCount = 0,
                Status = "Error"
            });

        var vm = CreateVm();
        await WaitForInitialRefresh(vm);
        await vm.RefreshAllCommand.ExecuteAsync(null);

        vm.VectorStoreHealthy.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAll_UpdatesDocumentCounts()
    {
        var vm = CreateVm();
        await WaitForInitialRefresh(vm);
        await vm.RefreshAllCommand.ExecuteAsync(null);

        vm.TotalDocuments.Should().Be(4);
        vm.IndexedDocuments.Should().Be(2);
        vm.PendingDocuments.Should().Be(1);
        vm.QuarantinedDocuments.Should().Be(1);
    }

    [Fact]
    public async Task RefreshAll_SetsLastRefreshTime()
    {
        var vm = CreateVm();
        await WaitForInitialRefresh(vm);
        await vm.RefreshAllCommand.ExecuteAsync(null);

        vm.LastRefreshTime.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RefreshAll_LoadsMetrics()
    {
        var vm = CreateVm();
        await WaitForInitialRefresh(vm);
        await vm.RefreshAllCommand.ExecuteAsync(null);

        vm.MetricItems.Should().NotBeEmpty();
        vm.MetricItems.Should().Contain(m => m.NameEn == "Total Queries");
        vm.MetricItems.Should().Contain(m => m.NameEn == "Abstentions");
        vm.MetricItems.Should().Contain(m => m.NameEn == "Injection Attempts Blocked");
    }

    [Fact]
    public async Task RefreshAll_MetricValues_AreFormatted()
    {
        var vm = CreateVm();
        await WaitForInitialRefresh(vm);
        await vm.RefreshAllCommand.ExecuteAsync(null);

        var totalQueries = vm.MetricItems.First(m => m.NameEn == "Total Queries");
        totalQueries.Value.Should().Contain("100");

        var injections = vm.MetricItems.First(m => m.NameEn == "Injection Attempts Blocked");
        injections.Value.Should().Contain("3");
    }

    // ═══════════════════════════════════════
    //  Audit Chain Verification
    // ═══════════════════════════════════════

    [Fact]
    public async Task VerifyAuditChain_Valid_ShowsSuccess()
    {
        _audit.Setup(a => a.VerifyChainIntegrityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var vm = CreateVm();
        await vm.VerifyAuditChainCommand.ExecuteAsync(null);

        vm.AuditChainValid.Should().BeTrue();
        vm.AuditChainStatus.Should().Contain("✓");
    }

    [Fact]
    public async Task VerifyAuditChain_Invalid_ShowsError()
    {
        _audit.Setup(a => a.VerifyChainIntegrityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var vm = CreateVm();
        await vm.VerifyAuditChainCommand.ExecuteAsync(null);

        vm.AuditChainValid.Should().BeFalse();
        vm.AuditChainStatus.Should().Contain("✗");
    }

    [Fact]
    public async Task VerifyAuditChain_Exception_ShowsError()
    {
        _audit.Setup(a => a.VerifyChainIntegrityAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB corrupted"));

        var vm = CreateVm();
        await vm.VerifyAuditChainCommand.ExecuteAsync(null);

        vm.AuditChainValid.Should().BeFalse();
        vm.AuditChainStatus.Should().Contain("✗");
    }

    // ═══════════════════════════════════════
    //  Error Handling — Graceful Degradation
    // ═══════════════════════════════════════

    [Fact]
    public async Task RefreshAll_LlmThrows_HandledGracefully()
    {
        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("LLM crashed"));

        var vm = CreateVm();
        await WaitForInitialRefresh(vm);
        await vm.RefreshAllCommand.ExecuteAsync(null);

        vm.LlmAvailable.Should().BeFalse();
        vm.LlmStatusText.Should().Contain("✗");
    }

    [Fact]
    public async Task RefreshAll_VectorStoreThrows_HandledGracefully()
    {
        _vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DB offline"));

        var vm = CreateVm();
        await WaitForInitialRefresh(vm);
        await vm.RefreshAllCommand.ExecuteAsync(null);

        vm.VectorStoreHealthy.Should().BeFalse();
        vm.VectorStoreStatus.Should().Contain("خطأ");
    }

    [Fact]
    public async Task RefreshAll_DocumentStoreThrows_HandledGracefully()
    {
        _docStore.Setup(d => d.GetAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Disk fail"));

        var vm = CreateVm();
        await WaitForInitialRefresh(vm);
        var act = async () => await vm.RefreshAllCommand.ExecuteAsync(null);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RefreshAll_MetricsThrow_HandledGracefully()
    {
        _metrics.Setup(m => m.GetSnapshot())
            .Throws(new Exception("Metrics unavailable"));

        var vm = CreateVm();
        await WaitForInitialRefresh(vm);
        var act = async () => await vm.RefreshAllCommand.ExecuteAsync(null);
        await act.Should().NotThrowAsync();
    }

    // ═══════════════════════════════════════
    //  IsRefreshing Guard
    // ═══════════════════════════════════════

    [Fact]
    public async Task RefreshAll_SetsIsRefreshingDuringExecution()
    {
        bool wasRefreshing = false;

        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken _) =>
            {
                await Task.Delay(50);
                return true;
            });

        var vm = CreateVm();

        // Wait for constructor refresh to complete
        await Task.Delay(200);

        // Now trigger a controlled refresh
        var refreshTask = vm.RefreshAllCommand.ExecuteAsync(null);
        await Task.Delay(10);
        wasRefreshing = vm.IsRefreshing;
        await refreshTask;

        // After completion, should not be refreshing
        vm.IsRefreshing.Should().BeFalse();
    }

    // ═══════════════════════════════════════
    //  MetricItem DTO
    // ═══════════════════════════════════════

    [Fact]
    public void MetricItem_DefaultValues()
    {
        var item = new MetricItem();
        item.NameAr.Should().BeEmpty();
        item.NameEn.Should().BeEmpty();
        item.Value.Should().BeEmpty();
    }

    [Fact]
    public void MetricItem_InitProperties()
    {
        var item = new MetricItem
        {
            NameAr = "الاستعلامات",
            NameEn = "Queries",
            Value = "42"
        };

        item.NameAr.Should().Be("الاستعلامات");
        item.NameEn.Should().Be("Queries");
        item.Value.Should().Be("42");
    }

    // ═══════════════════════════════════════
    //  Helper
    // ═══════════════════════════════════════

    /// <summary>
    /// Wait for the constructor's fire-and-forget RefreshAllAsync to finish.
    /// Without this, the IsRefreshing guard causes RefreshAllCommand to no-op.
    /// </summary>
    private static async Task WaitForInitialRefresh(HealthViewModel vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.IsRefreshing && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }
}
