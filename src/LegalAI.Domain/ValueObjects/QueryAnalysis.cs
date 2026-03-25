namespace LegalAI.Domain.ValueObjects;

/// <summary>
/// Describes the analyzed intent and characteristics of a user query.
/// </summary>
public sealed class QueryAnalysis
{
    public required string OriginalQuery { get; init; }
    public required string NormalizedQuery { get; init; }
    public required List<string> SemanticVariants { get; init; }
    public required QueryType QueryType { get; init; }
    public required QueryDepth RequiredDepth { get; init; }
    public string? DetectedArticle { get; set; }
    public string? DetectedCaseNumber { get; set; }
    public string? DetectedCourt { get; set; }
    public string? DetectedDate { get; set; }
}

public enum QueryType
{
    FactLookup,
    Explanation,
    Comparison,
    Timeline,
    PrecedentSearch,
    ArticleSearch,
    ContradictionDetection,
    General
}

public enum QueryDepth
{
    Brief,
    Standard,
    Detailed,
    Exhaustive
}
