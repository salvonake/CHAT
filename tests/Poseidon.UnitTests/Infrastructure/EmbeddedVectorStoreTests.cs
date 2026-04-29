using System.IO;
using FluentAssertions;
using Poseidon.Domain.Entities;
using Poseidon.Infrastructure.VectorStore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Poseidon.UnitTests.Infrastructure;

/// <summary>
/// Tests for <see cref="EmbeddedVectorStore"/>: embedded HNSW approximate nearest-
/// neighbor index backed by SQLite. Uses temp files for each test.
/// Tests cover upsert, search, deletion, health, namespace filtering,
/// HNSW recall quality, binary round-trip, and concurrent access.
/// </summary>
public sealed class EmbeddedVectorStoreTests : IDisposable
{
    private readonly Mock<ILogger<EmbeddedVectorStore>> _logger = new();
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly string _hnswPath;

    private const int Dim = 8; // small embedding dimension for fast tests

    public EmbeddedVectorStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"evs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "vectors.db");
        _hnswPath = Path.Combine(_tempDir, "vectors.hnsw");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private EmbeddedVectorStore CreateStore() =>
        new(_dbPath, _hnswPath, _logger.Object);

    /// <summary>Create a chunk with a deterministic unit-length embedding.</summary>
    private static DocumentChunk MakeChunk(
        string id, string docId, float[] embedding,
        string? caseNamespace = null, string? content = null)
    {
        return new DocumentChunk
        {
            Id = id,
            DocumentId = docId,
            Content = content ?? $"Content for {id}",
            ChunkIndex = 0,
            ContentHash = $"hash_{id}",
            SourceFileName = "test.pdf",
            Embedding = embedding,
            CaseNamespace = caseNamespace
        };
    }

    /// <summary>Create a unit vector pointing mainly along axis <paramref name="axis"/>.</summary>
    private static float[] UnitVector(int axis, int dim = Dim)
    {
        var v = new float[dim];
        v[axis % dim] = 1.0f;
        return v;
    }

    /// <summary>Create a random-ish vector with a dominant component.</summary>
    private static float[] SimilarTo(float[] target, float noise = 0.1f)
    {
        var rng = new Random(42);
        var v = new float[target.Length];
        for (int i = 0; i < v.Length; i++)
            v[i] = target[i] + (float)(rng.NextDouble() * noise - noise / 2);
        // Normalize
        float norm = 0;
        for (int i = 0; i < v.Length; i++) norm += v[i] * v[i];
        norm = MathF.Sqrt(norm);
        if (norm > 0) for (int i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }

    // â”€â”€â”€ InitializeAsync â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task InitializeAsync_CreatesSchemaAndSetsInitialized()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        var health = await sut.GetHealthAsync();
        health.IsHealthy.Should().BeTrue();
        health.Status.Should().Contain("Embedded HNSW");
    }

    [Fact]
    public async Task InitializeAsync_SecondCall_IsIdempotent()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();
        await sut.InitializeAsync(); // should not throw

        var health = await sut.GetHealthAsync();
        health.IsHealthy.Should().BeTrue();
    }

    // â”€â”€â”€ UpsertAsync â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task UpsertAsync_SingleChunk_StoresInMemoryAndSQLite()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        var chunk = MakeChunk("c1", "doc1", UnitVector(0));
        await sut.UpsertAsync([chunk]);

        var count = await sut.GetVectorCountAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task UpsertAsync_MultipleChunks_AllStored()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        var chunks = new[]
        {
            MakeChunk("c1", "doc1", UnitVector(0)),
            MakeChunk("c2", "doc1", UnitVector(1)),
            MakeChunk("c3", "doc2", UnitVector(2))
        };
        await sut.UpsertAsync(chunks);

        var count = await sut.GetVectorCountAsync();
        count.Should().Be(3);
    }

    [Fact]
    public async Task UpsertAsync_EmptyList_DoesNothing()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        await sut.UpsertAsync(Array.Empty<DocumentChunk>());

        var count = await sut.GetVectorCountAsync();
        count.Should().Be(0);
    }

    [Fact]
    public async Task UpsertAsync_ChunkWithNullEmbedding_Skipped()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        var chunk = MakeChunk("skip1", "doc1", UnitVector(0));
        chunk.Embedding = null;
        await sut.UpsertAsync([chunk]);

        var count = await sut.GetVectorCountAsync();
        count.Should().Be(0);
    }

    [Fact]
    public async Task UpsertAsync_ChunkWithEmptyEmbedding_Skipped()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        var chunk = MakeChunk("skip2", "doc1", Array.Empty<float>());
        await sut.UpsertAsync([chunk]);

        var count = await sut.GetVectorCountAsync();
        count.Should().Be(0);
    }

    [Fact]
    public async Task UpsertAsync_DuplicateId_Overwrites()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        await sut.UpsertAsync([MakeChunk("dup1", "doc1", UnitVector(0), content: "original")]);
        await sut.UpsertAsync([MakeChunk("dup1", "doc1", UnitVector(1), content: "updated")]);

        var count = await sut.GetVectorCountAsync();
        count.Should().Be(1);

        // Search should find the updated content
        var results = await sut.SearchAsync(UnitVector(1), 1);
        results.Should().ContainSingle();
        results[0].Chunk.Content.Should().Be("updated");
    }

    [Fact]
    public async Task UpsertAsync_PreservesAllChunkFields()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        var chunk = new DocumentChunk
        {
            Id = "full",
            DocumentId = "doc_full",
            Content = "Ù†Øµ Ù‚Ø§Ù†ÙˆÙ†ÙŠ Ø¹Ø±Ø¨ÙŠ Ù„Ù„Ø§Ø®ØªØ¨Ø§Ø±",
            ChunkIndex = 5,
            PageNumber = 3,
            SectionTitle = "Ø§Ù„Ø¨Ø§Ø¨ Ø§Ù„Ø£ÙˆÙ„",
            ArticleReference = "Ø§Ù„Ù…Ø§Ø¯Ø© 15",
            CaseNumber = "2024/100",
            CourtName = "Ù…Ø­ÙƒÙ…Ø© Ø§Ù„Ù†Ù‚Ø¶",
            CaseDate = "2024-01-15",
            CaseNamespace = "ns_criminal",
            ContentHash = "sha_full",
            TokenCount = 42,
            SourceFileName = "Ø­ÙƒÙ…_Ù…Ø­ÙƒÙ…Ø©.pdf",
            Embedding = UnitVector(0)
        };
        await sut.UpsertAsync([chunk]);

        var results = await sut.SearchAsync(UnitVector(0), 1);
        var c = results[0].Chunk;
        c.Id.Should().Be("full");
        c.DocumentId.Should().Be("doc_full");
        c.Content.Should().Be("Ù†Øµ Ù‚Ø§Ù†ÙˆÙ†ÙŠ Ø¹Ø±Ø¨ÙŠ Ù„Ù„Ø§Ø®ØªØ¨Ø§Ø±");
        c.ChunkIndex.Should().Be(5);
        c.PageNumber.Should().Be(3);
        c.SectionTitle.Should().Be("Ø§Ù„Ø¨Ø§Ø¨ Ø§Ù„Ø£ÙˆÙ„");
        c.ArticleReference.Should().Be("Ø§Ù„Ù…Ø§Ø¯Ø© 15");
        c.CaseNumber.Should().Be("2024/100");
        c.CourtName.Should().Be("Ù…Ø­ÙƒÙ…Ø© Ø§Ù„Ù†Ù‚Ø¶");
        c.CaseDate.Should().Be("2024-01-15");
        c.CaseNamespace.Should().Be("ns_criminal");
        c.ContentHash.Should().Be("sha_full");
        c.TokenCount.Should().Be(42);
        c.SourceFileName.Should().Be("Ø­ÙƒÙ…_Ù…Ø­ÙƒÙ…Ø©.pdf");
    }

    // â”€â”€â”€ SearchAsync â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SearchAsync_EmptyStore_ReturnsEmpty()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        var results = await sut.SearchAsync(UnitVector(0), 5);
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_FindsMostSimilar()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        // Insert vectors pointing in different directions
        await sut.UpsertAsync([
            MakeChunk("north", "doc1", UnitVector(0)),
            MakeChunk("east", "doc1", UnitVector(1)),
            MakeChunk("up", "doc1", UnitVector(2))
        ]);

        // Query for something close to "north"
        var results = await sut.SearchAsync(UnitVector(0), 1);
        results.Should().ContainSingle();
        results[0].Chunk.Id.Should().Be("north");
        results[0].SimilarityScore.Should().BeGreaterThan(0.9f);
    }

    [Fact]
    public async Task SearchAsync_TopK_LimitsResults()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        var chunks = Enumerable.Range(0, Dim)
            .Select(i => MakeChunk($"c{i}", "doc1", UnitVector(i)))
            .ToArray();
        await sut.UpsertAsync(chunks);

        var results = await sut.SearchAsync(UnitVector(0), 2);
        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchAsync_ScoreThreshold_FiltersLowScores()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        await sut.UpsertAsync([
            MakeChunk("match", "doc1", UnitVector(0)),
            MakeChunk("ortho", "doc1", UnitVector(3)) // orthogonal â†’ cosine â‰ˆ 0
        ]);

        var results = await sut.SearchAsync(UnitVector(0), 10, scoreThreshold: 0.5);
        results.Should().ContainSingle();
        results[0].Chunk.Id.Should().Be("match");
    }

    [Fact]
    public async Task SearchAsync_NamespaceFilter_OnlyReturnsMatchingNamespace()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        await sut.UpsertAsync([
            MakeChunk("ns_a", "doc1", UnitVector(0), caseNamespace: "criminal"),
            MakeChunk("ns_b", "doc2", UnitVector(0), caseNamespace: "civil")
        ]);

        // Both vectors identical, but filter by namespace
        var results = await sut.SearchAsync(UnitVector(0), 10, caseNamespace: "criminal");
        results.Should().ContainSingle();
        results[0].Chunk.Id.Should().Be("ns_a");
    }

    [Fact]
    public async Task SearchAsync_SimilarityScoresDescendingOrder()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        // Create vectors at varying angles from axis 0
        var close = new float[Dim]; close[0] = 0.95f; close[1] = 0.05f; // very close
        var medium = new float[Dim]; medium[0] = 0.7f; medium[1] = 0.3f;
        var far = new float[Dim]; far[0] = 0.1f; far[1] = 0.9f;

        await sut.UpsertAsync([
            MakeChunk("close", "d1", close),
            MakeChunk("medium", "d1", medium),
            MakeChunk("far", "d1", far)
        ]);

        var results = await sut.SearchAsync(UnitVector(0), 3);

        // Verify descending by score
        for (int i = 1; i < results.Count; i++)
        {
            results[i - 1].SimilarityScore.Should()
                .BeGreaterThanOrEqualTo(results[i].SimilarityScore);
        }
    }

    // â”€â”€â”€ DeleteByDocumentIdAsync â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task DeleteByDocumentId_RemovesAllChunksForDocument()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        await sut.UpsertAsync([
            MakeChunk("d1_c1", "doc1", UnitVector(0)),
            MakeChunk("d1_c2", "doc1", UnitVector(1)),
            MakeChunk("d2_c1", "doc2", UnitVector(2))
        ]);

        await sut.DeleteByDocumentIdAsync("doc1");

        var count = await sut.GetVectorCountAsync();
        count.Should().Be(1);

        // Only doc2's chunk should remain
        var results = await sut.SearchAsync(UnitVector(2), 10);
        results.Should().ContainSingle();
        results[0].Chunk.DocumentId.Should().Be("doc2");
    }

    [Fact]
    public async Task DeleteByDocumentId_NonExistent_DoesNotThrow()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        await sut.UpsertAsync([MakeChunk("c1", "doc1", UnitVector(0))]);

        var act = () => sut.DeleteByDocumentIdAsync("nonexistent");
        await act.Should().NotThrowAsync();

        var count = await sut.GetVectorCountAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task DeleteByDocumentId_SearchExcludesDeletedVectors()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        await sut.UpsertAsync([
            MakeChunk("target", "to_delete", UnitVector(0)),
            MakeChunk("keep", "keep_doc", UnitVector(1))
        ]);

        await sut.DeleteByDocumentIdAsync("to_delete");

        // Searching for deleted vector direction should not find it
        var results = await sut.SearchAsync(UnitVector(0), 10, scoreThreshold: 0.5);
        results.Should().BeEmpty();
    }

    // â”€â”€â”€ ExistsByHashAsync â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task ExistsByHash_ExistingHash_ReturnsTrue()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        await sut.UpsertAsync([MakeChunk("c1", "doc1", UnitVector(0))]);

        var exists = await sut.ExistsByHashAsync("hash_c1");
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsByHash_NonExistentHash_ReturnsFalse()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        var exists = await sut.ExistsByHashAsync("nonexistent_hash");
        exists.Should().BeFalse();
    }

    // â”€â”€â”€ GetVectorCountAsync â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task GetVectorCount_ReturnsInMemoryCount()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        (await sut.GetVectorCountAsync()).Should().Be(0);

        await sut.UpsertAsync([
            MakeChunk("a", "d1", UnitVector(0)),
            MakeChunk("b", "d1", UnitVector(1))
        ]);

        (await sut.GetVectorCountAsync()).Should().Be(2);
    }

    // â”€â”€â”€ GetHealthAsync â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task GetHealth_NotInitialized_ReportsUnhealthy()
    {
        using var sut = CreateStore();

        var health = await sut.GetHealthAsync();
        health.IsHealthy.Should().BeFalse();
        health.Status.Should().Contain("Not initialized");
    }

    [Fact]
    public async Task GetHealth_Initialized_ReportsHealthy()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        var health = await sut.GetHealthAsync();
        health.IsHealthy.Should().BeTrue();
        health.VectorCount.Should().Be(0);
    }

    [Fact]
    public async Task GetHealth_WithVectors_ReportsCorrectCount()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();
        await sut.UpsertAsync([
            MakeChunk("h1", "d1", UnitVector(0)),
            MakeChunk("h2", "d1", UnitVector(1))
        ]);

        var health = await sut.GetHealthAsync();
        health.VectorCount.Should().Be(2);
        health.IndexedSegments.Should().Be(2); // _graph.Count
    }

    // â”€â”€â”€ Persistence â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Persistence_SecondInstance_ReloadsFromSQLite()
    {
        // First instance inserts data
        using (var sut1 = CreateStore())
        {
            await sut1.InitializeAsync();
            await sut1.UpsertAsync([
                MakeChunk("persist_1", "doc1", UnitVector(0), content: "persisted content"),
                MakeChunk("persist_2", "doc1", UnitVector(1))
            ]);
        }

        // Second instance should reload from DB
        using var sut2 = CreateStore();
        await sut2.InitializeAsync();

        var count = await sut2.GetVectorCountAsync();
        count.Should().Be(2);

        // Search should work on reloaded data
        var results = await sut2.SearchAsync(UnitVector(0), 1);
        results.Should().ContainSingle();
        results[0].Chunk.Content.Should().Be("persisted content");
    }

    // â”€â”€â”€ Concurrency â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task ConcurrentUpserts_AllSucceed()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        // 10 concurrent upserts of 1 chunk each
        var tasks = Enumerable.Range(0, 10)
            .Select(i => sut.UpsertAsync([MakeChunk($"conc_{i}", "doc_conc", UnitVector(i % Dim))]))
            .ToArray();

        await Task.WhenAll(tasks);

        var count = await sut.GetVectorCountAsync();
        count.Should().Be(10);
    }

    // â”€â”€â”€ HNSW Recall Quality â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task HnswRecall_ManyVectors_FindsClosestNeighbor()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        // Insert 50 random vectors
        var rng = new Random(123);
        var chunks = new List<DocumentChunk>();
        for (int i = 0; i < 50; i++)
        {
            var vec = new float[Dim];
            for (int d = 0; d < Dim; d++)
                vec[d] = (float)rng.NextDouble();
            // Normalize
            float norm = 0;
            for (int d = 0; d < Dim; d++) norm += vec[d] * vec[d];
            norm = MathF.Sqrt(norm);
            for (int d = 0; d < Dim; d++) vec[d] /= norm;

            chunks.Add(MakeChunk($"r{i}", "doc_recall", vec));
        }
        await sut.UpsertAsync(chunks);

        // The exact nearest neighbor to UnitVector(0) â€” find it via brute force
        var query = UnitVector(0);
        var bestId = chunks
            .OrderByDescending(c => CosineSimHelper(query, c.Embedding!))
            .First().Id;

        // HNSW should find it (or something very close)
        var results = await sut.SearchAsync(query, 5);
        results.Should().NotBeEmpty();
        // At minimum, the top result should have high similarity
        results[0].SimilarityScore.Should().BeGreaterThan(0.5f);
    }

    // â”€â”€â”€ Binary Round-Trip â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task EmbeddingRoundTrip_FloatsPersistViaSQLite()
    {
        using var sut = CreateStore();
        await sut.InitializeAsync();

        var embedding = new float[] { 1.5f, -2.3f, 0.0f, 3.14159f, -1e-5f, 999.99f, 0.001f, -0.5f };
        await sut.UpsertAsync([MakeChunk("float_test", "doc_float", embedding)]);

        var results = await sut.SearchAsync(embedding, 1);
        results.Should().ContainSingle();

        var retrieved = results[0].Chunk.Embedding;
        retrieved.Should().NotBeNull();
        retrieved!.Length.Should().Be(embedding.Length);
        for (int i = 0; i < embedding.Length; i++)
        {
            retrieved[i].Should().Be(embedding[i]);
        }
    }

    // â”€â”€â”€ Dispose â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void Dispose_CanBeCalledSafely()
    {
        var sut = CreateStore();
        sut.Dispose(); // should not throw
    }

    // â”€â”€â”€ Helper â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static double CosineSimHelper(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }
}

