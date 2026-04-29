using FluentAssertions;
using Poseidon.Retrieval.Lexical;

namespace Poseidon.UnitTests.Retrieval;

/// <summary>
/// Tests for <see cref="BM25Index"/>. Verifies BM25 scoring, IDF weighting,
/// term frequency normalization, document length normalization, add/remove
/// operations, tokenization, and edge cases.
/// </summary>
public sealed class BM25IndexTests
{
    private readonly BM25Index _index = new();

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Initial State
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void Empty_DocumentCount_IsZero()
    {
        _index.DocumentCount.Should().Be(0);
    }

    [Fact]
    public void Empty_Search_ReturnsEmptyList()
    {
        _index.Search("Ø£ÙŠ Ø´ÙŠØ¡", 10).Should().BeEmpty();
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  AddDocument
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void AddDocument_IncrementsDocumentCount()
    {
        _index.AddDocument("doc1", "Ù†Øµ Ù‚Ø§Ù†ÙˆÙ†ÙŠ Ù…Ù‡Ù…");
        _index.DocumentCount.Should().Be(1);

        _index.AddDocument("doc2", "Ù†Øµ Ø¢Ø®Ø± Ù…Ø®ØªÙ„Ù");
        _index.DocumentCount.Should().Be(2);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Search â€” Basic
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void Search_ExactTerm_FindsDocument()
    {
        _index.AddDocument("doc1", "Ø§Ù„Ù…Ø§Ø¯Ø© Ø§Ù„Ù‚Ø§Ù†ÙˆÙ†ÙŠØ© Ø§Ù„Ø£ÙˆÙ„Ù‰");

        var results = _index.Search("Ø§Ù„Ù…Ø§Ø¯Ø©", 10);

        results.Should().HaveCount(1);
        results[0].DocId.Should().Be("doc1");
        results[0].Score.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Search_MultipleMatches_RankedByRelevance()
    {
        // doc1 mentions "Ø§Ù„Ù…Ø§Ø¯Ø©" twice, doc2 mentions it once
        _index.AddDocument("doc1", "Ø§Ù„Ù…Ø§Ø¯Ø© Ø§Ù„Ø£ÙˆÙ„Ù‰ Ø§Ù„Ù…Ø§Ø¯Ø© Ø§Ù„Ø«Ø§Ù†ÙŠØ©");
        _index.AddDocument("doc2", "Ø§Ù„Ù…Ø§Ø¯Ø© Ø§Ù„Ø£ÙˆÙ„Ù‰ ÙÙ‚Ø·");

        var results = _index.Search("Ø§Ù„Ù…Ø§Ø¯Ø©", 10);

        results.Should().HaveCount(2);
        // doc1 should score higher (higher TF for "Ø§Ù„Ù…Ø§Ø¯Ø©")
        results[0].DocId.Should().Be("doc1");
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        _index.AddDocument("doc1", "Ø§Ù„Ù‚Ø§Ù†ÙˆÙ† Ø§Ù„Ù…Ø¯Ù†ÙŠ");

        _index.Search("Ø§Ù„Ø¬Ù†Ø§Ø¦ÙŠ", 10).Should().BeEmpty();
    }

    [Fact]
    public void Search_TopK_LimitsResults()
    {
        for (int i = 0; i < 10; i++)
            _index.AddDocument($"doc{i}", $"Ø§Ù„Ù…Ø§Ø¯Ø© Ø±Ù‚Ù… {i} ÙÙŠ Ø§Ù„Ù‚Ø§Ù†ÙˆÙ†");

        _index.Search("Ø§Ù„Ù…Ø§Ø¯Ø© Ø§Ù„Ù‚Ø§Ù†ÙˆÙ†", 3).Should().HaveCount(3);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  IDF Weighting
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void Search_RareTerm_ScoresHigherThanCommon()
    {
        // "Ù‚Ø§Ù†ÙˆÙ†" appears in all docs, "Ø¬Ù†Ø§Ø¦ÙŠ" only in doc2
        _index.AddDocument("doc1", "Ù‚Ø§Ù†ÙˆÙ† Ù…Ø¯Ù†ÙŠ Ø¹Ø§Ù…");
        _index.AddDocument("doc2", "Ù‚Ø§Ù†ÙˆÙ† Ø¬Ù†Ø§Ø¦ÙŠ Ø®Ø§Øµ");
        _index.AddDocument("doc3", "Ù‚Ø§Ù†ÙˆÙ† ØªØ¬Ø§Ø±ÙŠ Ø¯ÙˆÙ„ÙŠ");

        var results = _index.Search("Ø¬Ù†Ø§Ø¦ÙŠ", 10);

        // "Ø¬Ù†Ø§Ø¦ÙŠ" has high IDF â†’ only doc2 matches
        results.Should().HaveCount(1);
        results[0].DocId.Should().Be("doc2");
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  RemoveDocument
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void RemoveDocument_DecrementsCount()
    {
        _index.AddDocument("doc1", "Ù†Øµ Ø£ÙˆÙ„");
        _index.AddDocument("doc2", "Ù†Øµ Ø«Ø§Ù†ÙŠ");
        _index.RemoveDocument("doc1");

        _index.DocumentCount.Should().Be(1);
    }

    [Fact]
    public void RemoveDocument_ExcludedFromSearch()
    {
        _index.AddDocument("doc1", "Ø§Ù„Ù…Ø§Ø¯Ø© Ø§Ù„Ù‚Ø§Ù†ÙˆÙ†ÙŠØ©");
        _index.AddDocument("doc2", "Ø§Ù„Ù†Øµ Ø§Ù„Ù…Ø¯Ù†ÙŠ");
        _index.RemoveDocument("doc1");

        var results = _index.Search("Ø§Ù„Ù…Ø§Ø¯Ø©", 10);
        results.Should().BeEmpty("doc1 was removed");
    }

    [Fact]
    public void RemoveDocument_NonExistent_NoError()
    {
        _index.AddDocument("doc1", "Ù†Øµ");
        var act = () => _index.RemoveDocument("nonexistent");
        act.Should().NotThrow();
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Tokenization
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

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
        _index.AddDocument("doc1", "Ø§Ù„Ù…Ø§Ø¯Ø© (42): Ø§Ù„Ù†Øµ Ø§Ù„Ù‚Ø§Ù†ÙˆÙ†ÙŠ.");

        var results = _index.Search("Ø§Ù„Ù…Ø§Ø¯Ø© 42", 10);
        // "Ø§Ù„Ù…Ø§Ø¯Ø©" matches; "42" is a single char and gets filtered (len > 1)
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

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Multi-Term Queries
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void Search_MultiTerm_AllMatchingDocsReturned()
    {
        // Keep documents similar length so BM25 length normalization doesn't dominate
        _index.AddDocument("doc1", "Ø§Ù„Ù‚Ø§Ù†ÙˆÙ† Ø§Ù„Ù…Ø¯Ù†ÙŠ Ø§Ù„Ø¹Ø§Ù… Ø§Ù„Ø´Ø§Ù…Ù„");
        _index.AddDocument("doc2", "Ø§Ù„Ù‚Ø§Ù†ÙˆÙ† Ø§Ù„Ø¬Ù†Ø§Ø¦ÙŠ Ø§Ù„Ø®Ø§Øµ Ø§Ù„Ø´Ø§Ù…Ù„");
        _index.AddDocument("doc3", "Ø§Ù„Ù‚Ø§Ù†ÙˆÙ† Ø§Ù„Ù…Ø¯Ù†ÙŠ Ø§Ù„Ø¬Ù†Ø§Ø¦ÙŠ Ø§Ù„Ø´Ø§Ù…Ù„");

        var results = _index.Search("Ø§Ù„Ù…Ø¯Ù†ÙŠ Ø§Ù„Ø¬Ù†Ø§Ø¦ÙŠ", 10);

        // All 3 docs should appear (each has at least one term)
        results.Should().HaveCount(3);
        // doc3 contains BOTH query terms, so it should score highest
        var doc3Score = results.First(r => r.DocId == "doc3").Score;
        var doc1Score = results.First(r => r.DocId == "doc1").Score;
        var doc2Score = results.First(r => r.DocId == "doc2").Score;
        doc3Score.Should().BeGreaterThan(doc1Score, "doc3 has both terms");
        doc3Score.Should().BeGreaterThan(doc2Score, "doc3 has both terms");
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Arabic Legal Text
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void Search_ArabicLegalTerms_WorksCorrectly()
    {
        _index.AddDocument("penal", "ÙŠØ¹Ø§Ù‚Ø¨ Ø¨Ø§Ù„Ø³Ø¬Ù† Ù…Ø¯Ø© Ù„Ø§ ØªÙ‚Ù„ Ø¹Ù† Ø³Ù†Ø© ÙƒÙ„ Ù…Ù† Ø§Ø±ØªÙƒØ¨ Ø¬Ø±ÙŠÙ…Ø© Ø§Ù„Ø³Ø±Ù‚Ø©");
        _index.AddDocument("civil", "ÙŠÙ„ØªØ²Ù… Ø§Ù„Ù…Ø¯ÙŠÙ† Ø¨ØªØ¹ÙˆÙŠØ¶ Ø§Ù„Ø¯Ø§Ø¦Ù† Ø¹Ù† Ø§Ù„Ø£Ø¶Ø±Ø§Ø± Ø§Ù„Ù†Ø§Ø¬Ù…Ø© Ø¹Ù† Ø§Ù„Ø¥Ø®Ù„Ø§Ù„ Ø¨Ø§Ù„Ø¹Ù‚Ø¯");
        _index.AddDocument("commercial", "ÙŠØ¬ÙˆØ² Ù„Ù„Ø´Ø±ÙƒØ© Ø¥ØµØ¯Ø§Ø± Ø£Ø³Ù‡Ù… Ù…Ù…ØªØ§Ø²Ø© ÙˆÙÙ‚Ø§Ù‹ Ù„Ù†Ø¸Ø§Ù…Ù‡Ø§ Ø§Ù„Ø£Ø³Ø§Ø³ÙŠ");

        var results = _index.Search("Ø§Ù„Ø³Ø±Ù‚Ø© Ø¹Ù‚ÙˆØ¨Ø©", 10);
        results.Should().NotBeEmpty();
        results[0].DocId.Should().Be("penal");
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  BM25 Score Properties
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void Score_AlwaysPositive()
    {
        _index.AddDocument("doc1", "Ù†Øµ Ù‚Ø§Ù†ÙˆÙ†ÙŠ Ù…Ù‡Ù… Ø¬Ø¯Ø§Ù‹ ÙÙŠ Ø§Ù„Ù…Ø§Ø¯Ø© Ø§Ù„Ø£ÙˆÙ„Ù‰");

        var results = _index.Search("Ø§Ù„Ù…Ø§Ø¯Ø©", 10);
        results.Should().AllSatisfy(r => r.Score.Should().BeGreaterThan(0));
    }

    [Fact]
    public void Score_HigherTF_HigherScore()
    {
        // doc1: "Ù‚Ø§Ù†ÙˆÙ†" x3, doc2: "Ù‚Ø§Ù†ÙˆÙ†" x1
        _index.AddDocument("doc1", "Ù‚Ø§Ù†ÙˆÙ† Ù‚Ø§Ù†ÙˆÙ† Ù‚Ø§Ù†ÙˆÙ†");
        _index.AddDocument("doc2", "Ù‚Ø§Ù†ÙˆÙ† ÙÙ‚Ø· Ù‡Ù†Ø§");

        var results = _index.Search("Ù‚Ø§Ù†ÙˆÙ†", 10);
        results.Should().HaveCount(2);

        var doc1Score = results.First(r => r.DocId == "doc1").Score;
        var doc2Score = results.First(r => r.DocId == "doc2").Score;
        doc1Score.Should().BeGreaterThan(doc2Score);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Edge Cases
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void Search_EmptyQuery_ReturnsEmpty()
    {
        _index.AddDocument("doc1", "Ù†Øµ");
        _index.Search("", 10).Should().BeEmpty();
    }

    [Fact]
    public void Search_PunctuationOnlyQuery_ReturnsEmpty()
    {
        _index.AddDocument("doc1", "Ù†Øµ Ù…Ù‡Ù…");
        // All chars are single or punctuation â†’ empty tokens after filter
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
        _index.AddDocument("doc1", "Ù†Øµ Ù‚Ø§Ù†ÙˆÙ†ÙŠ");
        _index.Search("Ù†Øµ", 0).Should().BeEmpty();
    }
}

