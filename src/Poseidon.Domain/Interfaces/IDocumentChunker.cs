using Poseidon.Domain.Entities;

namespace Poseidon.Domain.Interfaces;

/// <summary>
/// Chunks documents into semantically meaningful segments for embedding.
/// </summary>
public interface IDocumentChunker
{
    /// <summary>
    /// Chunks extracted PDF content into legal-aware segments.
    /// </summary>
    List<DocumentChunk> ChunkDocument(
        string documentId,
        string sourceFileName,
        PdfExtractionResult extraction,
        string? caseNamespace = null);
}

