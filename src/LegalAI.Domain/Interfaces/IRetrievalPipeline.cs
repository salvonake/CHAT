using LegalAI.Domain.Entities;
using LegalAI.Domain.ValueObjects;

namespace LegalAI.Domain.Interfaces;

/// <summary>
/// Multi-stage retrieval pipeline: Query Analysis → Hybrid Retrieval → Reranking → Context Budget.
/// </summary>
public interface IRetrievalPipeline
{
    /// <summary>
    /// Executes the full retrieval pipeline for a user query.
    /// </summary>
    Task<RetrievalResult> RetrieveAsync(
        string query,
        RetrievalConfig config,
        string? caseNamespace = null,
        CancellationToken ct = default);
}

public sealed class RetrievalResult
{
    public required List<RetrievedChunk> Chunks { get; init; }
    public required QueryAnalysis QueryAnalysis { get; init; }
    public double AverageSimilarity { get; init; }
    public double RetrievalLatencyMs { get; init; }
    public double? RerankLatencyMs { get; init; }
    public double CompressionRatio { get; init; }
    public int TotalCandidatesEvaluated { get; init; }
    public string AssembledContext { get; init; } = string.Empty;
}
