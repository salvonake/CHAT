using LegalAI.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace LegalAI.Ingestion.Extractors;

/// <summary>
/// Extracts text from PDF files using PdfPig.
/// Handles Arabic RTL text and detects scanned (image-only) PDFs.
/// </summary>
public sealed class PdfPigExtractor : IPdfExtractor
{
    private readonly ILogger<PdfPigExtractor> _logger;

    // Minimum characters per page to consider it a text PDF (vs scanned)
    private const int MinCharsPerPageForText = 30;

    public PdfPigExtractor(ILogger<PdfPigExtractor> logger)
    {
        _logger = logger;
    }

    public Task<PdfExtractionResult> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return Task.FromResult(new PdfExtractionResult
                {
                    Pages = [],
                    FullText = string.Empty,
                    Error = $"File not found: {filePath}"
                });
            }

            using var document = PdfDocument.Open(filePath);
            var pages = new List<PageContent>();
            var fullTextBuilder = new System.Text.StringBuilder();
            var isScanned = true;
            var totalChars = 0;

            foreach (var page in document.GetPages())
            {
                ct.ThrowIfCancellationRequested();

                var pageText = ExtractPageText(page);
                totalChars += pageText.Length;

                if (pageText.Length >= MinCharsPerPageForText)
                {
                    isScanned = false;
                }

                pages.Add(new PageContent
                {
                    PageNumber = page.Number,
                    Text = pageText
                });

                if (pageText.Length > 0)
                {
                    fullTextBuilder.AppendLine(pageText);
                    fullTextBuilder.AppendLine(); // Page separator
                }
            }

            var fullText = fullTextBuilder.ToString().Trim();

            // If the document appears scanned (very little text), log a warning
            if (isScanned && document.NumberOfPages > 0)
            {
                _logger.LogWarning(
                    "Document appears to be scanned (low character density): {FilePath}. " +
                    "OCR extraction is needed for accurate processing.",
                    filePath);
            }

            var detectedLanguage = Arabic.ArabicNormalizer.IsArabic(fullText) ? "ar" : "unknown";

            return Task.FromResult(new PdfExtractionResult
            {
                Pages = pages,
                FullText = fullText,
                PageCount = document.NumberOfPages,
                IsScanned = isScanned,
                DetectedLanguage = detectedLanguage
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract PDF: {FilePath}", filePath);
            return Task.FromResult(new PdfExtractionResult
            {
                Pages = [],
                FullText = string.Empty,
                Error = $"PDF extraction failed: {ex.Message}"
            });
        }
    }

    private static string ExtractPageText(Page page)
    {
        try
        {
            // PdfPig handles RTL text correctly
            var text = page.Text;

            // Clean up common PDF artifacts
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // Remove null characters
            text = text.Replace("\0", "");

            // Normalize line breaks
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            // Remove excessive blank lines
            while (text.Contains("\n\n\n"))
                text = text.Replace("\n\n\n", "\n\n");

            return text.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}
