using LegalAI.Domain.Entities;

namespace LegalAI.Domain.Interfaces;

/// <summary>
/// Vector store abstraction for storing and searching document chunk embeddings.
/// </summary>
public interface IVectorStore
{
    /// <summary>
    /// Initializes the vector store (creates collections if needed).
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Upserts chunks with their embeddings into the vector store.
    /// </summary>
    Task UpsertAsync(IReadOnlyList<DocumentChunk> chunks, CancellationToken ct = default);

    /// <summary>
    /// Searches for the most similar chunks to the query embedding.
    /// </summary>
    Task<List<RetrievedChunk>> SearchAsync(
        float[] queryEmbedding,
        int topK,
        double scoreThreshold = 0.0,
        string? caseNamespace = null,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes all chunks belonging to a document.
    /// </summary>
    Task DeleteByDocumentIdAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Checks if a chunk with the given content hash already exists.
    /// </summary>
    Task<bool> ExistsByHashAsync(string contentHash, CancellationToken ct = default);

    /// <summary>
    /// Gets the total number of vectors stored.
    /// </summary>
    Task<long> GetVectorCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets collection/index health status.
    /// </summary>
    Task<VectorStoreHealth> GetHealthAsync(CancellationToken ct = default);
}

public sealed class VectorStoreHealth
{
    public bool IsHealthy { get; init; }
    public long VectorCount { get; init; }
    public long IndexedSegments { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }
}
