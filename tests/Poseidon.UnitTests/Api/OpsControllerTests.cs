using FluentAssertions;
using Poseidon.Api.Controllers;
using Poseidon.Api.Localization;
using Poseidon.Domain.Entities;
using Poseidon.Domain.Interfaces;
using Poseidon.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Poseidon.UnitTests.Api;

/// <summary>
/// Tests for <see cref="OpsController"/>: health, metrics, audit, and
/// ops overview REST endpoints.
/// </summary>
public sealed class OpsControllerTests
{
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<ILlmService> _llm = new();
    private readonly Mock<IMetricsCollector> _metrics = new();
    private readonly Mock<IAuditService> _audit = new();
    private readonly ApiTextLocalizer _text = CreateLocalizer();

    private OpsController CreateController() =>
        new(_vectorStore.Object, _llm.Object, _metrics.Object, _audit.Object, _text);

    // â”€â”€â”€ Health â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Health_AllHealthy_Returns200()
    {
        _vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VectorStoreHealth
            {
                IsHealthy = true,
                VectorCount = 1000,
                Status = "Healthy"
            });
        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _audit.Setup(a => a.VerifyChainIntegrityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateController();
        var result = await sut.Health(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result;
        ok.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Health_VectorStoreUnhealthy_Returns503()
    {
        _vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VectorStoreHealth
            {
                IsHealthy = false,
                Error = "Connection refused"
            });
        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _audit.Setup(a => a.VerifyChainIntegrityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateController();
        var result = await sut.Health(CancellationToken.None);

        result.Should().BeOfType<ObjectResult>();
        var obj = (ObjectResult)result;
        obj.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task Health_LlmUnavailable_Returns503()
    {
        _vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VectorStoreHealth { IsHealthy = true });
        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _audit.Setup(a => a.VerifyChainIntegrityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateController();
        var result = await sut.Health(CancellationToken.None);

        result.Should().BeOfType<ObjectResult>();
        ((ObjectResult)result).StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task Health_AuditChainBroken_Returns503()
    {
        _vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VectorStoreHealth { IsHealthy = true });
        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _audit.Setup(a => a.VerifyChainIntegrityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = CreateController();
        var result = await sut.Health(CancellationToken.None);

        result.Should().BeOfType<ObjectResult>();
        ((ObjectResult)result).StatusCode.Should().Be(503);
    }

    // â”€â”€â”€ GetMetrics â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void GetMetrics_ReturnsSnapshot()
    {
        var snapshot = new SystemMetrics
        {
            TotalQueries = 50,
            AbstentionCount = 5,
            TotalDocumentsIndexed = 100
        };
        _metrics.Setup(m => m.GetSnapshot()).Returns(snapshot);

        var sut = CreateController();
        var result = sut.GetMetrics();

        result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result;
        ok.Value.Should().Be(snapshot);
    }

    // â”€â”€â”€ Overview â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Overview_ReturnsCombinedHealthAndMetrics()
    {
        _vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VectorStoreHealth
            {
                IsHealthy = true,
                VectorCount = 500
            });
        _metrics.Setup(m => m.GetSnapshot()).Returns(new SystemMetrics
        {
            TotalQueries = 200,
            AbstentionCount = 10,
            TotalDocumentsIndexed = 50
        });

        var sut = CreateController();
        var result = await sut.Overview(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        ((OkObjectResult)result).StatusCode.Should().Be(200);
    }

    // â”€â”€â”€ GetAuditLog â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task GetAuditLog_ReturnsEntries()
    {
        var entries = new List<AuditEntry>
        {
            new() { Action = "QUERY", Details = "q1", PreviousHash = "GENESIS", Hmac = "h1" },
            new() { Action = "INGEST", Details = "d1", PreviousHash = "h1", Hmac = "h2" }
        };
        _audit.Setup(a => a.GetEntriesAsync(50, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        var sut = CreateController();
        var result = await sut.GetAuditLog(50, 0, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result;
        ok.Value.Should().BeEquivalentTo(entries);
    }

    [Fact]
    public async Task GetAuditLog_CustomLimitAndOffset()
    {
        _audit.Setup(a => a.GetEntriesAsync(10, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AuditEntry>());

        var sut = CreateController();
        await sut.GetAuditLog(10, 20, CancellationToken.None);

        _audit.Verify(a => a.GetEntriesAsync(10, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    // â”€â”€â”€ VerifyAudit â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task VerifyAudit_ValidChain_ReturnsVALID()
    {
        _audit.Setup(a => a.VerifyChainIntegrityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateController();
        var result = await sut.VerifyAudit(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task VerifyAudit_BrokenChain_ReturnsBROKEN()
    {
        _audit.Setup(a => a.VerifyChainIntegrityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sut = CreateController();
        var result = await sut.VerifyAudit(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    private static ApiTextLocalizer CreateLocalizer()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Localization:DefaultLanguage"] = "fr",
                ["Localization:SupportedLanguages"] = "fr,ar,en"
            })
            .Build();

        return new ApiTextLocalizer(config);
    }
}

