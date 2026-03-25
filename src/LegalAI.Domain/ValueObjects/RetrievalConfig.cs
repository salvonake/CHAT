namespace LegalAI.Domain.ValueObjects;

/// <summary>
/// Configuration for the retrieval pipeline.
/// </summary>
public sealed class RetrievalConfig
{
    /// <summary>Number of top chunks to retrieve from vector search.</summary>
    public int TopK { get; init; } = 10;

    /// <summary>Minimum similarity threshold. Below this, chunks are discarded.</summary>
    public double SimilarityThreshold { get; init; } = 0.45;

    /// <summary>Whether to enable BM25 lexical search alongside vector search.</summary>
    public bool EnableHybridSearch { get; init; } = true;

    /// <summary>Weight for vector search in fusion (0-1). BM25 weight = 1 - this.</summary>
    public double VectorWeight { get; init; } = 0.7;

    /// <summary>Whether to apply cross-encoder reranking.</summary>
    public bool EnableReranking { get; init; } = false;

    /// <summary>Multiplier for rerank candidates (TopK × Multiplier).</summary>
    public int RerankMultiplier { get; init; } = 3;

    /// <summary>Maximum tokens in the context window for the LLM.</summary>
    public int MaxContextTokens { get; init; } = 4096;

    /// <summary>Enable strict evidence mode — every claim must be cited.</summary>
    public bool StrictMode { get; init; } = true;

    /// <summary>Confidence score below which the system abstains.</summary>
    public double AbstentionThreshold { get; init; } = 0.50;

    /// <summary>Confidence score below which a warning is added.</summary>
    public double WarningThreshold { get; init; } = 0.70;

    /// <summary>Number of query semantic variants to generate.</summary>
    public int QueryVariants { get; init; } = 3;
}
