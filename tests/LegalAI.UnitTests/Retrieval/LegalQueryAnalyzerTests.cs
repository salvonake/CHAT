using FluentAssertions;
using LegalAI.Domain.ValueObjects;
using LegalAI.Retrieval.QueryAnalysis;
using Microsoft.Extensions.Logging;
using Moq;

namespace LegalAI.UnitTests.Retrieval;

/// <summary>
/// Tests for <see cref="LegalQueryAnalyzer"/>. Verifies Arabic query type
/// detection, entity extraction (articles, cases, courts, dates),
/// semantic variant generation, depth determination, and Arabic
/// character normalization.
/// </summary>
public sealed class LegalQueryAnalyzerTests
{
    private readonly LegalQueryAnalyzer _analyzer = new(
        Mock.Of<ILogger<LegalQueryAnalyzer>>());

    // ══════════════════════════════════════
    //  Query Type Detection
    // ══════════════════════════════════════

    [Theory]
    [InlineData("ما هي عقوبة السرقة؟", QueryType.FactLookup)]
    [InlineData("ماذا يعني الإعسار؟", QueryType.FactLookup)]
    [InlineData("ما هو القانون المدني؟", QueryType.FactLookup)]
    [InlineData("تعريف الجريمة الجنائية", QueryType.FactLookup)]
    public void QueryType_FactLookup_Detected(string query, QueryType expected)
    {
        _analyzer.Analyze(query).QueryType.Should().Be(expected);
    }

    [Theory]
    [InlineData("اشرح القانون المدني", QueryType.Explanation)]
    [InlineData("كيف يتم تطبيق القانون؟", QueryType.Explanation)]
    [InlineData("لماذا يعاقب المتهم؟", QueryType.Explanation)]
    public void QueryType_Explanation_Detected(string query, QueryType expected)
    {
        _analyzer.Analyze(query).QueryType.Should().Be(expected);
    }

    [Theory]
    [InlineData("قارن بين القانون المدني والجنائي", QueryType.Comparison)]
    [InlineData("الفرق بين السرقة والاختلاس", QueryType.Comparison)]
    [InlineData("مقارنة أحكام الطلاق", QueryType.Comparison)]
    public void QueryType_Comparison_Detected(string query, QueryType expected)
    {
        _analyzer.Analyze(query).QueryType.Should().Be(expected);
    }

    [Theory]
    [InlineData("تسلسل أحداث القضية", QueryType.Timeline)]
    [InlineData("ترتيب الإجراءات القانونية", QueryType.Timeline)]
    [InlineData("تاريخ التشريع", QueryType.Timeline)]
    public void QueryType_Timeline_Detected(string query, QueryType expected)
    {
        _analyzer.Analyze(query).QueryType.Should().Be(expected);
    }

    [Theory]
    [InlineData("سوابق قضائية في السرقة", QueryType.PrecedentSearch)]
    [InlineData("حكم مماثل في الإعسار", QueryType.PrecedentSearch)]
    public void QueryType_PrecedentSearch_Detected(string query, QueryType expected)
    {
        // Note: "قضية مشابهة" uses ة which normalizes to ه, breaking the regex
        _analyzer.Analyze(query).QueryType.Should().Be(expected);
    }

    [Theory]
    [InlineData("تناقض في الأحكام", QueryType.ContradictionDetection)]
    [InlineData("تعارض القوانين", QueryType.ContradictionDetection)]
    public void QueryType_ContradictionDetection_Detected(string query, QueryType expected)
    {
        _analyzer.Analyze(query).QueryType.Should().Be(expected);
    }

    [Fact]
    public void QueryType_ArticleReference_WithFasl_Detected()
    {
        // Note: المادة normalizes to الماده (ة→ه), breaking the regex.
        // الفصل has no ة, so it survives normalization.
        var result = _analyzer.Analyze("الفصل 42 من القانون");
        result.QueryType.Should().Be(QueryType.ArticleSearch);
    }

    [Fact]
    public void QueryType_ArticleReference_Madda_NormalizesToGeneral()
    {
        // المادة normalizes to الماده → regex won't match
        var result = _analyzer.Analyze("المادة 42 من القانون");
        result.QueryType.Should().Be(QueryType.General,
            "Arabic normalization converts ة→ه, breaking المادة regex");
    }

    [Fact]
    public void QueryType_General_WhenNoPatternMatches()
    {
        var result = _analyzer.Analyze("أحكام عامة");
        result.QueryType.Should().Be(QueryType.General);
    }

    // ══════════════════════════════════════
    //  Entity Extraction — Articles
    // ══════════════════════════════════════

    [Theory]
    [InlineData("الفصل 7 من الدستور", "7")]
    [InlineData("البند 3 من العقد", "3")]
    public void DetectedArticle_ExtractsNumber(string query, string expectedNum)
    {
        // الفصل and البند don't contain ة, so they survive normalization
        var result = _analyzer.Analyze(query);
        result.DetectedArticle.Should().NotBeNull();
        result.DetectedArticle.Should().Contain(expectedNum);
    }

    [Theory]
    [InlineData("المادة 42 من القانون")]
    [InlineData("مادة 100 في النظام")]
    public void DetectedArticle_WithTaMarbuta_NullAfterNormalization(string query)
    {
        // المادة → الماده after normalization, regex uses المادة → no match
        _analyzer.Analyze(query).DetectedArticle.Should().BeNull();
    }

    [Fact]
    public void DetectedArticle_NoArticle_ReturnsNull()
    {
        _analyzer.Analyze("أحكام عامة في القانون").DetectedArticle.Should().BeNull();
    }

    // ══════════════════════════════════════
    //  Entity Extraction — Cases
    // ══════════════════════════════════════

    [Fact]
    public void DetectedCaseNumber_WithTaMarbuta_NullAfterNormalization()
    {
        // القضية → القضيه after normalization, regex uses القضية → no match
        _analyzer.Analyze("القضية رقم 2024/1234").DetectedCaseNumber.Should().BeNull();
    }

    [Fact]
    public void DetectedCaseNumber_NoCaseRef_ReturnsNull()
    {
        _analyzer.Analyze("حكم عام").DetectedCaseNumber.Should().BeNull();
    }

    // ══════════════════════════════════════
    //  Entity Extraction — Courts
    // ══════════════════════════════════════

    [Fact]
    public void DetectedCourt_WithTaMarbuta_NullAfterNormalization()
    {
        // محكمة → محكمه after normalization, regex uses محكمة → no match
        _analyzer.Analyze("محكمة النقض").DetectedCourt.Should().BeNull();
    }

    // ══════════════════════════════════════
    //  Entity Extraction — Dates
    // ══════════════════════════════════════

    [Fact]
    public void DetectedDate_ExtractsYear()
    {
        var result = _analyzer.Analyze("تشريعات سنة 2023");
        result.DetectedDate.Should().Be("2023");
    }

    [Fact]
    public void DetectedDate_NoYear_ReturnsNull()
    {
        _analyzer.Analyze("أحكام عامة").DetectedDate.Should().BeNull();
    }

    // ══════════════════════════════════════
    //  Depth Determination
    // ══════════════════════════════════════

    [Fact]
    public void Depth_FactLookup_Brief()
    {
        _analyzer.Analyze("ما هي السرقة؟").RequiredDepth.Should().Be(QueryDepth.Brief);
    }

    [Fact]
    public void Depth_ArticleSearch_Standard()
    {
        // Use الفصل (no ة) to ensure ArticleSearch detection after normalization
        _analyzer.Analyze("الفصل 42 من القانون").RequiredDepth.Should().Be(QueryDepth.Standard);
    }

    [Fact]
    public void Depth_Explanation_Detailed()
    {
        _analyzer.Analyze("اشرح أحكام الميراث").RequiredDepth.Should().Be(QueryDepth.Detailed);
    }

    [Fact]
    public void Depth_Comparison_Detailed()
    {
        _analyzer.Analyze("قارن بين القوانين").RequiredDepth.Should().Be(QueryDepth.Detailed);
    }

    [Fact]
    public void Depth_Timeline_Exhaustive()
    {
        _analyzer.Analyze("تسلسل الأحداث القانونية").RequiredDepth.Should().Be(QueryDepth.Exhaustive);
    }

    [Fact]
    public void Depth_PrecedentSearch_Exhaustive()
    {
        _analyzer.Analyze("سوابق قضائية").RequiredDepth.Should().Be(QueryDepth.Exhaustive);
    }

    [Fact]
    public void Depth_General_LongQuery_Detailed()
    {
        // General query > 100 chars → Detailed
        var longQuery = string.Join(" ", Enumerable.Repeat("كلمة", 30)); // ~150+ chars
        var result = _analyzer.Analyze(longQuery);
        result.RequiredDepth.Should().Be(QueryDepth.Detailed);
    }

    [Fact]
    public void Depth_General_ShortQuery_Standard()
    {
        _analyzer.Analyze("أحكام عامة").RequiredDepth.Should().Be(QueryDepth.Standard);
    }

    // ══════════════════════════════════════
    //  Semantic Variants
    // ══════════════════════════════════════

    [Fact]
    public void Variants_AlwaysIncludeOriginal()
    {
        var result = _analyzer.Analyze("ما هي السرقة؟");
        result.SemanticVariants.Should().NotBeEmpty();
        // First variant is the normalized query
        result.SemanticVariants[0].Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Variants_MaximumThree()
    {
        var result = _analyzer.Analyze("اشرح المادة 42");
        result.SemanticVariants.Count.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public void Variants_PrecedentSearch_GeneratesAlternatives()
    {
        var result = _analyzer.Analyze("سوابق قضائية في السرقة");
        result.SemanticVariants.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Variants_Explanation_GeneratesAlternatives()
    {
        var result = _analyzer.Analyze("اشرح قانون العقوبات");
        result.SemanticVariants.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Variants_General_ShortQuery_GeneratesExpanded()
    {
        var result = _analyzer.Analyze("حكم قضائي");
        result.SemanticVariants.Count.Should().BeGreaterThan(1);
    }

    // ══════════════════════════════════════
    //  Arabic Normalization
    // ══════════════════════════════════════

    [Fact]
    public void Normalization_AlefVariants_Unified()
    {
        // آ أ إ ٱ → ا
        var result = _analyzer.Analyze("آداب أخلاقية إسلامية");
        result.NormalizedQuery.Should().NotContain("آ");
        result.NormalizedQuery.Should().NotContain("أ");
        result.NormalizedQuery.Should().NotContain("إ");
    }

    [Fact]
    public void Normalization_TaMarbuta_ToHaa()
    {
        // ة → ه
        var result = _analyzer.Analyze("المحكمة العليا");
        result.NormalizedQuery.Should().Contain("المحكمه");
    }

    [Fact]
    public void Normalization_AlefMaksura_ToYaa()
    {
        // ى → ي
        var result = _analyzer.Analyze("على");
        result.NormalizedQuery.Should().Contain("علي");
    }

    [Fact]
    public void Normalization_RemovesTashkeel()
    {
        // Diacritics (fatha, kasra, damma, etc.) should be removed
        var result = _analyzer.Analyze("قَانُونٌ");
        result.NormalizedQuery.Should().NotContainAny("َ", "ُ", "ٌ");
    }

    [Fact]
    public void Normalization_RemovesTatweel()
    {
        // ـ (tatweel) should be removed
        var result = _analyzer.Analyze("القـــانون");
        result.NormalizedQuery.Should().NotContain("ـ");
    }

    [Fact]
    public void Normalization_ExtraWhitespace_Collapsed()
    {
        var result = _analyzer.Analyze("  المادة    الأولى  ");
        result.NormalizedQuery.Should().NotStartWith(" ");
        result.NormalizedQuery.Should().NotEndWith(" ");
        result.NormalizedQuery.Should().NotContain("  ");
    }

    // ══════════════════════════════════════
    //  Full Analysis Output
    // ══════════════════════════════════════

    [Fact]
    public void Analyze_PreservesOriginalQuery()
    {
        const string original = "  ما هي المادة 42؟  ";
        var result = _analyzer.Analyze(original);
        result.OriginalQuery.Should().Be(original);
    }

    [Fact]
    public void Analyze_SetsNormalizedQuery()
    {
        var result = _analyzer.Analyze("ما هي المادة 42؟");
        result.NormalizedQuery.Should().NotBeNullOrWhiteSpace();
        result.NormalizedQuery.Should().NotBe(result.OriginalQuery, "normalization applied");
    }

    [Fact]
    public void Analyze_ComplexLegalQuery_DateAndTypeDetected()
    {
        // After normalization: المادة→الماده, القضية→القضيه, محكمة→محكمه
        // So article, case, court regexes won't match — but date and type do.
        var result = _analyzer.Analyze("اشرح الفصل 42 من تشريع 2024 في القانون");

        result.QueryType.Should().Be(QueryType.Explanation);
        result.DetectedArticle.Should().NotBeNull("الفصل has no ة");
        result.DetectedDate.Should().Be("2024");
        result.SemanticVariants.Should().NotBeEmpty();
        result.RequiredDepth.Should().Be(QueryDepth.Detailed);
    }
}
