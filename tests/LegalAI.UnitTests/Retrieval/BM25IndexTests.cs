using FluentAssertions;
using LegalAI.Retrieval.Lexical;

namespace LegalAI.UnitTests.Retrieval;

/// <summary>
/// Tests for <see cref="BM25Index"/>. Verifies BM25 scoring, IDF weighting,
/// term frequency normalization, document length normalization, add/remove
/// operations, tokenization, and edge cases.
/// </summary>
public sealed class BM25IndexTests
{
    private readonly BM25Index _index = new();

    // ══════════════════════════════════════
    //  Initial State
    // ══════════════════════════════════════

    [Fact]
    public void Empty_DocumentCount_IsZero()
    {
        _index.DocumentCount.Should().Be(0);
    }

    [Fact]
    public void Empty_Search_ReturnsEmptyList()
    {
        _index.Search("أي شيء", 10).Should().BeEmpty();
    }

    // ══════════════════════════════════════
    //  AddDocument
    // ══════════════════════════════════════

    [Fact]
    public void AddDocument_IncrementsDocumentCount()
    {
        _index.AddDocument("doc1", "نص قانوني مهم");
        _index.DocumentCount.Should().Be(1);

        _index.AddDocument("doc2", "نص آخر مختلف");
        _index.DocumentCount.Should().Be(2);
    }

    // ══════════════════════════════════════
    //  Search — Basic
    // ══════════════════════════════════════

    [Fact]
    public void Search_ExactTerm_FindsDocument()
    {
        _index.AddDocument("doc1", "المادة القانونية الأولى");

        var results = _index.Search("المادة", 10);

        results.Should().HaveCount(1);
        results[0].DocId.Should().Be("doc1");
        results[0].Score.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Search_MultipleMatches_RankedByRelevance()
    {
        // doc1 mentions "المادة" twice, doc2 mentions it once
        _index.AddDocument("doc1", "المادة الأولى المادة الثانية");
        _index.AddDocument("doc2", "المادة الأولى فقط");

        var results = _index.Search("المادة", 10);

        results.Should().HaveCount(2);
        // doc1 should score higher (higher TF for "المادة")
        results[0].DocId.Should().Be("doc1");
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        _index.AddDocument("doc1", "القانون المدني");

        _index.Search("الجنائي", 10).Should().BeEmpty();
    }

    [Fact]
    public void Search_TopK_LimitsResults()
    {
        for (int i = 0; i < 10; i++)
            _index.AddDocument($"doc{i}", $"المادة رقم {i} في القانون");

        _index.Search("المادة القانون", 3).Should().HaveCount(3);
    }

    // ══════════════════════════════════════
    //  IDF Weighting
    // ══════════════════════════════════════

    [Fact]
    public void Search_RareTerm_ScoresHigherThanCommon()
    {
        // "قانون" appears in all docs, "جنائي" only in doc2
        _index.AddDocument("doc1", "قانون مدني عام");
        _index.AddDocument("doc2", "قانون جنائي خاص");
        _index.AddDocument("doc3", "قانون تجاري دولي");

        var results = _index.Search("جنائي", 10);

        // "جنائي" has high IDF → only doc2 matches
        results.Should().HaveCount(1);
        results[0].DocId.Should().Be("doc2");
    }

    // ══════════════════════════════════════
    //  RemoveDocument
    // ══════════════════════════════════════

    [Fact]
    public void RemoveDocument_DecrementsCount()
    {
        _index.AddDocument("doc1", "نص أول");
        _index.AddDocument("doc2", "نص ثاني");
        _index.RemoveDocument("doc1");

        _index.DocumentCount.Should().Be(1);
    }

    [Fact]
    public void RemoveDocument_ExcludedFromSearch()
    {
        _index.AddDocument("doc1", "المادة القانونية");
        _index.AddDocument("doc2", "النص المدني");
        _index.RemoveDocument("doc1");

        var results = _index.Search("المادة", 10);
        results.Should().BeEmpty("doc1 was removed");
    }

    [Fact]
    public void RemoveDocument_NonExistent_NoError()
    {
        _index.AddDocument("doc1", "نص");
        var act = () => _index.RemoveDocument("nonexistent");
        act.Should().NotThrow();
    }

    // ══════════════════════════════════════
    //  Tokenization
    // ══════════════════════════════════════

    [Fact]
    public void Tokenization_CaseInsensitive()
    {
        _index.AddDocument("doc1", "Article About LAW");

        var results = _index.Search("article about law", 10);
        results.Should().HaveCount(1);
    }

    [Fact]
    public void Tokenization_RemovesPunctuation()
    {
        _index.AddDocument("doc1", "المادة (42): النص القانوني.");

        var results = _index.Search("المادة 42", 10);
        // "المادة" matches; "42" is a single char and gets filtered (len > 1)
        results.Should().NotBeEmpty();
    }

    [Fact]
    public void Tokenization_FiltersSingleCharTokens()
    {
        // Single character tokens should be filtered out
        _index.AddDocument("doc1", "a b c longer word");

        // Only "longer" and "word" are indexed (len > 1)
        var results = _index.Search("longer", 10);
        results.Should().HaveCount(1);
    }

    // ══════════════════════════════════════
    //  Multi-Term Queries
    // ══════════════════════════════════════

    [Fact]
    public void Search_MultiTerm_AllMatchingDocsReturned()
    {
        // Keep documents similar length so BM25 length normalization doesn't dominate
        _index.AddDocument("doc1", "القانون المدني العام الشامل");
        _index.AddDocument("doc2", "القانون الجنائي الخاص الشامل");
        _index.AddDocument("doc3", "القانون المدني الجنائي الشامل");

        var results = _index.Search("المدني الجنائي", 10);

        // All 3 docs should appear (each has at least one term)
        results.Should().HaveCount(3);
        // doc3 contains BOTH query terms, so it should score highest
        var doc3Score = results.First(r => r.DocId == "doc3").Score;
        var doc1Score = results.First(r => r.DocId == "doc1").Score;
        var doc2Score = results.First(r => r.DocId == "doc2").Score;
        doc3Score.Should().BeGreaterThan(doc1Score, "doc3 has both terms");
        doc3Score.Should().BeGreaterThan(doc2Score, "doc3 has both terms");
    }

    // ══════════════════════════════════════
    //  Arabic Legal Text
    // ══════════════════════════════════════

    [Fact]
    public void Search_ArabicLegalTerms_WorksCorrectly()
    {
        _index.AddDocument("penal", "يعاقب بالسجن مدة لا تقل عن سنة كل من ارتكب جريمة السرقة");
        _index.AddDocument("civil", "يلتزم المدين بتعويض الدائن عن الأضرار الناجمة عن الإخلال بالعقد");
        _index.AddDocument("commercial", "يجوز للشركة إصدار أسهم ممتازة وفقاً لنظامها الأساسي");

        var results = _index.Search("السرقة عقوبة", 10);
        results.Should().NotBeEmpty();
        results[0].DocId.Should().Be("penal");
    }

    // ══════════════════════════════════════
    //  BM25 Score Properties
    // ══════════════════════════════════════

    [Fact]
    public void Score_AlwaysPositive()
    {
        _index.AddDocument("doc1", "نص قانوني مهم جداً في المادة الأولى");

        var results = _index.Search("المادة", 10);
        results.Should().AllSatisfy(r => r.Score.Should().BeGreaterThan(0));
    }

    [Fact]
    public void Score_HigherTF_HigherScore()
    {
        // doc1: "قانون" x3, doc2: "قانون" x1
        _index.AddDocument("doc1", "قانون قانون قانون");
        _index.AddDocument("doc2", "قانون فقط هنا");

        var results = _index.Search("قانون", 10);
        results.Should().HaveCount(2);

        var doc1Score = results.First(r => r.DocId == "doc1").Score;
        var doc2Score = results.First(r => r.DocId == "doc2").Score;
        doc1Score.Should().BeGreaterThan(doc2Score);
    }

    // ══════════════════════════════════════
    //  Edge Cases
    // ══════════════════════════════════════

    [Fact]
    public void Search_EmptyQuery_ReturnsEmpty()
    {
        _index.AddDocument("doc1", "نص");
        _index.Search("", 10).Should().BeEmpty();
    }

    [Fact]
    public void Search_PunctuationOnlyQuery_ReturnsEmpty()
    {
        _index.AddDocument("doc1", "نص مهم");
        // All chars are single or punctuation → empty tokens after filter
        _index.Search("!@#$%", 10).Should().BeEmpty();
    }

    [Fact]
    public void AddDocument_EmptyText_DoesNotThrow()
    {
        var act = () => _index.AddDocument("doc1", "");
        act.Should().NotThrow();
    }

    [Fact]
    public void Search_TopKZero_ReturnsEmpty()
    {
        _index.AddDocument("doc1", "نص قانوني");
        _index.Search("نص", 0).Should().BeEmpty();
    }
}
