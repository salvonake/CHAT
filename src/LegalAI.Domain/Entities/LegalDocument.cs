namespace LegalAI.Domain.Entities;

/// <summary>
/// Represents a legal document that has been ingested into the system.
/// </summary>
public sealed class LegalDocument
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string ContentHash { get; init; }
    public long FileSizeBytes { get; init; }
    public int PageCount { get; set; }
    public DateTimeOffset IndexedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastModified { get; init; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;
    public string? ErrorMessage { get; set; }
    public int FailureCount { get; set; }
    public string? CaseNamespace { get; set; }

    /// <summary>
    /// Structured metadata extracted from the legal document.
    /// </summary>
    public LegalDocumentMetadata Metadata { get; set; } = new();

    /// <summary>
    /// Number of chunks generated from this document.
    /// </summary>
    public int ChunkCount { get; set; }
}

public enum DocumentStatus
{
    Pending,
    Indexing,
    Indexed,
    Failed,
    Quarantined,
    Deleted
}
