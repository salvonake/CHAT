using FluentAssertions;
using Grpc.Core;
using LegalAI.Domain.Entities;
using LegalAI.Infrastructure.VectorStore;
using Microsoft.Extensions.Logging;
using Moq;

namespace LegalAI.UnitTests.Infrastructure;

public sealed class QdrantVectorStoreTests
{
    private readonly Mock<ILogger<QdrantVectorStore>> _logger = new();

    private QdrantVectorStore CreateSut() =>
        new(
            host: "127.0.0.1",
            port: 65530,
            collectionName: "legal_chunks_test",
            embeddingDimension: 768,
            logger: _logger.Object);

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var act = () => CreateSut();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task UpsertAsync_EmptyChunks_ReturnsWithoutThrowing()
    {
        var sut = CreateSut();

        var act = async () => await sut.UpsertAsync([]);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task InitializeAsync_CancelledToken_ThrowsCancellationError()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await sut.InitializeAsync(cts.Token);

        var ex = await act.Should().ThrowAsync<Exception>();
        (ex.Which is OperationCanceledException ||
         ex.Which is RpcException rpc && rpc.StatusCode == StatusCode.Cancelled)
            .Should().BeTrue();
    }

    [Fact]
    public async Task GetHealthAsync_CancelledToken_ReturnsUnhealthy()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var health = await sut.GetHealthAsync(cts.Token);

        health.IsHealthy.Should().BeFalse();
        health.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SearchAsync_CancelledToken_ThrowsCancellationError()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await sut.SearchAsync(
            queryEmbedding: new float[768],
            topK: 5,
            scoreThreshold: 0,
            caseNamespace: null,
            ct: cts.Token);

        var ex = await act.Should().ThrowAsync<Exception>();
        (ex.Which is OperationCanceledException ||
         ex.Which is RpcException rpc && rpc.StatusCode == StatusCode.Cancelled)
            .Should().BeTrue();
    }

    [Fact]
    public async Task DeleteByDocumentIdAsync_CancelledToken_ThrowsCancellationError()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await sut.DeleteByDocumentIdAsync("doc-1", cts.Token);

        var ex = await act.Should().ThrowAsync<Exception>();
        (ex.Which is OperationCanceledException ||
         ex.Which is RpcException rpc && rpc.StatusCode == StatusCode.Cancelled)
            .Should().BeTrue();
    }

    [Fact]
    public async Task ExistsByHashAsync_CancelledToken_ThrowsCancellationError()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await sut.ExistsByHashAsync("hash-1", cts.Token);

        var ex = await act.Should().ThrowAsync<Exception>();
        (ex.Which is OperationCanceledException ||
         ex.Which is RpcException rpc && rpc.StatusCode == StatusCode.Cancelled)
            .Should().BeTrue();
    }

    [Fact]
    public async Task GetVectorCountAsync_CancelledToken_ThrowsCancellationError()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await sut.GetVectorCountAsync(cts.Token);

        var ex = await act.Should().ThrowAsync<Exception>();
        (ex.Which is OperationCanceledException ||
         ex.Which is RpcException rpc && rpc.StatusCode == StatusCode.Cancelled)
            .Should().BeTrue();
    }

    [Fact]
    public async Task UpsertAsync_NullEmbedding_ThrowsArgumentNull()
    {
        var sut = CreateSut();

        var chunk = new DocumentChunk
        {
            Id = "chunk-1",
            DocumentId = "doc-1",
            Content = "text",
            ChunkIndex = 0,
            PageNumber = 1,
            ContentHash = "hash-1",
            TokenCount = 10,
            SourceFileName = "file.pdf",
            Embedding = null!
        };

        var act = async () => await sut.UpsertAsync([chunk]);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
