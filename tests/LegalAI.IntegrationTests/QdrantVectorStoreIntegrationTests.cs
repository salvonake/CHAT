using FluentAssertions;
using LegalAI.Domain.Entities;
using LegalAI.Infrastructure.VectorStore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LegalAI.IntegrationTests;

public sealed class QdrantVectorStoreIntegrationTests
{
    private readonly string _host;
    private readonly int _port;
    private readonly bool _enabled;

    public QdrantVectorStoreIntegrationTests()
    {
        (_host, _port) = IntegrationTestGate.GetQdrantEndpoint();
        _enabled = IntegrationTestGate.IsIntegrationEnabled()
            && IntegrationTestGate.IsQdrantReachable(_host, _port);
    }

    private QdrantVectorStore CreateSut(string collectionName, int dim = 4)
    {
        return new QdrantVectorStore(
            host: _host,
            port: _port,
            collectionName: collectionName,
            embeddingDimension: dim,
            logger: NullLogger<QdrantVectorStore>.Instance);
    }

    private static DocumentChunk MakeChunk(
        string documentId,
        string contentHash,
        string caseNamespace,
        float[] embedding,
        string content = "Sample legal text")
    {
        return new DocumentChunk
        {
            Id = Guid.NewGuid().ToString("N"),
            DocumentId = documentId,
            Content = content,
            ChunkIndex = 0,
            PageNumber = 1,
            SectionTitle = "Section 1",
            ArticleReference = "Art-1",
            CaseNumber = "Case-100",
            CourtName = "Court A",
            CaseDate = "2026-01-01",
            CaseNamespace = caseNamespace,
            ContentHash = contentHash,
            TokenCount = 10,
            SourceFileName = "doc.pdf",
            Embedding = embedding
        };
    }

    [Fact]
    public async Task InitializeAsync_CreatesCollection_AndHealthIsAvailable()
    {
        if (!_enabled) return;

        var collection = $"legal_chunks_it_{Guid.NewGuid():N}";
        var sut = CreateSut(collection);

        await sut.InitializeAsync();
        var health = await sut.GetHealthAsync();

        health.Status.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task UpsertAndSearch_ReturnsInsertedChunk()
    {
        if (!_enabled) return;

        var collection = $"legal_chunks_it_{Guid.NewGuid():N}";
        var sut = CreateSut(collection);
        await sut.InitializeAsync();

        var chunk = MakeChunk(
            documentId: "doc-1",
            contentHash: "hash-1",
            caseNamespace: "ns-a",
            embedding: [0.99f, 0.01f, 0f, 0f],
            content: "Contract clause text");

        await sut.UpsertAsync([chunk]);
        await Task.Delay(250);

        var results = await sut.SearchAsync(
            queryEmbedding: [1f, 0f, 0f, 0f],
            topK: 3,
            scoreThreshold: 0.0,
            caseNamespace: null);

        results.Should().NotBeEmpty();
        results.Select(r => r.Chunk.DocumentId).Should().Contain("doc-1");
    }

    [Fact]
    public async Task SearchAsync_WithNamespaceFilter_ReturnsOnlyMatchingNamespace()
    {
        if (!_enabled) return;

        var collection = $"legal_chunks_it_{Guid.NewGuid():N}";
        var sut = CreateSut(collection);
        await sut.InitializeAsync();

        var chunkA = MakeChunk("doc-a", "hash-a", "case-a", [1f, 0f, 0f, 0f], "A text");
        var chunkB = MakeChunk("doc-b", "hash-b", "case-b", [1f, 0f, 0f, 0f], "B text");

        await sut.UpsertAsync([chunkA, chunkB]);
        await Task.Delay(250);

        var filtered = await sut.SearchAsync(
            queryEmbedding: [1f, 0f, 0f, 0f],
            topK: 10,
            scoreThreshold: 0,
            caseNamespace: "case-a");

        filtered.Should().NotBeEmpty();
        filtered.Should().OnlyContain(r => r.Chunk.CaseNamespace == "case-a");
    }

    [Fact]
    public async Task ExistsByHashAsync_ReflectsUpsertedData()
    {
        if (!_enabled) return;

        var collection = $"legal_chunks_it_{Guid.NewGuid():N}";
        var sut = CreateSut(collection);
        await sut.InitializeAsync();

        await sut.ExistsByHashAsync("missing-hash").ContinueWith(t => t.Result.Should().BeFalse());

        var chunk = MakeChunk("doc-2", "hash-exists", "ns", [0f, 1f, 0f, 0f]);
        await sut.UpsertAsync([chunk]);
        await Task.Delay(250);

        var exists = await sut.ExistsByHashAsync("hash-exists");
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteByDocumentIdAsync_RemovesVectorsFromSearch()
    {
        if (!_enabled) return;

        var collection = $"legal_chunks_it_{Guid.NewGuid():N}";
        var sut = CreateSut(collection);
        await sut.InitializeAsync();

        var chunk = MakeChunk("doc-delete", "hash-delete", "ns", [0f, 0f, 1f, 0f]);
        await sut.UpsertAsync([chunk]);
        await Task.Delay(250);

        var before = await sut.SearchAsync([0f, 0f, 1f, 0f], topK: 5, scoreThreshold: 0, caseNamespace: null);
        before.Select(r => r.Chunk.DocumentId).Should().Contain("doc-delete");

        await sut.DeleteByDocumentIdAsync("doc-delete");
        await Task.Delay(350);

        var after = await sut.SearchAsync([0f, 0f, 1f, 0f], topK: 5, scoreThreshold: 0, caseNamespace: null);
        after.Select(r => r.Chunk.DocumentId).Should().NotContain("doc-delete");
    }

    [Fact]
    public async Task GetVectorCountAsync_IncreasesAfterUpsert()
    {
        if (!_enabled) return;

        var collection = $"legal_chunks_it_{Guid.NewGuid():N}";
        var sut = CreateSut(collection);
        await sut.InitializeAsync();

        var countBefore = await sut.GetVectorCountAsync();

        var chunk1 = MakeChunk("doc-count-1", "hash-count-1", "ns", [0f, 0f, 0f, 1f]);
        var chunk2 = MakeChunk("doc-count-2", "hash-count-2", "ns", [0f, 0f, 0f, 1f]);
        await sut.UpsertAsync([chunk1, chunk2]);
        await Task.Delay(250);

        var countAfter = await sut.GetVectorCountAsync();
        countAfter.Should().BeGreaterThanOrEqualTo(countBefore + 2);
    }
}
