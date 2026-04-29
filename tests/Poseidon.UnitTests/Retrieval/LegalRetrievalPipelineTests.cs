using FluentAssertions;
using Poseidon.Domain.Entities;
using Poseidon.Domain.Interfaces;
using Poseidon.Domain.ValueObjects;
using Poseidon.Retrieval.Lexical;
using Poseidon.Retrieval.Pipeline;
using Poseidon.Retrieval.QueryAnalysis;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace Poseidon.UnitTests.Retrieval;

/// <summary>
/// Tests for LegalRetrievalPipeline â€” the multi-stage retrieval pipeline.
/// Covers: caching, query analysis, vector search, BM25 lexical search,
/// hybrid fusion, deduplication, reranking placeholder, context budget,
/// BuildContext helpers, NormalizeBm25, and ComputeCacheKey.
/// </summary>
public sealed class LegalRetrievalPipelineTests : IDisposable
{
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<IEmbeddingService> _embedder = new();
    private readonly Mock<IMetricsCollector> _metrics = new();
    private readonly Mock<ILogger<LegalRetrievalPipeline>> _logger = new();
    private readonly LegalQueryAnalyzer _queryAnalyzer;
    private readonly BM25Index _bm25Index = new();
    private readonly MemoryCache _cache;

    public LegalRetrievalPipelineTests()
    {
        _queryAnalyzer = new LegalQueryAnalyzer(new Mock<ILogger<LegalQueryAnalyzer>>().Object);
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    public void Dispose() => _cache.Dispose();

    private LegalRetrievalPipeline CreatePipeline() =>
        new(_vectorStore.Object, _embedder.Object, _queryAnalyzer, _bm25Index,
            _cache, _metrics.Object, _logger.Object);

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static RetrievalConfig DefaultConfig(
        int topK = 5,
        bool hybrid = false,
        bool rerank = false) => new()
    {
        TopK = topK,
        EnableHybridSearch = hybrid,
        EnableReranking = rerank,
        SimilarityThreshold = 0.0,
        MaxContextTokens = 4096,
        StrictMode = true
    };

    private static DocumentChunk MakeChunk(
        string id = "chunk1",
        string content = "Ù†Øµ Ù‚Ø§Ù†ÙˆÙ†ÙŠ Ù„Ù„Ø§Ø®ØªØ¨Ø§Ø±",
        string sourceFile = "law.pdf",
        int page = 1,
        string hash = "hash1",
        int tokens = 50,
        string? article = null,
        string? caseNum = null,
        string? court = null) => new()
    {
        Id = id,
        DocumentId = "doc1",
        Content = content,
        ChunkIndex = 0,
        PageNumber = page,
        ContentHash = hash,
        SourceFileName = sourceFile,
        TokenCount = tokens,
        ArticleReference = article,
        CaseNumber = caseNum,
        CourtName = court
    };

    private static RetrievedChunk MakeRetrievedChunk(
        string id = "chunk1",
        float sim = 0.9f,
        string content = "Ù†Øµ Ù‚Ø§Ù†ÙˆÙ†ÙŠ Ù„Ù„Ø§Ø®ØªØ¨Ø§Ø±",
        string hash = "hash1",
        string sourceFile = "law.pdf",
        int page = 1,
        int tokens = 50,
        string? article = null,
        string? caseNum = null,
        string? court = null) => new()
    {
        Chunk = MakeChunk(id, content, sourceFile, page, hash, tokens, article, caseNum, court),
        SimilarityScore = sim
    };

    private void SetupEmbedding(float[]? embedding = null)
    {
        _embedder.Setup(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding ?? new float[] { 0.1f, 0.2f, 0.3f });
    }

    private void SetupVectorSearch(params RetrievedChunk[] chunks)
    {
        _vectorStore.Setup(v => v.SearchAsync(
                It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<double>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks.ToList());
    }

    private void SetupScopedVectorSearch(params RetrievedChunk[] chunks)
    {
        _vectorStore.Setup(v => v.SearchAsync(
                It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<double>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(chunks.ToList());
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Cache
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task RetrieveAsync_CacheHit_ReturnsCachedResult()
    {
        SetupEmbedding();
        var chunk = MakeRetrievedChunk();
        SetupVectorSearch(chunk);

        var pipeline = CreatePipeline();
        var config = DefaultConfig(hybrid: false);

        // First call â€” populates cache
        var result1 = await pipeline.RetrieveAsync("test query", config, null, CancellationToken.None);
        // Second call â€” should hit cache
        var result2 = await pipeline.RetrieveAsync("test query", config, null, CancellationToken.None);

        result2.Should().BeSameAs(result1);
        _metrics.Verify(m => m.IncrementCounter("cache_hit", It.IsAny<long>()), Times.Once);
        _metrics.Verify(m => m.IncrementCounter("cache_miss", It.IsAny<long>()), Times.Once);
    }

    [Fact]
    public async Task RetrieveAsync_DifferentQuery_CacheMiss()
    {
        SetupEmbedding();
        SetupVectorSearch(MakeRetrievedChunk());

        var pipeline = CreatePipeline();
        var config = DefaultConfig(hybrid: false);

        await pipeline.RetrieveAsync("query1", config, null, CancellationToken.None);
        await pipeline.RetrieveAsync("query2", config, null, CancellationToken.None);

        _metrics.Verify(m => m.IncrementCounter("cache_miss", It.IsAny<long>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RetrieveAsync_DifferentNamespace_CacheMiss()
    {
        SetupEmbedding();
        SetupVectorSearch(MakeRetrievedChunk());

        var pipeline = CreatePipeline();
        var config = DefaultConfig(hybrid: false);

        await pipeline.RetrieveAsync("query", config, "ns1", CancellationToken.None);
        await pipeline.RetrieveAsync("query", config, "ns2", CancellationToken.None);

        _metrics.Verify(m => m.IncrementCounter("cache_miss", It.IsAny<long>()), Times.Exactly(2));
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Vector Search (Stage 2a)
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task RetrieveAsync_CallsEmbedder_ForEachVariant()
    {
        SetupEmbedding();
        SetupVectorSearch(MakeRetrievedChunk());

        var pipeline = CreatePipeline();
        // The query analyzer generates semantic variants; each gets embedded
        await pipeline.RetrieveAsync("Ø§Ø´Ø±Ø­ Ø§Ù„Ù‚Ø§Ù†ÙˆÙ†", DefaultConfig(hybrid: false), null, CancellationToken.None);

        // At least 1 embed call (for the normalized query + any variants)
        _embedder.Verify(e => e.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RetrieveAsync_PassesNamespaceToVectorStore()
    {
        SetupEmbedding();
        SetupVectorSearch(MakeRetrievedChunk());

        var pipeline = CreatePipeline();
        await pipeline.RetrieveAsync("query", DefaultConfig(hybrid: false), "myns", CancellationToken.None);

        _vectorStore.Verify(v => v.SearchAsync(
            It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<double>(),
            "myns", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RetrieveAsync_PassesExplicitDomainAndDatasetFilters_ToVectorStore()
    {
        SetupEmbedding();
        SetupScopedVectorSearch(MakeRetrievedChunk());

        var pipeline = CreatePipeline();
        await pipeline.RetrieveAsync(
            "query",
            DefaultConfig(hybrid: false),
            "legal:contracts",
            CancellationToken.None,
            "legal",
            "contracts");

        _vectorStore.Verify(v => v.SearchAsync(
            It.IsAny<float[]>(),
            It.IsAny<int>(),
            It.IsAny<double>(),
            "legal:contracts",
            It.IsAny<CancellationToken>(),
            "legal",
            "contracts"), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RetrieveAsync_DerivesDomainAndDatasetFilters_FromNamespace()
    {
        SetupEmbedding();
        SetupScopedVectorSearch(MakeRetrievedChunk());

        var pipeline = CreatePipeline();
        await pipeline.RetrieveAsync(
            "query",
            DefaultConfig(hybrid: false),
            "legal:contracts",
            CancellationToken.None);

        _vectorStore.Verify(v => v.SearchAsync(
            It.IsAny<float[]>(),
            It.IsAny<int>(),
            It.IsAny<double>(),
            "legal:contracts",
            It.IsAny<CancellationToken>(),
            "legal",
            "contracts"), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RetrieveAsync_NoResults_ReturnsEmptyChunks()
    {
        SetupEmbedding();
        SetupVectorSearch(); // empty

        var pipeline = CreatePipeline();
        var result = await pipeline.RetrieveAsync("query", DefaultConfig(hybrid: false), null, CancellationToken.None);

        result.Chunks.Should().BeEmpty();
        result.AverageSimilarity.Should().Be(0);
    }

    [Fact]
    public async Task RetrieveAsync_ReturnsTopK_Chunks()
    {
        SetupEmbedding();
        var chunks = Enumerable.Range(1, 20)
            .Select(i => MakeRetrievedChunk($"c{i}", 1.0f - i * 0.01f, hash: $"h{i}"))
            .ToArray();
        SetupVectorSearch(chunks);

        var pipeline = CreatePipeline();
        var result = await pipeline.RetrieveAsync("query", DefaultConfig(topK: 3, hybrid: false), null, CancellationToken.None);

        result.Chunks.Should().HaveCount(3);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Deduplication
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task RetrieveAsync_DuplicateContentHashes_Deduplicated()
    {
        SetupEmbedding();
        // Two chunks with same content hash but different IDs
        var c1 = MakeRetrievedChunk("id1", 0.9f, hash: "samehash");
        var c2 = MakeRetrievedChunk("id2", 0.8f, hash: "samehash");
        SetupVectorSearch(c1, c2);

        var pipeline = CreatePipeline();
        var result = await pipeline.RetrieveAsync("query", DefaultConfig(hybrid: false), null, CancellationToken.None);

        result.Chunks.Should().HaveCount(1);
        // Should keep the one with higher score
        result.Chunks[0].SimilarityScore.Should().Be(0.9f);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Hybrid Search (BM25 Fusion)
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task RetrieveAsync_HybridEnabled_WithBm25Docs_FusesScores()
    {
        SetupEmbedding();
        var chunk = MakeRetrievedChunk("bm25match", 0.7f, hash: "h1");
        SetupVectorSearch(chunk);

        // Add document to BM25 index with matching ID
        _bm25Index.AddDocument("bm25match", "some legal text about law");

        var pipeline = CreatePipeline();
        var config = DefaultConfig(hybrid: true);
        var result = await pipeline.RetrieveAsync("legal text", config, null, CancellationToken.None);

        result.Chunks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RetrieveAsync_HybridDisabled_SkipsBm25()
    {
        SetupEmbedding();
        SetupVectorSearch(MakeRetrievedChunk());

        // Even with BM25 docs, hybrid disabled means no fusion
        _bm25Index.AddDocument("doc1", "text");

        var pipeline = CreatePipeline();
        var result = await pipeline.RetrieveAsync("text", DefaultConfig(hybrid: false), null, CancellationToken.None);

        // Should still return vector results
        result.Chunks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RetrieveAsync_HybridEnabled_NoBm25Docs_SkipsFusion()
    {
        SetupEmbedding();
        SetupVectorSearch(MakeRetrievedChunk());

        var pipeline = CreatePipeline();
        // BM25 is empty (DocumentCount == 0), so fusion is skipped
        var result = await pipeline.RetrieveAsync("query", DefaultConfig(hybrid: true), null, CancellationToken.None);

        result.Chunks.Should().NotBeEmpty();
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Reranking (Stage 3)
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task RetrieveAsync_RerankEnabled_RecordsRerankLatency()
    {
        SetupEmbedding();
        SetupVectorSearch(MakeRetrievedChunk());

        var pipeline = CreatePipeline();
        var config = DefaultConfig(rerank: true);
        var result = await pipeline.RetrieveAsync("query", config, null, CancellationToken.None);

        result.RerankLatencyMs.Should().NotBeNull();
        result.RerankLatencyMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task RetrieveAsync_RerankDisabled_NoRerankLatency()
    {
        SetupEmbedding();
        SetupVectorSearch(MakeRetrievedChunk());

        var pipeline = CreatePipeline();
        var config = DefaultConfig(rerank: false);
        var result = await pipeline.RetrieveAsync("query", config, null, CancellationToken.None);

        result.RerankLatencyMs.Should().BeNull();
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Context Budget (Stage 4 â€” BuildContext)
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task RetrieveAsync_ContextIncludesSourceHeaders()
    {
        SetupEmbedding();
        var chunk = MakeRetrievedChunk(sourceFile: "contract.pdf", page: 5, article: "Ø§Ù„Ù…Ø§Ø¯Ø© 12", caseNum: "Q-789", court: "Ù…Ø­ÙƒÙ…Ø© Ø§Ù„Ù†Ù‚Ø¶");
        SetupVectorSearch(chunk);

        var pipeline = CreatePipeline();
        var result = await pipeline.RetrieveAsync("query", DefaultConfig(hybrid: false), null, CancellationToken.None);

        result.AssembledContext.Should().Contain("contract.pdf");
        result.AssembledContext.Should().Contain("Ø§Ù„Ù…ØµØ¯Ø±");
        result.AssembledContext.Should().Contain("Ø§Ù„Ù…Ø§Ø¯Ø© 12");
        result.AssembledContext.Should().Contain("Q-789");
        result.AssembledContext.Should().Contain("Ù…Ø­ÙƒÙ…Ø© Ø§Ù„Ù†Ù‚Ø¶");
    }

    [Fact]
    public async Task RetrieveAsync_EmptyChunks_EmptyContext()
    {
        SetupEmbedding();
        SetupVectorSearch(); // empty

        var pipeline = CreatePipeline();
        var result = await pipeline.RetrieveAsync("query", DefaultConfig(hybrid: false), null, CancellationToken.None);

        result.AssembledContext.Should().BeEmpty();
        result.CompressionRatio.Should().Be(0);
    }

    [Fact]
    public async Task RetrieveAsync_ChunksExceedTokenBudget_Truncated()
    {
        SetupEmbedding();
        // 3 chunks, each 2000 tokens, budget is 4096
        var chunks = new[]
        {
            MakeRetrievedChunk("c1", 0.9f, hash: "h1", tokens: 2000),
            MakeRetrievedChunk("c2", 0.8f, hash: "h2", content: "Ù†Øµ Ø«Ø§Ù†ÙŠ", tokens: 2000),
            MakeRetrievedChunk("c3", 0.7f, hash: "h3", content: "Ù†Øµ Ø«Ø§Ù„Ø«", tokens: 2000),
        };
        SetupVectorSearch(chunks);

        var pipeline = CreatePipeline();
        var config = new RetrievalConfig
        {
            TopK = 5,
            MaxContextTokens = 4096,
            EnableHybridSearch = false,
            EnableReranking = false,
            SimilarityThreshold = 0.0
        };
        var result = await pipeline.RetrieveAsync("query", config, null, CancellationToken.None);

        // Only first 2 chunks fit the 4096 budget (2000 + 2000 = 4000 â‰¤ 4096)
        // Third would push to 6000 > 4096
        result.CompressionRatio.Should().BeLessThan(1.0);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Result Structure
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task RetrieveAsync_PopulatesQueryAnalysis()
    {
        SetupEmbedding();
        SetupVectorSearch(MakeRetrievedChunk());

        var pipeline = CreatePipeline();
        var result = await pipeline.RetrieveAsync("test query", DefaultConfig(hybrid: false), null, CancellationToken.None);

        result.QueryAnalysis.Should().NotBeNull();
        result.QueryAnalysis.NormalizedQuery.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RetrieveAsync_ComputesAverageSimilarity()
    {
        SetupEmbedding();
        var chunks = new[]
        {
            MakeRetrievedChunk("c1", 0.9f, hash: "h1"),
            MakeRetrievedChunk("c2", 0.7f, hash: "h2"),
        };
        SetupVectorSearch(chunks);

        var pipeline = CreatePipeline();
        var result = await pipeline.RetrieveAsync("query", DefaultConfig(topK: 5, hybrid: false), null, CancellationToken.None);

        result.AverageSimilarity.Should().BeApproximately(0.8, 0.001);
    }

    [Fact]
    public async Task RetrieveAsync_RecordsPipelineLatency()
    {
        SetupEmbedding();
        SetupVectorSearch(MakeRetrievedChunk());

        var pipeline = CreatePipeline();
        var result = await pipeline.RetrieveAsync("query", DefaultConfig(hybrid: false), null, CancellationToken.None);

        result.RetrievalLatencyMs.Should().BeGreaterThanOrEqualTo(0);
        _metrics.Verify(m => m.RecordLatency("retrieval_pipeline", It.IsAny<double>()), Times.Once);
        _metrics.Verify(m => m.SetGauge("avg_similarity", It.IsAny<double>()), Times.Once);
    }

    [Fact]
    public async Task RetrieveAsync_TracksTotalCandidatesEvaluated()
    {
        SetupEmbedding();
        var chunks = Enumerable.Range(1, 10)
            .Select(i => MakeRetrievedChunk($"c{i}", 1.0f - i * 0.05f, hash: $"h{i}"))
            .ToArray();
        SetupVectorSearch(chunks);

        var pipeline = CreatePipeline();
        var result = await pipeline.RetrieveAsync("query", DefaultConfig(topK: 3, hybrid: false), null, CancellationToken.None);

        result.TotalCandidatesEvaluated.Should().BeGreaterThanOrEqualTo(3);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Ordering
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task RetrieveAsync_ResultsOrderedByFinalScoreDescending()
    {
        SetupEmbedding();
        var chunks = new[]
        {
            MakeRetrievedChunk("c1", 0.5f, hash: "h1"),
            MakeRetrievedChunk("c2", 0.9f, hash: "h2"),
            MakeRetrievedChunk("c3", 0.7f, hash: "h3"),
        };
        SetupVectorSearch(chunks);

        var pipeline = CreatePipeline();
        var result = await pipeline.RetrieveAsync("query", DefaultConfig(topK: 10, hybrid: false), null, CancellationToken.None);

        result.Chunks.Should().BeInDescendingOrder(c => c.FinalScore);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Cancellation Token
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task RetrieveAsync_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        _embedder.Setup(e => e.EmbedAsync(It.IsAny<string>(), token))
            .ReturnsAsync(new float[] { 0.1f });
        _vectorStore.Setup(v => v.SearchAsync(
                It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<double>(),
                It.IsAny<string?>(), token))
            .ReturnsAsync(new List<RetrievedChunk> { MakeRetrievedChunk() });

        var pipeline = CreatePipeline();
        await pipeline.RetrieveAsync("query", DefaultConfig(hybrid: false), null, token);

        _embedder.Verify(e => e.EmbedAsync(It.IsAny<string>(), token), Times.AtLeastOnce);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Context Content
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task RetrieveAsync_ContextContainsChunkContent()
    {
        SetupEmbedding();
        var chunk = MakeRetrievedChunk(content: "Ù‡Ø°Ø§ Ù†Øµ Ù…Ù‡Ù… Ø¬Ø¯Ø§Ù‹ Ù„Ù„Ù‚Ø§Ù†ÙˆÙ†");
        SetupVectorSearch(chunk);

        var pipeline = CreatePipeline();
        var result = await pipeline.RetrieveAsync("query", DefaultConfig(hybrid: false), null, CancellationToken.None);

        result.AssembledContext.Should().Contain("Ù‡Ø°Ø§ Ù†Øµ Ù…Ù‡Ù… Ø¬Ø¯Ø§Ù‹ Ù„Ù„Ù‚Ø§Ù†ÙˆÙ†");
    }

    [Fact]
    public async Task RetrieveAsync_ContextIncludesChunkNumbering()
    {
        SetupEmbedding();
        var chunks = new[]
        {
            MakeRetrievedChunk("c1", 0.9f, hash: "h1", sourceFile: "a.pdf"),
            MakeRetrievedChunk("c2", 0.8f, hash: "h2", sourceFile: "b.pdf"),
        };
        SetupVectorSearch(chunks);

        var pipeline = CreatePipeline();
        var result = await pipeline.RetrieveAsync("query", DefaultConfig(topK: 5, hybrid: false), null, CancellationToken.None);

        result.AssembledContext.Should().Contain("Ø§Ù„Ù…ØµØ¯Ø± 1");
        result.AssembledContext.Should().Contain("Ø§Ù„Ù…ØµØ¯Ø± 2");
    }
}

