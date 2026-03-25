namespace LegalAI.Domain.Interfaces;

/// <summary>
/// Generates embedding vectors for text content.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Generates an embedding vector for a single text.
    /// </summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Generates embedding vectors for a batch of texts.
    /// </summary>
    Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);

    /// <summary>
    /// The dimensionality of the embedding vectors.
    /// </summary>
    int EmbeddingDimension { get; }
}
