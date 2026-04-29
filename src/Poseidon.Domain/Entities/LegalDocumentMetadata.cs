namespace Poseidon.Domain.Entities;

/// <summary>
/// Structured metadata extracted from a legal document.
/// </summary>
public sealed class LegalDocumentMetadata
{
    public string? CaseNumber { get; set; }
    public string? CourtName { get; set; }
    public string? JudgeName { get; set; }
    public string? CaseDate { get; set; }
    public string? CaseDateHijri { get; set; }
    public string? CaseType { get; set; }
    public string? Decision { get; set; }
    public List<string> ArticleReferences { get; set; } = [];
    public List<string> ReferencedCases { get; set; } = [];
    public List<string> Parties { get; set; } = [];
    public string? Language { get; set; }
    public Dictionary<string, string> AdditionalProperties { get; set; } = [];
}

