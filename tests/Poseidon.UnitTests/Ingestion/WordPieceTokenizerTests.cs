using System.IO;
using FluentAssertions;
using Poseidon.Ingestion.Embedding;

namespace Poseidon.UnitTests.Ingestion;

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
        _tempDir = Path.Combine(Path.GetTempPath(), $"Poseidon_WP_{Guid.NewGuid():N}");
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
            "\u0642\u0627\u0646\u0648\u0646",       // 5 law
            "\u0627\u0644",                         // 6 definite article
            "##\u0642\u0627\u0646\u0648\u0646",     // 7 continuation: law
            "\u0645\u062d\u0643\u0645\u0629",       // 8 court
            "\u0639\u0642\u062f",                   // 9 contract
            "\u062d\u0643\u0645",                   // 10 ruling
            "##\u064a",                             // 11 continuation: ya
            "##\u0629",                             // 12 continuation: ta marbuta
            "\u0642\u0627\u0636",                   // 13 judge stem
            "\u0645\u0627\u062f\u0629",             // 14 article, legal

            // 15-19: English words
            "the",      // 15
            "law",      // 16
            "court",    // 17
            "legal",    // 18
            "##al",     // 19 continuation

            // 20-24: punctuation & numbers
            ".",         // 20
            ",",         // 21
            "(",         // 22
            ")",         // 23
            "1",         // 24

            // 25-29: more Arabic
            "\u0641\u064a",                         // 25 in
            "\u0645\u0646",                         // 26 from
            "\u0639\u0644\u0649",                   // 27 on
            "\u0647\u0630\u0627",                   // 28 this
            "\u0630\u0644\u0643",                   // 29 that

            // 30-32: additional sub-words
            "##\u0648\u0646",                       // 30 plural masculine suffix
            "##\u0627\u062a",                       // 31 plural feminine suffix
            "##\u0647\u0627",                       // 32 her/its suffix
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

    // ---------------------------------------
    //  Constructor
    // ---------------------------------------

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

    // ---------------------------------------
    //  Special Tokens: [CLS] and [SEP]
    // ---------------------------------------

    [Fact]
    public void Tokenize_AlwaysStartsWithCLS()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("\u0642\u0627\u0646\u0648\u0646");

        ids[0].Should().Be(2, "[CLS] token ID");
    }

    [Fact]
    public void Tokenize_AlwaysEndsWithSEP()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("\u0642\u0627\u0646\u0648\u0646");

        ids[^1].Should().Be(3, "[SEP] token ID");
    }

    [Fact]
    public void Tokenize_EmptyText_ReturnsClsSepOnly()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("");

        ids.Should().BeEquivalentTo(new long[] { 2, 3 }, "empty text maps to [CLS] [SEP]");
    }

    [Fact]
    public void Tokenize_WhitespaceOnly_ReturnsClsSepOnly()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("   \t\n  ");

        ids.Should().BeEquivalentTo(new long[] { 2, 3 });
    }

    // ---------------------------------------
    //  Arabic Whole Words
    // ---------------------------------------

    [Fact]
    public void Tokenize_ArabicWholeWord_MatchesVocab()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("\u0642\u0627\u0646\u0648\u0646");

        // [CLS]=2, "law"=5, [SEP]=3
        ids.Should().BeEquivalentTo(new long[] { 2, 5, 3 });
    }

    [Fact]
    public void Tokenize_MultipleArabicWords()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("\u0645\u062d\u0643\u0645\u0629 \u0641\u064a \u0642\u0627\u0646\u0648\u0646");

        // [CLS]=2, court=8, in=25, law=5, [SEP]=3
        ids.Should().BeEquivalentTo(new long[] { 2, 8, 25, 5, 3 });
    }

    // ---------------------------------------
    //  Sub-word Decomposition
    // ---------------------------------------

    [Fact]
    public void Tokenize_ArabicSubword_Decomposes()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("\u0627\u0644\u0642\u0627\u0646\u0648\u0646");

        ids.Should().BeEquivalentTo(new long[] { 2, 6, 7, 3 },
            "definite article + law decomposition");
    }

    [Fact]
    public void Tokenize_ArabicSuffix_Decomposes()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("\u0642\u0627\u0646\u0648\u0646\u064a");

        ids.Should().BeEquivalentTo(new long[] { 2, 5, 11, 3 },
            "law + ya suffix decomposition");
    }

    // ---------------------------------------
    //  Unknown Tokens
    // ---------------------------------------

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
        var ids = tok.Tokenize("\u0642\u0627\u0646\u0648\u0646 xyz \u0645\u062d\u0643\u0645\u0629");

        ids[0].Should().Be(2);  // [CLS]
        ids[1].Should().Be(5);  // law
        ids[2].Should().Be(1);  // [UNK] for "xyz"
        ids[3].Should().Be(8);  // court
        ids[4].Should().Be(3);  // [SEP]
    }

    // ---------------------------------------
    //  English Words
    // ---------------------------------------

    [Fact]
    public void Tokenize_EnglishWord_CaseInsensitive()
    {
        var tok = CreateTokenizer();
        // "The" lowercased to "the" -> ID 15
        var ids = tok.Tokenize("The");

        ids.Should().BeEquivalentTo(new long[] { 2, 15, 3 });
    }

    [Fact]
    public void Tokenize_EnglishSubword()
    {
        var tok = CreateTokenizer();
        // "legal" is in vocab -> ID 18
        var ids = tok.Tokenize("legal");

        ids.Should().BeEquivalentTo(new long[] { 2, 18, 3 });
    }

    // ---------------------------------------
    //  Punctuation
    // ---------------------------------------

    [Fact]
    public void Tokenize_PunctuationSeparated()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("\u0642\u0627\u0646\u0648\u0646.");

        // law=5, "."=20
        ids.Should().BeEquivalentTo(new long[] { 2, 5, 20, 3 });
    }

    [Fact]
    public void Tokenize_Parentheses()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("(\u0645\u062d\u0643\u0645\u0629)");

        // "("=22, court=8, ")"=23
        ids.Should().BeEquivalentTo(new long[] { 2, 22, 8, 23, 3 });
    }

    // ---------------------------------------
    //  Truncation
    // ---------------------------------------

    [Fact]
    public void Tokenize_TruncatesToMaxLength()
    {
        var tok = CreateTokenizer();
        // Max length 5: [CLS] + 3 tokens + [SEP]
        var ids = tok.Tokenize("\u0642\u0627\u0646\u0648\u0646 \u0645\u062d\u0643\u0645\u0629 \u0639\u0642\u062f \u062d\u0643\u0645 \u0645\u0627\u062f\u0629", maxLength: 5);

        ids.Length.Should().Be(5);
        ids[0].Should().Be(2);   // [CLS]
        ids[^1].Should().Be(3);  // [SEP]
    }

    [Fact]
    public void Tokenize_VeryShortMaxLength_StillHasClsSep()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("\u0642\u0627\u0646\u0648\u0646", maxLength: 2);

        // Can only fit [CLS] + [SEP] (no room for actual token)
        ids.Length.Should().Be(2);
        ids[0].Should().Be(2);
        ids[1].Should().Be(3);
    }

    // ---------------------------------------
    //  TokenizeWithMask
    // ---------------------------------------

    [Fact]
    public void TokenizeWithMask_ProducesCorrectMask()
    {
        var tok = CreateTokenizer();
        var (ids, mask) = tok.TokenizeWithMask("\u0642\u0627\u0646\u0648\u0646 \u0645\u062d\u0643\u0645\u0629");

        ids.Length.Should().Be(mask.Length);

        // All positions should have attention
        mask.Should().AllSatisfy(m => m.Should().Be(1));
    }

    [Fact]
    public void TokenizeWithMask_WithPadding()
    {
        var tok = CreateTokenizer();
        var (ids, mask) = tok.TokenizeWithMask("\u0642\u0627\u0646\u0648\u0646", padToLength: 10);

        ids.Length.Should().Be(10);
        mask.Length.Should().Be(10);

        // Real tokens: [CLS], law, [SEP] -> 3 ones
        mask.Take(3).Should().AllSatisfy(m => m.Should().Be(1));
        // Padding: 7 zeros
        mask.Skip(3).Should().AllSatisfy(m => m.Should().Be(0));
    }

    [Fact]
    public void TokenizeWithMask_PadShorterThanActual_UsesActualLength()
    {
        var tok = CreateTokenizer();
        var (ids, _) = tok.TokenizeWithMask("\u0642\u0627\u0646\u0648\u0646 \u0645\u062d\u0643\u0645\u0629 \u0639\u0642\u062f", padToLength: 2);

        // 5 tokens ([CLS] + 3 words + [SEP]) > padToLength=2, so it uses actual length.
        ids.Length.Should().BeGreaterThanOrEqualTo(5);
    }

    // ---------------------------------------
    //  IdsToTokens (reverse mapping)
    // ---------------------------------------

    [Fact]
    public void IdsToTokens_RoundTrips()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("\u0642\u0627\u0646\u0648\u0646");
        var tokens = tok.IdsToTokens(ids);

        tokens[0].Should().Be("[CLS]");
        tokens[1].Should().Be("\u0642\u0627\u0646\u0648\u0646");
        tokens[2].Should().Be("[SEP]");
    }

    [Fact]
    public void IdsToTokens_UnknownId_ReturnsUNK()
    {
        var tok = CreateTokenizer();
        var tokens = tok.IdsToTokens([9999]);

        tokens[0].Should().Be("[UNK]");
    }

    // ---------------------------------------
    //  Long Word Handling
    // ---------------------------------------

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

    // ---------------------------------------
    //  Realistic Legal Arabic Sentences
    // ---------------------------------------

    [Fact]
    public void Tokenize_LegalPhrase_PreservesStructure()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("\u062d\u0643\u0645 \u0645\u062d\u0643\u0645\u0629");

        ids.Should().BeEquivalentTo(new long[] { 2, 10, 8, 3 });
    }

    [Fact]
    public void Tokenize_DefiniteArticleDecomposition()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("\u0627\u0644\u0642\u0627\u0646\u0648\u0646");
        var tokens = tok.IdsToTokens(ids);

        tokens.Should().Contain("\u0627\u0644");
        tokens.Should().Contain("##\u0642\u0627\u0646\u0648\u0646");
    }

    [Fact]
    public void Tokenize_PluralSuffix()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("\u0642\u0627\u0646\u0648\u0646\u0627\u062a");

        ids.Should().BeEquivalentTo(new long[] { 2, 5, 31, 3 },
            "law + plural suffix decomposition");
    }

    // ---------------------------------------
    //  Thread Safety
    // ---------------------------------------

    [Fact]
    public async Task Tokenize_ConcurrentCalls_AreThreadSafe()
    {
        var tok = CreateTokenizer();
        var texts = new[]
        {
            "\u0642\u0627\u0646\u0648\u0646",
            "\u0645\u062d\u0643\u0645\u0629",
            "\u062d\u0643\u0645 \u0641\u064a \u0642\u0627\u0646\u0648\u0646",
            "\u0645\u0627\u062f\u0629",
            "\u0639\u0642\u062f"
        };

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

    // ---------------------------------------
    //  Pre-tokenization Edge Cases
    // ---------------------------------------

    [Fact]
    public void Tokenize_MultipleSpaces_TreatedAsSingle()
    {
        var tok = CreateTokenizer();
        var ids1 = tok.Tokenize("\u0642\u0627\u0646\u0648\u0646 \u0645\u062d\u0643\u0645\u0629");
        var ids2 = tok.Tokenize("\u0642\u0627\u0646\u0648\u0646    \u0645\u062d\u0643\u0645\u0629");

        ids1.Should().BeEquivalentTo(ids2, "multiple spaces same as single space");
    }

    [Fact]
    public void Tokenize_TabsAndNewlines_TreatedAsWhitespace()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("\u0642\u0627\u0646\u0648\u0646\t\n\u0645\u062d\u0643\u0645\u0629");

        ids.Should().BeEquivalentTo(new long[] { 2, 5, 8, 3 });
    }

    [Fact]
    public void Tokenize_Numbers()
    {
        var tok = CreateTokenizer();
        var ids = tok.Tokenize("\u0645\u0627\u062f\u0629 1");

        // article=14, "1"=24
        ids.Should().BeEquivalentTo(new long[] { 2, 14, 24, 3 });
    }
}



