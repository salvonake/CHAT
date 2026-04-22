using System.Diagnostics;
using System.Text;
using LegalAI.Application.Services;
using LegalAI.Domain.Entities;
using LegalAI.Domain.Interfaces;
using LegalAI.Domain.ValueObjects;
using LegalAI.Retrieval.Lexical;
using LegalAI.Retrieval.QueryAnalysis;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace LegalAI.Retrieval.Pipeline;

/// <summary>
/// Multi-stage retrieval pipeline implementing:
/// Stage 1: Query Analysis (type detection, variant generation, normalization)
/// Stage 2: Hybrid Retrieval (vector search + BM25 lexical search + fusion)
/// Stage 3: Reranking (optional cross-encoder)
/// Stage 4: Context Budget Optimization (packing, deduplication, compression)
/// </summary>
public sealed class LegalRetrievalPipeline : IRetrievalPipeline
{
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingService _embedder;
    private readonly IDomainQueryAnalyzer _queryAnalyzer;
    private readonly BM25Index _bm25Index;
    private readonly IMemoryCache _cache;
    private readonly IMetricsCollector _metrics;
    private readonly ILogger<LegalRetrievalPipeline> _logger;

    public LegalRetrievalPipeline(
        IVectorStore vectorStore,
        IEmbeddingService embedder,
        IDomainQueryAnalyzer queryAnalyzer,
        BM25Index bm25Index,
        IMemoryCache cache,
        IMetricsCollector metrics,
        ILogger<LegalRetrievalPipeline> logger)
    {
        _vectorStore = vectorStore;
        _embedder = embedder;
        _queryAnalyzer = queryAnalyzer;
        _bm25Index = bm25Index;
        _cache = cache;
        _metrics = metrics;
        _logger = logger;
    }

    public Task<RetrievalResult> RetrieveAsync(
        string query,
        RetrievalConfig config,
        string? caseNamespace = null,
        CancellationToken ct = default)
    {
        return RetrieveAsync(query, config, caseNamespace, ct, null, null);
    }

    public async Task<RetrievalResult> RetrieveAsync(
        string query,
        RetrievalConfig config,
        string? caseNamespace,
        CancellationToken ct,
        string? domainId,
        string? datasetScope)
    {
        var sw = Stopwatch.StartNew();

        var domainFilter = NormalizeScopeValue(domainId);
        var datasetFilter = NormalizeScopeValue(datasetScope);

        if (string.IsNullOrWhiteSpace(domainFilter) || string.IsNullOrWhiteSpace(datasetFilter))
        {
            var (parsedDomainId, parsedDatasetScope) = ScopeNamespaceBuilder.Parse(caseNamespace);
            domainFilter ??= parsedDomainId;
            datasetFilter ??= parsedDatasetScope;
        }

        // Check cache
        var cacheKey = ComputeCacheKey(query, config, caseNamespace, domainFilter, datasetFilter);
        if (_cache.TryGetValue<RetrievalResult>(cacheKey, out var cachedResult) && cachedResult is not null)
        {
            _metrics.IncrementCounter("cache_hit");
            return cachedResult;
        }

        _metrics.IncrementCounter("cache_miss");

        // Stage 1: Query Analysis
        var queryAnalysis = _queryAnalyzer.Analyze(query);
        _logger.LogDebug("Query type: {Type}, variants: {Count}",
            queryAnalysis.QueryType, queryAnalysis.SemanticVariants.Count);

        // Stage 2: Hybrid Retrieval
        var allChunks = new List<RetrievedChunk>();

        // 2a: Vector search across all query variants
        var vectorTopK = config.EnableReranking
            ? config.TopK * config.RerankMultiplier
            : config.TopK * 2; // Retrieve more, then deduplicate

        foreach (var variant in queryAnalysis.SemanticVariants)
        {
            var embedding = await _embedder.EmbedAsync(variant, ct);

            var results = string.IsNullOrWhiteSpace(domainFilter)
                && string.IsNullOrWhiteSpace(datasetFilter)
                ? await _vectorStore.SearchAsync(
                    embedding,
                    vectorTopK,
                    config.SimilarityThreshold,
                    caseNamespace,
                    ct)
                : await _vectorStore.SearchAsync(
                    embedding,
                    vectorTopK,
                    config.SimilarityThreshold,
                    caseNamespace,
                    ct,
                    domainId: domainFilter,
                    datasetScope: datasetFilter);

            if (results is not null)
            {
                allChunks.AddRange(results);
            }
        }

        // 2b: BM25 lexical search (if enabled)
        if (config.EnableHybridSearch && _bm25Index.DocumentCount > 0)
        {
            var bm25Results = _bm25Index.Search(queryAnalysis.NormalizedQuery, vectorTopK);
            // BM25 results need to be resolved from vector store (we don't store full chunks in BM25)
            // For now, we use BM25 scores to boost existing vector results
            var bm25Scores = bm25Results.ToDictionary(r => r.DocId, r => r.Score);

            foreach (var chunk in allChunks)
            {
                if (bm25Scores.TryGetValue(chunk.Chunk.Id, out var bm25Score))
                {
                    // Reciprocal Rank Fusion
                    var vectorScore = chunk.SimilarityScore;
                    var fusedScore = (float)(config.VectorWeight * vectorScore +
                                            (1 - config.VectorWeight) * NormalizeBm25(bm25Score));
                    // Store as rerank score (repurposed for fusion score)
                    chunk.RerankScore = fusedScore;
                }
            }
        }

        // Deduplicate by chunk content hash
        var deduplicated = allChunks
            .GroupBy(c => c.Chunk.ContentHash)
            .Select(g => g.OrderByDescending(c => c.FinalScore).First())
            .OrderByDescending(c => c.FinalScore)
            .ToList();

        var totalCandidates = deduplicated.Count;

        // Stage 3: Reranking (optional — placeholder for cross-encoder)
        double? rerankLatency = null;
        if (config.EnableReranking)
        {
            var rerankSw = Stopwatch.StartNew();
            // Cross-encoder reranking would go here
            // For now, we rely on the fusion scores
            deduplicated = deduplicated
                .OrderByDescending(c => c.FinalScore)
                .ToList();
            rerankSw.Stop();
            rerankLatency = rerankSw.Elapsed.TotalMilliseconds;
        }

        // Take top K after reranking
        var topChunks = deduplicated.Take(config.TopK).ToList();

        // Stage 4: Context Budget Optimization
        var (assembledContext, compressionRatio) = BuildContext(topChunks, config.MaxContextTokens);

        sw.Stop();

        var avgSimilarity = topChunks.Count > 0
            ? topChunks.Average(c => c.SimilarityScore)
            : 0.0;

        _metrics.RecordLatency("retrieval_pipeline", sw.Elapsed.TotalMilliseconds);
        _metrics.SetGauge("avg_similarity", avgSimilarity);

        var result = new RetrievalResult
        {
            Chunks = topChunks,
            QueryAnalysis = queryAnalysis,
            AverageSimilarity = avgSimilarity,
            RetrievalLatencyMs = sw.Elapsed.TotalMilliseconds,
            RerankLatencyMs = rerankLatency,
            CompressionRatio = compressionRatio,
            TotalCandidatesEvaluated = totalCandidates,
            AssembledContext = assembledContext
        };

        // Cache the result
        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(5));

        return result;
    }

    /// <summary>
    /// Builds the context string from retrieved chunks, respecting token budget.
    /// Preserves citation boundaries and deduplicates overlapping content.
    /// </summary>
    private static (string Context, double CompressionRatio) BuildContext(
        List<RetrievedChunk> chunks, int maxTokens)
    {
        if (chunks.Count == 0)
            return (string.Empty, 0);

        var sb = new StringBuilder();
        var totalOriginalTokens = 0;
        var includedTokens = 0;
        var chunkNum = 1;

        foreach (var chunk in chunks)
        {
            totalOriginalTokens += chunk.Chunk.TokenCount;

            // Check if adding this chunk would exceed budget
            if (includedTokens + chunk.Chunk.TokenCount > maxTokens && includedTokens > 0)
                break;

            // Add chunk with citation header
            sb.AppendLine($"--- [المصدر {chunkNum}: {chunk.Chunk.SourceFileName}، صفحة {chunk.Chunk.PageNumber}] ---");

            if (chunk.Chunk.ArticleReference is not null)
                sb.AppendLine($"[مرجع المادة: {chunk.Chunk.ArticleReference}]");

            if (chunk.Chunk.CaseNumber is not null)
                sb.AppendLine($"[رقم القضية: {chunk.Chunk.CaseNumber}]");

            if (chunk.Chunk.CourtName is not null)
                sb.AppendLine($"[المحكمة: {chunk.Chunk.CourtName}]");

            sb.AppendLine(chunk.Chunk.Content);
            sb.AppendLine();

            includedTokens += chunk.Chunk.TokenCount;
            chunkNum++;
        }

        var compressionRatio = totalOriginalTokens > 0
            ? (double)includedTokens / totalOriginalTokens
            : 0;

        return (sb.ToString(), compressionRatio);
    }

    /// <summary>
    /// Normalize BM25 score to [0, 1] range for fusion.
    /// </summary>
    private static double NormalizeBm25(double score)
    {
        // Sigmoid normalization
        return 1.0 / (1.0 + Math.Exp(-score));
    }

    private static string ComputeCacheKey(
        string query,
        RetrievalConfig config,
        string? ns,
        string? domainId,
        string? datasetScope)
    {
        var raw = $"{query}|{config.TopK}|{config.StrictMode}|{ns ?? "default"}|{domainId ?? "*"}|{datasetScope ?? "*"}";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..24];
    }

    private static string? NormalizeScopeValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();
    }
}
