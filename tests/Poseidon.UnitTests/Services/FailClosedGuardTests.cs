using System.IO;
using FluentAssertions;
using Poseidon.Desktop;
using Poseidon.Desktop.Services;
using Poseidon.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Poseidon.UnitTests.Services;

/// <summary>
/// Tests for <see cref="FailClosedGuard"/>. Life-critical component:
/// when in doubt, the system must refuse to generate answers.
/// </summary>
public sealed class FailClosedGuardTests : IDisposable
{
    private readonly Mock<ILlmService> _llm = new();
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<ModelIntegrityService> _modelIntegrity;
    private readonly Mock<ILogger<FailClosedGuard>> _logger = new();

    public FailClosedGuardTests()
    {
        // ModelIntegrityService has no interface — need to mock via constructor.
        // Since it takes IConfiguration, DataPaths, ILogger, we'll create a test subclass instead.
        // For now, we'll use a real instance with a test helper.
        _modelIntegrity = CreateMockModelIntegrity(
            llmExists: true, llmValid: true,
            embExists: true, embValid: true);
    }

    private static Mock<ModelIntegrityService> CreateMockModelIntegrity(
        bool llmExists, bool llmValid, bool embExists, bool embValid)
    {
        // ModelIntegrityService is not easily mockable (sealed-like properties).
        // We'll work around this by using a wrapper approach in tests.
        // For now, use a real TestableModelIntegrityService.
        var mock = new Mock<ModelIntegrityService>(
            MockBehavior.Loose,
            Mock.Of<IConfiguration>(),
            new DataPaths
            {
                DataDirectory = Path.GetTempPath(),
                ModelsDirectory = Path.GetTempPath(),
                VectorDbPath = Path.Combine(Path.GetTempPath(), "test.db"),
                HnswIndexPath = Path.Combine(Path.GetTempPath(), "test.hnsw"),
                DocumentDbPath = Path.Combine(Path.GetTempPath(), "test-docs.db"),
                AuditDbPath = Path.Combine(Path.GetTempPath(), "test-audit.db"),
                WatchDirectory = Path.GetTempPath()
            },
            Mock.Of<ILogger<ModelIntegrityService>>()
        );

        mock.SetupGet(m => m.LlmModelExists).Returns(llmExists);
        mock.SetupGet(m => m.LlmModelValid).Returns(llmValid);
        mock.SetupGet(m => m.EmbeddingModelExists).Returns(embExists);
        mock.SetupGet(m => m.EmbeddingModelValid).Returns(embValid);

        return mock;
    }

    private FailClosedGuard CreateGuard(
        bool llmExists = true, bool llmValid = true,
        bool embExists = true, bool embValid = true,
        bool llmAvailable = true,
        bool vectorHealthy = true)
    {
        var modelIntegrity = CreateMockModelIntegrity(llmExists, llmValid, embExists, embValid);

        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(llmAvailable);

        _vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VectorStoreHealth { IsHealthy = vectorHealthy, VectorCount = 100 });

        return new FailClosedGuard(_llm.Object, _vectorStore.Object, modelIntegrity.Object, _logger.Object);
    }

    // ═══════════════════════════════════════
    //  Status transitions
    // ═══════════════════════════════════════

    [Fact]
    public void InitialStatus_IsInitializing()
    {
        var guard = CreateGuard();
        guard.Status.Should().Be(SystemOperationalStatus.Initializing);
    }

    [Fact]
    public async Task Initialize_AllHealthy_StatusOperational()
    {
        var guard = CreateGuard();
        await guard.InitializeAsync();

        guard.Status.Should().Be(SystemOperationalStatus.Operational);
        guard.CanAskQuestions.Should().BeTrue();
        guard.BlockReasons.Should().BeEmpty();
    }

    [Fact]
    public async Task Initialize_LlmMissing_StatusLibraryOnly()
    {
        var guard = CreateGuard(llmExists: false);
        await guard.InitializeAsync();

        guard.Status.Should().Be(SystemOperationalStatus.LibraryOnly);
        guard.CanAskQuestions.Should().BeFalse();
        guard.BlockReasons.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Initialize_LlmInvalid_StatusLibraryOnly()
    {
        var guard = CreateGuard(llmExists: true, llmValid: false);
        await guard.InitializeAsync();

        guard.Status.Should().Be(SystemOperationalStatus.LibraryOnly);
        guard.CanAskQuestions.Should().BeFalse();
    }

    [Fact]
    public async Task Initialize_EmbeddingMissing_StatusLibraryOnly()
    {
        var guard = CreateGuard(embExists: false);
        await guard.InitializeAsync();

        guard.Status.Should().Be(SystemOperationalStatus.LibraryOnly);
        guard.CanAskQuestions.Should().BeFalse();
    }

    [Fact]
    public async Task Initialize_EmbeddingInvalid_StatusLibraryOnly()
    {
        var guard = CreateGuard(embExists: true, embValid: false);
        await guard.InitializeAsync();

        guard.Status.Should().Be(SystemOperationalStatus.LibraryOnly);
    }

    [Fact]
    public async Task Initialize_LlmUnavailable_StatusLibraryOnly()
    {
        var guard = CreateGuard(llmAvailable: false);
        await guard.InitializeAsync();

        guard.Status.Should().Be(SystemOperationalStatus.LibraryOnly);
        guard.CanAskQuestions.Should().BeFalse();
    }

    [Fact]
    public async Task Initialize_LlmThrows_StatusLibraryOnly()
    {
        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection refused"));

        var modelIntegrity = CreateMockModelIntegrity(true, true, true, true);
        _vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VectorStoreHealth { IsHealthy = true });

        var guard = new FailClosedGuard(_llm.Object, _vectorStore.Object, modelIntegrity.Object, _logger.Object);
        await guard.InitializeAsync();

        guard.Status.Should().Be(SystemOperationalStatus.LibraryOnly);
        guard.CanAskQuestions.Should().BeFalse();
    }

    [Fact]
    public async Task Initialize_VectorStoreUnhealthy_StatusDegraded()
    {
        var guard = CreateGuard(vectorHealthy: false);
        await guard.InitializeAsync();

        guard.Status.Should().Be(SystemOperationalStatus.Degraded);
        guard.CanAskQuestions.Should().BeTrue(); // Degraded still allows questions
        guard.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Initialize_VectorStoreThrows_StatusLibraryOnly()
    {
        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DB locked"));

        var modelIntegrity = CreateMockModelIntegrity(true, true, true, true);
        var guard = new FailClosedGuard(_llm.Object, _vectorStore.Object, modelIntegrity.Object, _logger.Object);
        await guard.InitializeAsync();

        guard.Status.Should().Be(SystemOperationalStatus.LibraryOnly);
        guard.CanAskQuestions.Should().BeFalse();
    }

    // ═══════════════════════════════════════
    //  StatusChanged event
    // ═══════════════════════════════════════

    [Fact]
    public async Task Initialize_FiresStatusChangedEvent()
    {
        var guard = CreateGuard();
        SystemOperationalStatus? receivedStatus = null;
        guard.StatusChanged += (_, s) => receivedStatus = s;

        await guard.InitializeAsync();

        receivedStatus.Should().Be(SystemOperationalStatus.Operational);
    }

    // ═══════════════════════════════════════
    //  Configuration constants
    // ═══════════════════════════════════════

    [Fact]
    public void AllowStrictModeOverride_AlwaysFalse()
    {
        var guard = CreateGuard();
        guard.AllowStrictModeOverride.Should().BeFalse();
    }

    [Fact]
    public void MinimumConfidenceThreshold_Is40Percent()
    {
        var guard = CreateGuard();
        guard.MinimumConfidenceThreshold.Should().Be(0.40);
    }

    [Fact]
    public void MinimumCitationCount_IsOneOrMore()
    {
        var guard = CreateGuard();
        guard.MinimumCitationCount.Should().BeGreaterThanOrEqualTo(1);
    }

    // ═══════════════════════════════════════
    //  ForceRecheck
    // ═══════════════════════════════════════

    [Fact]
    public async Task ForceRecheck_UpdatesStatus()
    {
        var guard = CreateGuard();
        await guard.InitializeAsync();
        guard.Status.Should().Be(SystemOperationalStatus.Operational);

        // Now make LLM unavailable
        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await guard.ForceRecheckAsync();

        guard.Status.Should().Be(SystemOperationalStatus.LibraryOnly);
        guard.CanAskQuestions.Should().BeFalse();
    }

    // ═══════════════════════════════════════
    //  Multiple simultaneous failures
    // ═══════════════════════════════════════

    [Fact]
    public async Task Initialize_MultipleFailures_AllBlockReasonsCaptured()
    {
        var guard = CreateGuard(llmExists: false, embExists: false, llmAvailable: false);
        await guard.InitializeAsync();

        guard.Status.Should().Be(SystemOperationalStatus.LibraryOnly);
        guard.BlockReasons.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    public void Dispose()
    {
        // Individual tests create and dispose their own guard instances
    }
}


