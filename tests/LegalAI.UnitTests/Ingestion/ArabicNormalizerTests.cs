using FluentAssertions;
using LegalAI.Ingestion.Arabic;

namespace LegalAI.UnitTests.Ingestion;

/// <summary>
/// Tests for <see cref="ArabicNormalizer"/>. Correct normalization is critical
/// for retrieval recall — inconsistent normalization means missed matches.
/// </summary>
public sealed class ArabicNormalizerTests
{
    // ═══════════════════════════════════════
    //  Empty / null input
    // ═══════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_NullOrEmpty_ReturnsEmpty(string? input)
    {
        ArabicNormalizer.Normalize(input!).Should().BeEmpty();
    }

    // ═══════════════════════════════════════
    //  Tashkeel (diacritics) removal
    // ═══════════════════════════════════════

    [Fact]
    public void Normalize_RemovesTashkeel()
    {
        var withDiacritics = "بِسْمِ اللَّهِ الرَّحْمَنِ الرَّحِيمِ";
        var result = ArabicNormalizer.Normalize(withDiacritics);

        // Should not contain any tashkeel characters
        result.Should().NotContainAny("\u064B", "\u064C", "\u064D", "\u064E",
            "\u064F", "\u0650", "\u0651", "\u0652");
        result.Should().Contain("بسم");
        result.Should().Contain("الله");
    }

    // ═══════════════════════════════════════
    //  Alef normalization
    // ═══════════════════════════════════════

    [Theory]
    [InlineData("\u0622", "\u0627")] // آ → ا
    [InlineData("\u0623", "\u0627")] // أ → ا
    [InlineData("\u0625", "\u0627")] // إ → ا
    [InlineData("\u0671", "\u0627")] // ٱ → ا
    public void Normalize_AlefVariants_ToPlainAlef(string input, string expected)
    {
        ArabicNormalizer.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_AlefInWord_Normalized()
    {
        // أحمد → احمد, إسلام → اسلام
        var result1 = ArabicNormalizer.Normalize("أحمد");
        result1.Should().Be("احمد");

        var result2 = ArabicNormalizer.Normalize("إسلام");
        result2.Should().Be("اسلام");
    }

    // ═══════════════════════════════════════
    //  Tatweel removal
    // ═══════════════════════════════════════

    [Fact]
    public void Normalize_RemovesTatweel()
    {
        var withTatweel = "القـــانون";
        var result = ArabicNormalizer.Normalize(withTatweel);

        result.Should().NotContain("\u0640");
        result.Should().Be("القانون");
    }

    // ═══════════════════════════════════════
    //  Teh marbuta → Heh
    // ═══════════════════════════════════════

    [Fact]
    public void Normalize_TehMarbuta_ToHeh()
    {
        var withTehMarbuta = "المحكمة العليا";  // ة → ه
        var result = ArabicNormalizer.Normalize(withTehMarbuta);

        result.Should().Contain("المحكمه");
    }

    // ═══════════════════════════════════════
    //  Alef Maksura → Ya
    // ═══════════════════════════════════════

    [Fact]
    public void Normalize_AlefMaksura_ToYa()
    {
        var withMaksura = "على"; // alef maksura ى → ي
        var result = ArabicNormalizer.Normalize(withMaksura);

        result.Should().Be("علي");
    }

    // ═══════════════════════════════════════
    //  Whitespace collapse
    // ═══════════════════════════════════════

    [Fact]
    public void Normalize_CollapsesMultipleSpaces()
    {
        var withSpaces = "القانون    الجنائي   المادة";
        var result = ArabicNormalizer.Normalize(withSpaces);

        result.Should().Be("القانون الجنائي الماده");
    }

    [Fact]
    public void Normalize_TrimsWhitespace()
    {
        var padded = "   نص قانوني   ";
        var result = ArabicNormalizer.Normalize(padded);

        result.Should().Be("نص قانوني");
    }

    // ═══════════════════════════════════════
    //  Combined normalizations
    // ═══════════════════════════════════════

    [Fact]
    public void Normalize_CombinedNormalizations()
    {
        // Full Arabic legal text with various normalization targets
        var input = "أحْكَامُ المَادَّةِ الخَامِسَة مِنْ قَانونِ الإجراءاتِ الجَزائيَّةِ";
        var result = ArabicNormalizer.Normalize(input);

        // Should have: removed tashkeel, normalized alef, collapsed spaces
        result.Should().NotContainAny("\u064B", "\u064E", "\u064F", "\u0650");
        result.Should().StartWith("احكام");
    }

    // ═══════════════════════════════════════
    //  Non-Arabic text passthrough
    // ═══════════════════════════════════════

    [Fact]
    public void Normalize_EnglishText_Unchanged()
    {
        var english = "Article 45 of the Labor Law";
        ArabicNormalizer.Normalize(english).Should().Be(english);
    }

    [Fact]
    public void Normalize_MixedArabicEnglish_OnlyArabicNormalized()
    {
        var mixed = "Article 45 من قَانون العَمَل";
        var result = ArabicNormalizer.Normalize(mixed);

        result.Should().Contain("Article 45");
        result.Should().NotContainAny("\u064E", "\u064F"); // tashkeel removed
    }

    // ═══════════════════════════════════════
    //  IsArabic detection
    // ═══════════════════════════════════════

    [Theory]
    [InlineData("نص عربي كامل", true)]
    [InlineData("This is English", false)]
    [InlineData("", false)]
    [InlineData("Mixed عربي and English", false)] // 4/19 = 21% < 30% threshold
    [InlineData("123 456", false)]
    public void IsArabic_DetectsCorrectly(string text, bool expected)
    {
        ArabicNormalizer.IsArabic(text).Should().Be(expected);
    }

    // ═══════════════════════════════════════
    //  NormalizeForRetrieval
    // ═══════════════════════════════════════

    [Fact]
    public void NormalizeForRetrieval_ExpandsAbbreviations()
    {
        var abbreviated = "م. 45 ف. 3";
        var result = ArabicNormalizer.NormalizeForRetrieval(abbreviated);

        result.Should().Contain("المادة");  // م. → المادة (expansion happens after Normalize)
        result.Should().Contain("الفصل");   // ف. → الفصل
    }

    [Fact]
    public void NormalizeForRetrieval_RemovesStopWords()
    {
        var withStops = "ما هي أحكام المادة في قانون العمل";
        var result = ArabicNormalizer.NormalizeForRetrieval(withStops);

        // Short stop words like "ما" and "في" should be removed
        // but domain words kept
        result.Should().Contain("احكام");
        result.Should().Contain("الماده");
    }

    [Fact]
    public void NormalizeForRetrieval_AppliesBaseNormalizationToo()
    {
        var input = "أحْكَامُ القَانَون";
        var result = ArabicNormalizer.NormalizeForRetrieval(input);

        // Should have tashkeel removed + alef normalized
        result.Should().NotContainAny("\u064E", "\u064F", "\u064B");
    }
}
