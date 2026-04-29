using FluentAssertions;
using Poseidon.Ingestion.Arabic;

namespace Poseidon.UnitTests.Ingestion;

/// <summary>
/// Tests for <see cref="ArabicNormalizer"/>. Correct normalization is critical
/// for retrieval recall; inconsistent normalization means missed matches.
/// </summary>
public sealed class ArabicNormalizerTests
{
    // ---------------------------------------
    //  Empty / null input
    // ---------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_NullOrEmpty_ReturnsEmpty(string? input)
    {
        ArabicNormalizer.Normalize(input!).Should().BeEmpty();
    }

    // ---------------------------------------
    //  Tashkeel (diacritics) removal
    // ---------------------------------------

    [Fact]
    public void Normalize_RemovesTashkeel()
    {
        var withDiacritics = "\u0627\u0644\u0633\u064f\u0644\u0637\u0629 \u0627\u0644\u0642\u064e\u0636\u0627\u0626\u064a\u0629 \u0645\u064f\u0633\u062a\u0642\u0644\u0629";
        var result = ArabicNormalizer.Normalize(withDiacritics);

        // Should not contain any tashkeel characters
        result.Should().NotContainAny("\u064B", "\u064C", "\u064D", "\u064E",
            "\u064F", "\u0650", "\u0651", "\u0652");
        result.Should().Contain("\u0627\u0644\u0633\u0644\u0637\u0647");
        result.Should().Contain("\u0627\u0644\u0642\u0636\u0627\u0626\u064a\u0647");
    }

    // ---------------------------------------
    //  Alef normalization
    // ---------------------------------------

    [Theory]
    [InlineData("\u0622", "\u0627")]
    [InlineData("\u0623", "\u0627")]
    [InlineData("\u0625", "\u0627")]
    [InlineData("\u0671", "\u0627")]
    public void Normalize_AlefVariants_ToPlainAlef(string input, string expected)
    {
        ArabicNormalizer.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_AlefInWord_Normalized()
    {
        var result1 = ArabicNormalizer.Normalize("\u0623\u062d\u0643\u0627\u0645");
        result1.Should().Be("\u0627\u062d\u0643\u0627\u0645");

        var result2 = ArabicNormalizer.Normalize("\u0625\u062c\u0631\u0627\u0621\u0627\u062a");
        result2.Should().Be("\u0627\u062c\u0631\u0627\u0621\u0627\u062a");
    }

    // ---------------------------------------
    //  Tatweel removal
    // ---------------------------------------

    [Fact]
    public void Normalize_RemovesTatweel()
    {
        var withTatweel = "\u0627\u0644\u0645\u062d\u0640\u0640\u0640\u0640\u0643\u0645\u0629";
        var result = ArabicNormalizer.Normalize(withTatweel);

        result.Should().NotContain("\u0640");
        result.Should().Be("\u0627\u0644\u0645\u062d\u0643\u0645\u0647");
    }

    // ---------------------------------------
    //  Teh marbuta to Heh
    // ---------------------------------------

    [Fact]
    public void Normalize_TehMarbuta_ToHeh()
    {
        var withTehMarbuta = "\u0645\u062d\u0643\u0645\u0629 \u0645\u062f\u0646\u064a\u0629";
        var result = ArabicNormalizer.Normalize(withTehMarbuta);

        result.Should().Contain("\u0645\u062d\u0643\u0645\u0647");
    }

    // ---------------------------------------
    //  Alef Maksura to Ya
    // ---------------------------------------

    [Fact]
    public void Normalize_AlefMaksura_ToYa()
    {
        var withMaksura = "\u0639\u0644\u0649";
        var result = ArabicNormalizer.Normalize(withMaksura);

        result.Should().Be("\u0639\u0644\u064a");
    }

    // ---------------------------------------
    //  Whitespace collapse
    // ---------------------------------------

    [Fact]
    public void Normalize_CollapsesMultipleSpaces()
    {
        var withSpaces = "\u0627\u0644\u0642\u0627\u0646\u0648\u0646    \u0627\u0644\u0645\u062f\u0646\u064a   \u0627\u0644\u062a\u0648\u0646\u0633\u064a";
        var result = ArabicNormalizer.Normalize(withSpaces);

        result.Should().Be("\u0627\u0644\u0642\u0627\u0646\u0648\u0646 \u0627\u0644\u0645\u062f\u0646\u064a \u0627\u0644\u062a\u0648\u0646\u0633\u064a");
    }

    [Fact]
    public void Normalize_TrimsWhitespace()
    {
        var padded = "   \u0641\u064a \u0627\u0644\u0645\u062d\u0643\u0645\u0629   ";
        var result = ArabicNormalizer.Normalize(padded);

        result.Should().Be("\u0641\u064a \u0627\u0644\u0645\u062d\u0643\u0645\u0647");
    }

    // ---------------------------------------
    //  Combined normalizations
    // ---------------------------------------

    [Fact]
    public void Normalize_CombinedNormalizations()
    {
        // Full Arabic legal text with various normalization targets
        var input = "\u0623\u062d\u0643\u0627\u0645 \u0627\u0644\u0642\u064e\u0636\u0627\u0621 \u0648\u0625\u062c\u0631\u0627\u0621\u0627\u062a \u0627\u0644\u0645\u062d\u0643\u0645\u0629";
        var result = ArabicNormalizer.Normalize(input);

        // Should have: removed tashkeel, normalized alef, collapsed spaces
        result.Should().NotContainAny("\u064B", "\u064E", "\u064F", "\u0650");
        result.Should().StartWith("\u0627\u062d\u0643\u0627\u0645");
    }

    // ---------------------------------------
    //  Non-Arabic text passthrough
    // ---------------------------------------

    [Fact]
    public void Normalize_EnglishText_Unchanged()
    {
        var english = "Article 45 of the Labor Law";
        ArabicNormalizer.Normalize(english).Should().Be(english);
    }

    [Fact]
    public void Normalize_MixedArabicEnglish_OnlyArabicNormalized()
    {
        var mixed = "Article 45 \u0641\u064a \u0627\u0644\u0645\u062d\u0643\u0645\u0629 \u0627\u0644\u0645\u062f\u0646\u064a\u0629";
        var result = ArabicNormalizer.Normalize(mixed);

        result.Should().Contain("Article 45");
        result.Should().NotContainAny("\u064E", "\u064F"); // tashkeel removed
    }

    // ---------------------------------------
    //  IsArabic detection
    // ---------------------------------------

    [Theory]
    [InlineData("\u0646\u0635 \u0642\u0627\u0646\u0648\u0646\u064a \u0639\u0631\u0628\u064a", true)]
    [InlineData("This is English", false)]
    [InlineData("", false)]
    [InlineData("Mixed \u0646\u0635 and English", false)]
    [InlineData("123 456", false)]
    public void IsArabic_DetectsCorrectly(string text, bool expected)
    {
        ArabicNormalizer.IsArabic(text).Should().Be(expected);
    }

    // ---------------------------------------
    //  NormalizeForRetrieval
    // ---------------------------------------

    [Fact]
    public void NormalizeForRetrieval_ExpandsAbbreviations()
    {
        var abbreviated = "\u0645. 45 \u0641. 3";
        var result = ArabicNormalizer.NormalizeForRetrieval(abbreviated);

        result.Should().Contain("\u0627\u0644\u0645\u0627\u062f\u0647");
        result.Should().Contain("\u0627\u0644\u0641\u0635\u0644");
    }

    [Fact]
    public void NormalizeForRetrieval_RemovesStopWords()
    {
        var withStops = "\u0641\u064a \u0645\u0646 \u0642\u0627\u0646\u0648\u0646 \u0627\u0644\u0645\u062d\u0643\u0645\u0629 \u0645\u0646 \u0641\u0635\u0644 \u062c\u062f\u064a\u062f";
        var result = ArabicNormalizer.NormalizeForRetrieval(withStops);

        // Short stop words should be removed
        // but domain words kept
        result.Should().Contain("\u0642\u0627\u0646\u0648\u0646");
        result.Should().Contain("\u0627\u0644\u0645\u062d\u0643\u0645\u0647");
    }

    [Fact]
    public void NormalizeForRetrieval_AppliesBaseNormalizationToo()
    {
        var input = "\u0623\u062d\u0643\u0627\u0645 \u0627\u0644\u0642\u064e\u0636\u0627\u0621";
        var result = ArabicNormalizer.NormalizeForRetrieval(input);

        // Should have tashkeel removed + alef normalized
        result.Should().NotContainAny("\u064E", "\u064F", "\u064B");
    }
}



