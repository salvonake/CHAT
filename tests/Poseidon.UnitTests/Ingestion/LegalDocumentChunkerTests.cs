using FluentAssertions;
using Poseidon.Domain.Interfaces;
using Poseidon.Ingestion.Chunking;
using Microsoft.Extensions.Logging;
using Moq;

namespace Poseidon.UnitTests.Ingestion;

/// <summary>
/// Tests for <see cref="LegalDocumentChunker"/>. Correct chunking is critical
/// for retrieval precision — bad chunks = bad citations = bad legal answers.
/// </summary>
public sealed class LegalDocumentChunkerTests
{
    private readonly LegalDocumentChunker _chunker;

    public LegalDocumentChunkerTests()
    {
        var logger = new Mock<ILogger<LegalDocumentChunker>>();
        _chunker = new LegalDocumentChunker(logger.Object);
    }

    private static PdfExtractionResult CreateExtraction(params (int page, string text)[] pages)
    {
        var pageContents = pages.Select(p => new PageContent
        {
            PageNumber = p.page,
            Text = p.text
        }).ToList();

        return new PdfExtractionResult
        {
            Pages = pageContents,
            FullText = string.Join("\n", pages.Select(p => p.text)),
            PageCount = pages.Length
        };
    }

    // ═══════════════════════════════════════
    //  Basic chunking
    // ═══════════════════════════════════════

    [Fact]
    public void ChunkDocument_SinglePage_ProducesChunks()
    {
        var text = string.Join(" ", Enumerable.Repeat("كلمة قانونية مهمة في هذا السياق", 50));
        var extraction = CreateExtraction((1, text));

        var chunks = _chunker.ChunkDocument("doc1", "test.pdf", extraction);

        chunks.Should().NotBeEmpty();
        chunks.Should().AllSatisfy(c =>
        {
            c.DocumentId.Should().Be("doc1");
            c.SourceFileName.Should().Be("test.pdf");
            c.PageNumber.Should().Be(1);
            c.Content.Should().NotBeEmpty();
            c.ContentHash.Should().NotBeEmpty();
            c.TokenCount.Should().BeGreaterThan(0);
        });
    }

    [Fact]
    public void ChunkDocument_EmptyPages_ReturnsEmpty()
    {
        var extraction = CreateExtraction((1, ""), (2, "   "));

        var chunks = _chunker.ChunkDocument("doc1", "empty.pdf", extraction);

        chunks.Should().BeEmpty();
    }

    [Fact]
    public void ChunkDocument_MultiplePages_PreservesPageNumbers()
    {
        var text = string.Join(" ", Enumerable.Repeat("نص قانوني يحتوي على معلومات هامة", 100));
        var extraction = CreateExtraction(
            (1, text),
            (2, text),
            (3, text));

        var chunks = _chunker.ChunkDocument("doc1", "multi.pdf", extraction);

        chunks.Should().NotBeEmpty();
        chunks.Select(c => c.PageNumber).Distinct().Should().BeEquivalentTo([1, 2, 3]);
    }

    // ═══════════════════════════════════════
    //  Legal structure detection
    // ═══════════════════════════════════════

    [Fact]
    public void ChunkDocument_DetectsArticleNumbers()
    {
        var text = """
            المادة 1: يسري هذا القانون على جميع الأشخاص في المملكة.
            ويحدد نطاق تطبيقه بموجب اللوائح التنفيذية الصادرة عن الوزارة المختصة.
            وتحدد اللائحة التنفيذية الإجراءات والضوابط اللازمة لتطبيق أحكام هذا القانون.
            ولا يجوز مخالفة أحكام هذا القانون إلا بموجب نص صريح في قانون آخر.
            المادة 2: يعاقب كل من يخالف هذا القانون بالسجن.
            والحبس مدة لا تقل عن سنة ولا تزيد عن خمس سنوات.
            وتضاعف العقوبة في حالة التكرار أو إذا كان الجاني موظفاً عاماً.
            ويجوز للمحكمة أن تأمر بنشر الحكم في الجريدة الرسمية على نفقة المحكوم عليه.
            """;
        var extraction = CreateExtraction((1, text));

        var chunks = _chunker.ChunkDocument("doc1", "law.pdf", extraction);

        chunks.Should().NotBeEmpty();
        // At least one chunk should have article reference
        chunks.Should().Contain(c => c.ArticleReference != null);
    }

    [Fact]
    public void ChunkDocument_DetectsCaseNumbers()
    {
        var text = """
            القضية رقم 2024/567
            حكمت المحكمة بما يلي في هذه القضية المعروضة أمامها بتاريخ اليوم
            بناء على الأدلة والمستندات المقدمة من الطرفين.
            وبعد الاطلاع على الأوراق والمداولة قانوناً.
            وحيث إن الدعوى استوفت شروطها الشكلية.
            فإن المحكمة تقضي بقبول الدعوى شكلاً وفي الموضوع بإلزام المدعى عليه.
            """ + string.Join(" ", Enumerable.Repeat("نص إضافي", 50));
        var extraction = CreateExtraction((1, text));

        var chunks = _chunker.ChunkDocument("doc1", "case.pdf", extraction);

        chunks.Should().Contain(c => c.CaseNumber != null);
    }

    [Fact]
    public void ChunkDocument_DetectsCourtNames()
    {
        var text = """
            محكمة النقض — أصدرت حكمها في الطعن المقيد بالجدول الكلي تحت رقم خاص
            بعد الاطلاع على الأوراق وسماع التقرير الذي تلاه السيد القاضي المقرر
            والمرافعة وبعد المداولة قانوناً وحيث إن الطعن استوفى أوضاعه الشكلية.
            """ + string.Join(" ", Enumerable.Repeat("نص قانوني إضافي", 40));
        var extraction = CreateExtraction((1, text));

        var chunks = _chunker.ChunkDocument("doc1", "court.pdf", extraction);

        chunks.Should().Contain(c => c.CourtName != null);
    }

    [Fact]
    public void ChunkDocument_DetectsDates()
    {
        var text = "صدر هذا الحكم بتاريخ 15/03/2024 " +
                   string.Join(" ", Enumerable.Repeat("نص قانوني مهم في سياق القضية", 60));
        var extraction = CreateExtraction((1, text));

        var chunks = _chunker.ChunkDocument("doc1", "dated.pdf", extraction);

        chunks.Should().Contain(c => c.CaseDate != null);
    }

    // ═══════════════════════════════════════
    //  Structural splitting
    // ═══════════════════════════════════════

    [Fact]
    public void ChunkDocument_SplitsOnArticleBoundaries()
    {
        var articleBody = string.Join(" ", Enumerable.Repeat("نص قانوني تفصيلي", 40));
        var text = $"""
            المادة 10: {articleBody}
            المادة 11: {articleBody}
            المادة 12: {articleBody}
            """;
        var extraction = CreateExtraction((1, text));

        var chunks = _chunker.ChunkDocument("doc1", "articles.pdf", extraction);

        // Should produce multiple chunks aligned to article boundaries
        chunks.Count.Should().BeGreaterThanOrEqualTo(3);
    }

    // ═══════════════════════════════════════
    //  Case namespace passthrough
    // ═══════════════════════════════════════

    [Fact]
    public void ChunkDocument_WithNamespace_SetsOnAllChunks()
    {
        var text = string.Join(" ", Enumerable.Repeat("نص قانوني", 100));
        var extraction = CreateExtraction((1, text));

        var chunks = _chunker.ChunkDocument("doc1", "ns.pdf", extraction, "criminal-law");

        chunks.Should().AllSatisfy(c => c.CaseNamespace.Should().Be("criminal-law"));
    }

    [Fact]
    public void ChunkDocument_WithoutNamespace_LeavesNull()
    {
        var text = string.Join(" ", Enumerable.Repeat("نص قانوني", 100));
        var extraction = CreateExtraction((1, text));

        var chunks = _chunker.ChunkDocument("doc1", "nons.pdf", extraction);

        chunks.Should().AllSatisfy(c => c.CaseNamespace.Should().BeNull());
    }

    // ═══════════════════════════════════════
    //  Content normalization
    // ═══════════════════════════════════════

    [Fact]
    public void ChunkDocument_NormalizesContent()
    {
        var text = "أحْكَامُ المَادَّةِ " + string.Join(" ", Enumerable.Repeat("كلمة", 60));
        var extraction = CreateExtraction((1, text));

        var chunks = _chunker.ChunkDocument("doc1", "norm.pdf", extraction);

        chunks.Should().NotBeEmpty();
        // Content should be normalized (no tashkeel)
        foreach (var chunk in chunks)
        {
            chunk.Content.Should().NotContainAny("\u064E", "\u064F", "\u064B");
        }
    }

    // ═══════════════════════════════════════
    //  Content hash uniqueness
    // ═══════════════════════════════════════

    [Fact]
    public void ChunkDocument_UniqueContentHashPerChunk()
    {
        var articleBody = string.Join(" ", Enumerable.Repeat("نص فريد", 60));
        var text = $"المادة 1: {articleBody} أول\nالمادة 2: {articleBody} ثاني";
        var extraction = CreateExtraction((1, text));

        var chunks = _chunker.ChunkDocument("doc1", "hash.pdf", extraction);

        if (chunks.Count > 1)
        {
            var hashes = chunks.Select(c => c.ContentHash).ToList();
            hashes.Distinct().Count().Should().Be(hashes.Count,
                "each chunk should have a unique content hash");
        }
    }

    // ═══════════════════════════════════════
    //  Chunk indices are sequential
    // ═══════════════════════════════════════

    [Fact]
    public void ChunkDocument_ChunkIndicesAreSequential()
    {
        var text = string.Join(" ", Enumerable.Repeat("نص قانوني طويل جداً يحتاج تقسيم", 200));
        var extraction = CreateExtraction((1, text));

        var chunks = _chunker.ChunkDocument("doc1", "seq.pdf", extraction);

        if (chunks.Count > 1)
        {
            var indices = chunks.Select(c => c.ChunkIndex).ToList();
            indices.Should().BeInAscendingOrder();
            indices[0].Should().Be(0);
        }
    }
}


