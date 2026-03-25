namespace LegalAI.Domain.ValueObjects;

/// <summary>
/// Structured response from the legal AI engine.
/// Every answer must carry citations and confidence metrics.
/// </summary>
public sealed class LegalAnswer
{
    public required string Answer { get; init; }
    public required List<Citation> Citations { get; init; }
    public required double ConfidenceScore { get; init; }
    public required int RetrievedChunksUsed { get; init; }
    public required double RetrievalSimilarityAvg { get; init; }
    public bool IsAbstention { get; init; }
    public string? AbstentionReason { get; init; }
    public List<string> Warnings { get; init; } = [];
    public double GenerationLatencyMs { get; init; }
    public double RetrievalLatencyMs { get; init; }
}

/// <summary>
/// A citation linking a claim to the source document.
/// </summary>
public sealed class Citation
{
    public required string Document { get; init; }
    public int Page { get; init; }
    public string? Section { get; init; }
    public required string Snippet { get; init; }
    public string? ArticleReference { get; init; }
    public string? CaseNumber { get; init; }
    public double SimilarityScore { get; init; }
}
