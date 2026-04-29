namespace Poseidon.Domain.Entities;

/// <summary>
/// Represents the result of a retrieval operation â€” a chunk with its similarity score.
/// </summary>
public sealed class RetrievedChunk
{
    public required DocumentChunk Chunk { get; init; }
    public required float SimilarityScore { get; init; }
    public float? RerankScore { get; set; }

    /// <summary>
    /// The final score after fusion (vector + lexical + rerank).
    /// </summary>
    public float FinalScore => RerankScore ?? SimilarityScore;
}

