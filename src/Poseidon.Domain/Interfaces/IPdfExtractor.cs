using Poseidon.Domain.Entities;

namespace Poseidon.Domain.Interfaces;

/// <summary>
/// Extracts text content from PDF documents.
/// </summary>
public interface IPdfExtractor
{
    /// <summary>
    /// Extracts text from a PDF file, returning page-by-page content.
    /// </summary>
    Task<PdfExtractionResult> ExtractAsync(string filePath, CancellationToken ct = default);
}

public sealed class PdfExtractionResult
{
    public required List<PageContent> Pages { get; init; }
    public required string FullText { get; init; }
    public int PageCount { get; init; }
    public bool IsScanned { get; init; }
    public string? DetectedLanguage { get; init; }
    public string? Error { get; set; }
    public bool Success => Error is null;
}

public sealed class PageContent
{
    public required int PageNumber { get; init; }
    public required string Text { get; init; }
}

