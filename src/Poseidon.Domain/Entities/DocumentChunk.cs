namespace Poseidon.Domain.Entities;

/// <summary>
/// A chunk of text extracted from a legal document, ready for embedding and indexing.
/// </summary>
public sealed class DocumentChunk
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string DocumentId { get; init; }
    public required string Content { get; init; }
    public required int ChunkIndex { get; init; }
    public int PageNumber { get; init; }
    public string? SectionTitle { get; set; }
    public string? ArticleReference { get; set; }
    public string? CaseNumber { get; set; }
    public string? CourtName { get; set; }
    public string? CaseDate { get; set; }
    public string? DomainId { get; set; }
    public string? DatasetId { get; set; }
    public string? DatasetScope { get; set; }
    public string? CaseNamespace { get; set; }
    public required string ContentHash { get; init; }
    public int TokenCount { get; set; }

    /// <summary>
    /// The embedding vector for this chunk. Null until computed.
    /// </summary>
    public float[]? Embedding { get; set; }

    /// <summary>
    /// Source file name for citation purposes.
    /// </summary>
    public required string SourceFileName { get; init; }
}

