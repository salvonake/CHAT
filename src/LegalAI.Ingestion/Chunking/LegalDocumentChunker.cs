using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LegalAI.Domain.Entities;
using LegalAI.Domain.Interfaces;
using LegalAI.Ingestion.Arabic;
using Microsoft.Extensions.Logging;

namespace LegalAI.Ingestion.Chunking;

/// <summary>
/// Legal-aware document chunker that detects article numbers, case numbers,
/// section headers, court names, dates, and structural elements in Arabic legal text.
/// 
/// Strategy:
/// 1. Detect legal structure markers (articles, sections, decisions)
/// 2. Chunk on structural boundaries first
/// 3. Fall back to sliding window for unstructured content
/// 4. Each chunk carries rich metadata for precision retrieval
/// </summary>
public sealed partial class LegalDocumentChunker : IDocumentChunker
{
    private readonly ILogger<LegalDocumentChunker> _logger;

    // Chunk size configuration
    private const int TargetChunkSize = 512;  // tokens (approximate by words)
    private const int ChunkOverlap = 64;      // token overlap between chunks
    private const int MinChunkSize = 50;       // minimum viable chunk
    private const int MaxChunkSize = 1024;     // hard max

    // Arabic legal structure patterns
    [GeneratedRegex(@"(?:المادة|المادّة|مادة|مادّة)\s*(?:رقم\s*)?([\d\u0660-\u0669]+)", RegexOptions.Compiled)]
    private static partial Regex ArticlePattern();

    [GeneratedRegex(@"(?:الفصل|فصل)\s*(?:رقم\s*)?([\d\u0660-\u0669]+)", RegexOptions.Compiled)]
    private static partial Regex ChapterPattern();

    [GeneratedRegex(@"(?:البند|بند)\s*(?:رقم\s*)?([\d\u0660-\u0669]+)", RegexOptions.Compiled)]
    private static partial Regex ClausePattern();

    [GeneratedRegex(@"(?:القرار|قرار)\s*(?:رقم\s*)?([\d\u0660-\u0669/\-]+)", RegexOptions.Compiled)]
    private static partial Regex DecisionPattern();

    [GeneratedRegex(@"(?:القضية|قضية|الدعوى|دعوى)\s*(?:رقم\s*)?([\d\u0660-\u0669/\-]+)", RegexOptions.Compiled)]
    private static partial Regex CaseNumberPattern();

    [GeneratedRegex(@"(?:محكمة|المحكمة)\s+([\u0600-\u06FF\s]+?)(?:\s*[-–—]|\s*$)", RegexOptions.Compiled)]
    private static partial Regex CourtPattern();

    // Gregorian date: dd/mm/yyyy or dd-mm-yyyy
    [GeneratedRegex(@"(\d{1,2})[/\-](\d{1,2})[/\-](\d{4})", RegexOptions.Compiled)]
    private static partial Regex GregorianDatePattern();

    // Hijri date pattern: يوم/شهر/سنة هـ
    [GeneratedRegex(@"(\d{1,2})[/\-](\d{1,2})[/\-](\d{4})\s*هـ", RegexOptions.Compiled)]
    private static partial Regex HijriDatePattern();

    // Section header pattern (Arabic headings — line starting with a bold/colon pattern)
    [GeneratedRegex(@"^([\u0600-\u06FF][\u0600-\u06FF\s]{2,50})\s*[:：]", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex SectionHeaderPattern();

    public LegalDocumentChunker(ILogger<LegalDocumentChunker> logger)
    {
        _logger = logger;
    }

    public List<DocumentChunk> ChunkDocument(
        string documentId,
        string sourceFileName,
        PdfExtractionResult extraction,
        string? caseNamespace = null)
    {
        var chunks = new List<DocumentChunk>();

        // Extract document-level metadata
        var docMetadata = ExtractDocumentMetadata(extraction.FullText);

        // Process page by page to maintain page number tracking
        var chunkIndex = 0;
        foreach (var page in extraction.Pages)
        {
            if (string.IsNullOrWhiteSpace(page.Text))
                continue;

            var pageChunks = ChunkPageContent(
                page.Text, page.PageNumber, documentId, sourceFileName,
                docMetadata, caseNamespace, ref chunkIndex);

            chunks.AddRange(pageChunks);
        }

        _logger.LogInformation(
            "Chunked {FileName}: {ChunkCount} chunks from {PageCount} pages",
            sourceFileName, chunks.Count, extraction.PageCount);

        return chunks;
    }

    private List<DocumentChunk> ChunkPageContent(
        string pageText, int pageNumber, string documentId, string sourceFileName,
        DocumentLevelMetadata docMeta, string? caseNamespace, ref int chunkIndex)
    {
        var chunks = new List<DocumentChunk>();

        // Try structural chunking first
        var sections = SplitByStructure(pageText);

        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section.Text) || 
                EstimateTokenCount(section.Text) < MinChunkSize)
            {
                continue;
            }

            // If section is too large, split with sliding window
            if (EstimateTokenCount(section.Text) > MaxChunkSize)
            {
                var subChunks = SlidingWindowChunk(section.Text);
                foreach (var subText in subChunks)
                {
                    var chunk = CreateChunk(
                        subText, pageNumber, documentId, sourceFileName,
                        section.Title, docMeta, caseNamespace, chunkIndex++);
                    chunks.Add(chunk);
                }
            }
            else
            {
                var chunk = CreateChunk(
                    section.Text, pageNumber, documentId, sourceFileName,
                    section.Title, docMeta, caseNamespace, chunkIndex++);
                chunks.Add(chunk);
            }
        }

        return chunks;
    }

    private DocumentChunk CreateChunk(
        string text, int pageNumber, string documentId, string sourceFileName,
        string? sectionTitle, DocumentLevelMetadata docMeta, string? caseNamespace, int chunkIndex)
    {
        // Extract chunk-level metadata
        var articleMatch = ArticlePattern().Match(text);
        var caseMatch = CaseNumberPattern().Match(text);
        var courtMatch = CourtPattern().Match(text);
        var dateMatch = GregorianDatePattern().Match(text);
        var hijriMatch = HijriDatePattern().Match(text);

        var normalizedContent = ArabicNormalizer.Normalize(text);
        var contentHash = ComputeHash(normalizedContent);

        return new DocumentChunk
        {
            DocumentId = documentId,
            Content = normalizedContent,
            ChunkIndex = chunkIndex,
            PageNumber = pageNumber,
            SectionTitle = sectionTitle,
            ArticleReference = articleMatch.Success ? articleMatch.Value : docMeta.ArticleReference,
            CaseNumber = caseMatch.Success ? caseMatch.Groups[1].Value : docMeta.CaseNumber,
            CourtName = courtMatch.Success ? courtMatch.Groups[1].Value.Trim() : docMeta.CourtName,
            CaseDate = dateMatch.Success ? dateMatch.Value : (hijriMatch.Success ? hijriMatch.Value : docMeta.CaseDate),
            CaseNamespace = caseNamespace,
            ContentHash = contentHash,
            TokenCount = EstimateTokenCount(normalizedContent),
            SourceFileName = sourceFileName
        };
    }

    /// <summary>
    /// Splits text into sections based on legal structural markers.
    /// </summary>
    private List<TextSection> SplitByStructure(string text)
    {
        var sections = new List<TextSection>();
        var markers = new List<(int Position, string Title)>();

        // Find all structural markers
        foreach (Match match in ArticlePattern().Matches(text))
            markers.Add((match.Index, match.Value));

        foreach (Match match in ChapterPattern().Matches(text))
            markers.Add((match.Index, match.Value));

        foreach (Match match in ClausePattern().Matches(text))
            markers.Add((match.Index, match.Value));

        foreach (Match match in DecisionPattern().Matches(text))
            markers.Add((match.Index, match.Value));

        foreach (Match match in SectionHeaderPattern().Matches(text))
            markers.Add((match.Index, match.Groups[1].Value.Trim()));

        if (markers.Count == 0)
        {
            // No structure detected — return as single section
            sections.Add(new TextSection { Text = text, Title = null });
            return sections;
        }

        // Sort by position
        markers.Sort((a, b) => a.Position.CompareTo(b.Position));

        // Text before first marker
        if (markers[0].Position > MinChunkSize)
        {
            sections.Add(new TextSection
            {
                Text = text[..markers[0].Position].Trim(),
                Title = null
            });
        }

        // Split between markers
        for (var i = 0; i < markers.Count; i++)
        {
            var start = markers[i].Position;
            var end = i + 1 < markers.Count ? markers[i + 1].Position : text.Length;
            var sectionText = text[start..end].Trim();

            if (!string.IsNullOrWhiteSpace(sectionText))
            {
                sections.Add(new TextSection
                {
                    Text = sectionText,
                    Title = markers[i].Title
                });
            }
        }

        return sections;
    }

    /// <summary>
    /// Sliding window chunking for large text sections.
    /// </summary>
    private static List<string> SlidingWindowChunk(string text)
    {
        var chunks = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length <= TargetChunkSize)
        {
            chunks.Add(text);
            return chunks;
        }

        var start = 0;
        while (start < words.Length)
        {
            var end = Math.Min(start + TargetChunkSize, words.Length);
            var chunkWords = words[start..end];
            var chunk = string.Join(' ', chunkWords);

            if (chunk.Length >= MinChunkSize)
            {
                chunks.Add(chunk);
            }

            start += TargetChunkSize - ChunkOverlap;
        }

        return chunks;
    }

    /// <summary>
    /// Extracts document-level metadata from the full text.
    /// </summary>
    private DocumentLevelMetadata ExtractDocumentMetadata(string fullText)
    {
        var caseMatch = CaseNumberPattern().Match(fullText);
        var courtMatch = CourtPattern().Match(fullText);
        var dateMatch = GregorianDatePattern().Match(fullText);
        var articleMatch = ArticlePattern().Match(fullText);

        return new DocumentLevelMetadata
        {
            CaseNumber = caseMatch.Success ? caseMatch.Groups[1].Value : null,
            CourtName = courtMatch.Success ? courtMatch.Groups[1].Value.Trim() : null,
            CaseDate = dateMatch.Success ? dateMatch.Value : null,
            ArticleReference = articleMatch.Success ? articleMatch.Value : null
        };
    }

    private static int EstimateTokenCount(string text)
    {
        // Rough approximation: Arabic words ≈ 1.5 tokens on average
        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return (int)(wordCount * 1.5);
    }

    private static string ComputeHash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant()[..16]; // Short hash for chunk ID
    }

    private sealed class TextSection
    {
        public required string Text { get; init; }
        public string? Title { get; init; }
    }

    private sealed class DocumentLevelMetadata
    {
        public string? CaseNumber { get; init; }
        public string? CourtName { get; init; }
        public string? CaseDate { get; init; }
        public string? ArticleReference { get; init; }
    }
}
