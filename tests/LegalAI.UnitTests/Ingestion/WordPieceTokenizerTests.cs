using System.IO;
using FluentAssertions;
using LegalAI.Ingestion.Embedding;

namespace LegalAI.UnitTests.Ingestion;

/// <summary>
/// Tests for <see cref="WordPieceTokenizer"/>: Arabic/English tokenization,
/// sub-word decomposition, special tokens, edge cases, and BERT compatibility.
/// </summary>
public sealed class WordPieceTokenizerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _vocabPath;

    /// <summary>
    /// Builds a minimal but realistic vocab.txt covering Arabic words,
    /// English words, sub-word continuations (##), special tokens,
    /// punctuation, and numbers.
    /// </summary>
    public WordPieceTokenizerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LegalAI_WP_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _vocabPath = Path.Combine(_tempDir, "vocab.txt");

        // Line number = token ID. Build a minimal BERT-style vocab.
        var vocab = new List<string>
        {
            // 0-4: special tokens
            "[PAD]",    // 0
            "[UNK]",    // 1
            "[CLS]",    // 2
            "[SEP]",    // 3
            "[MASK]",   // 4

            // 5-14: Arabic whole words & sub-words
            "قانون",    // 5  — law
            "ال",       // 6  — definite article
            "##قانون",  // 7  — continuation: law
            "محكمة",    // 8  — court
            "عقد",      // 9  — contract
            "حكم",      // 10 — ruling
            "##ي",      // 11 — continuation: ya (adjective suffix)
            "##ة",      // 12 — continuation: ta marbuta
            "قاض",      // 13 — judge (stem)
            "مادة",     // 14 — article (legal)

            // 15-19: English words
            "the",      // 15
            "law",      // 16
            "court",    // 17
            "legal",    // 18
            "##al",     // 19 — continuation

            // 20-24: punctuation & numbers
            ".",         // 20
            ",",         // 21
            "(",         // 22
            ")",         // 23
            "1",         // 24

            // 25-29: more Arabic
            "في",       // 25 — in
            "من",       // 26 — from
            "على",      // 27 — on
            "هذا",      // 28 — this
            "أن",       // 29 — that

            // 30-32: additional sub-words
            "##ون",     // 30 — plural masculine suffix
            "##ات",     // 31 — plural feminine suffix
            "##ها",     // 32 — her/its suffix
        };

        File.WriteAllLines(_vocabPath, vocab);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private WordPieceTokenizer CreateTokenizer() =>
        new(_vocabPath, unkToken: "[UNK]", clsToken: "[CLS]", sepToken: "[SEP]", padToken: "[PAD]");

    // ═══════════════════════════════════════
    //  Constructor
    // ═══════════════════════════════════════

    [Fact]
    public void Constructor_LoadsVocab()
    {
        var tok = CreateTokenizer();
        tok.VocabSize.Should().Be(33);
    }

    [Fact]
    public void Constructor_MissingFile_Throws()
    {
        var act = () => new WordPieceTokenizer(Path.Combine(_tempDir, "nonexistent.txt"));
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Constructor_FromDictionary_Works()
    {
        var vocab = new Dictionary<string, int>
        {
            ["[PAD]"] = 0, ["[UNK]"] = 1, ["[CLS]"] = 2, ["[SEP]"] = 3,
            ["hello"] = 4, ["world"] = 5
        };

        var tok = new WordPieceTokenizer(vocab);
        tok.VocabSize.Should().Be(6);
    }

    // ═══════════════════════════════════════
    //  Special Tokens: [CLS] and [SEP]
    // ═══════════════════════════════════════

    [Fact]
    public void Tokenize_AlwaysStartsWithCLS()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("قانون");

        ids[0].Should().Be(2, "[CLS] token ID");
    }

    [Fact]
    public void Tokenize_AlwaysEndsWithSEP()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("قانون");

        ids[^1].Should().Be(3, "[SEP] token ID");
    }

    [Fact]
    public void Tokenize_EmptyText_ReturnsClsSepOnly()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("");

        ids.Should().BeEquivalentTo(new long[] { 2, 3 }, "empty text → [CLS] [SEP]");
    }

    [Fact]
    public void Tokenize_WhitespaceOnly_ReturnsClsSepOnly()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("   \t\n  ");

        ids.Should().BeEquivalentTo(new long[] { 2, 3 });
    }

    // ═══════════════════════════════════════
    //  Arabic Whole Words
    // ═══════════════════════════════════════

    [Fact]
    public void Tokenize_ArabicWholeWord_MatchesVocab()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("قانون");

        // [CLS]=2, "قانون"=5, [SEP]=3
        ids.Should().BeEquivalentTo(new long[] { 2, 5, 3 });
    }

    [Fact]
    public void Tokenize_MultipleArabicWords()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("محكمة في قانون");

        // [CLS]=2, "محكمة"=8, "في"=25, "قانون"=5, [SEP]=3
        ids.Should().BeEquivalentTo(new long[] { 2, 8, 25, 5, 3 });
    }

    // ═══════════════════════════════════════
    //  Sub-word Decomposition
    // ═══════════════════════════════════════

    [Fact]
    public void Tokenize_ArabicSubword_Decomposes()
    {
        var tok = CreateTokenizer();
        // "القانون" → "ال" + "##قانون"
        var ids = tok.Tokenize("القانون");

        ids.Should().BeEquivalentTo(new long[] { 2, 6, 7, 3 },
            "ال + ##قانون decomposition");
    }

    [Fact]
    public void Tokenize_ArabicSuffix_Decomposes()
    {
        var tok = CreateTokenizer();
        // "قانوني" → "قانون" + "##ي"
        var ids = tok.Tokenize("قانوني");

        ids.Should().BeEquivalentTo(new long[] { 2, 5, 11, 3 },
            "قانون + ##ي suffix decomposition");
    }

    // ═══════════════════════════════════════
    //  Unknown Tokens
    // ═══════════════════════════════════════

    [Fact]
    public void Tokenize_UnknownWord_MapsToUNK()
    {
        var tok = CreateTokenizer();
        // "xyz" is not in vocab and can't be decomposed
        var ids = tok.Tokenize("xyz");

        // [CLS]=2, [UNK]=1, [SEP]=3
        ids.Should().BeEquivalentTo(new long[] { 2, 1, 3 });
    }

    [Fact]
    public void Tokenize_MixedKnownUnknown()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("قانون xyz محكمة");

        ids[0].Should().Be(2);  // [CLS]
        ids[1].Should().Be(5);  // "قانون"
        ids[2].Should().Be(1);  // [UNK] for "xyz"
        ids[3].Should().Be(8);  // "محكمة"
        ids[4].Should().Be(3);  // [SEP]
    }

    // ═══════════════════════════════════════
    //  English Words
    // ═══════════════════════════════════════

    [Fact]
    public void Tokenize_EnglishWord_CaseInsensitive()
    {
        var tok = CreateTokenizer();
        // "The" → lowercased to "the" → ID 15
        var ids = tok.Tokenize("The");

        ids.Should().BeEquivalentTo(new long[] { 2, 15, 3 });
    }

    [Fact]
    public void Tokenize_EnglishSubword()
    {
        var tok = CreateTokenizer();
        // "legal" is in vocab → ID 18
        var ids = tok.Tokenize("legal");

        ids.Should().BeEquivalentTo(new long[] { 2, 18, 3 });
    }

    // ═══════════════════════════════════════
    //  Punctuation
    // ═══════════════════════════════════════

    [Fact]
    public void Tokenize_PunctuationSeparated()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("قانون.");

        // "قانون"=5, "."=20
        ids.Should().BeEquivalentTo(new long[] { 2, 5, 20, 3 });
    }

    [Fact]
    public void Tokenize_Parentheses()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("(محكمة)");

        // "("=22, "محكمة"=8, ")"=23
        ids.Should().BeEquivalentTo(new long[] { 2, 22, 8, 23, 3 });
    }

    // ═══════════════════════════════════════
    //  Truncation
    // ═══════════════════════════════════════

    [Fact]
    public void Tokenize_TruncatesToMaxLength()
    {
        var tok = CreateTokenizer();
        // Max length 5: [CLS] + 3 tokens + [SEP]
        var ids = tok.Tokenize("قانون محكمة عقد حكم مادة", maxLength: 5);

        ids.Length.Should().Be(5);
        ids[0].Should().Be(2);   // [CLS]
        ids[^1].Should().Be(3);  // [SEP]
    }

    [Fact]
    public void Tokenize_VeryShortMaxLength_StillHasClsSep()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("قانون", maxLength: 2);

        // Can only fit [CLS] + [SEP] (no room for actual token)
        ids.Length.Should().Be(2);
        ids[0].Should().Be(2);
        ids[1].Should().Be(3);
    }

    // ═══════════════════════════════════════
    //  TokenizeWithMask
    // ═══════════════════════════════════════

    [Fact]
    public void TokenizeWithMask_ProducesCorrectMask()
    {
        var tok = CreateTokenizer();
        var (ids, mask) = tok.TokenizeWithMask("قانون محكمة");

        ids.Length.Should().Be(mask.Length);

        // All positions should have attention
        mask.Should().AllSatisfy(m => m.Should().Be(1));
    }

    [Fact]
    public void TokenizeWithMask_WithPadding()
    {
        var tok = CreateTokenizer();
        var (ids, mask) = tok.TokenizeWithMask("قانون", padToLength: 10);

        ids.Length.Should().Be(10);
        mask.Length.Should().Be(10);

        // Real tokens: [CLS]=1, "قانون"=1, [SEP]=1 → 3 ones
        mask.Take(3).Should().AllSatisfy(m => m.Should().Be(1));
        // Padding: 7 zeros
        mask.Skip(3).Should().AllSatisfy(m => m.Should().Be(0));
    }

    [Fact]
    public void TokenizeWithMask_PadShorterThanActual_UsesActualLength()
    {
        var tok = CreateTokenizer();
        var (ids, _) = tok.TokenizeWithMask("قانون محكمة عقد", padToLength: 2);

        // 5 tokens ([CLS] + 3 words + [SEP]) > padToLength=2 → uses actual
        ids.Length.Should().BeGreaterThanOrEqualTo(5);
    }

    // ═══════════════════════════════════════
    //  IdsToTokens (reverse mapping)
    // ═══════════════════════════════════════

    [Fact]
    public void IdsToTokens_RoundTrips()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("قانون");
        var tokens = tok.IdsToTokens(ids);

        tokens[0].Should().Be("[CLS]");
        tokens[1].Should().Be("قانون");
        tokens[2].Should().Be("[SEP]");
    }

    [Fact]
    public void IdsToTokens_UnknownId_ReturnsUNK()
    {
        var tok = CreateTokenizer();
        var tokens = tok.IdsToTokens([9999]);

        tokens[0].Should().Be("[UNK]");
    }

    // ═══════════════════════════════════════
    //  Long Word Handling
    // ═══════════════════════════════════════

    [Fact]
    public void Tokenize_VeryLongWord_TreatedAsUNK()
    {
        var tok = new WordPieceTokenizer(
            new Dictionary<string, int> {
                ["[PAD]"] = 0, ["[UNK]"] = 1, ["[CLS]"] = 2, ["[SEP]"] = 3
            },
            maxInputChars: 5);

        // 8-char word exceeds maxInputChars=5
        var ids = tok.Tokenize("abcdefgh");

        ids.Should().Contain(1, "long word should map to [UNK]");
    }

    // ═══════════════════════════════════════
    //  Realistic Legal Arabic Sentences
    // ═══════════════════════════════════════

    [Fact]
    public void Tokenize_LegalPhrase_PreservesStructure()
    {
        var tok = CreateTokenizer();
        // "حكم محكمة" → "حكم"=10, "محكمة"=8
        var ids = tok.Tokenize("حكم محكمة");

        ids.Should().BeEquivalentTo(new long[] { 2, 10, 8, 3 });
    }

    [Fact]
    public void Tokenize_DefiniteArticleDecomposition()
    {
        var tok = CreateTokenizer();
        // "القانون" → "ال"=6, "##قانون"=7
        var ids = tok.Tokenize("القانون");
        var tokens = tok.IdsToTokens(ids);

        tokens.Should().Contain("ال");
        tokens.Should().Contain("##قانون");
    }

    [Fact]
    public void Tokenize_PluralSuffix()
    {
        var tok = CreateTokenizer();
        // "قانونات" → "قانون"=5, "##ات"=31
        var ids = tok.Tokenize("قانونات");

        ids.Should().BeEquivalentTo(new long[] { 2, 5, 31, 3 },
            "قانون + ##ات plural decomposition");
    }

    // ═══════════════════════════════════════
    //  Thread Safety
    // ═══════════════════════════════════════

    [Fact]
    public async Task Tokenize_ConcurrentCalls_AreThreadSafe()
    {
        var tok = CreateTokenizer();
        var texts = new[] { "قانون", "محكمة", "حكم في قانون", "مادة", "عقد" };

        var tasks = Enumerable.Range(0, 50)
            .Select(i => Task.Run(() => tok.Tokenize(texts[i % texts.Length])))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        results.Should().AllSatisfy(ids =>
        {
            ids[0].Should().Be(2);    // [CLS]
            ids[^1].Should().Be(3);   // [SEP]
        });
    }

    // ═══════════════════════════════════════
    //  Pre-tokenization Edge Cases
    // ═══════════════════════════════════════

    [Fact]
    public void Tokenize_MultipleSpaces_TreatedAsSingle()
    {
        var tok = CreateTokenizer();
        var ids1 = tok.Tokenize("قانون محكمة");
        var ids2 = tok.Tokenize("قانون    محكمة");

        ids1.Should().BeEquivalentTo(ids2, "multiple spaces same as single space");
    }

    [Fact]
    public void Tokenize_TabsAndNewlines_TreatedAsWhitespace()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("قانون\t\nمحكمة");

        ids.Should().BeEquivalentTo(new long[] { 2, 5, 8, 3 });
    }

    [Fact]
    public void Tokenize_Numbers()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("مادة 1");

        // "مادة"=14, "1"=24
        ids.Should().BeEquivalentTo(new long[] { 2, 14, 24, 3 });
    }
}
