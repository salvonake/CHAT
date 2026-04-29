using System.IO;
using FluentAssertions;
using Poseidon.Desktop;
using Poseidon.Desktop.Services;
using Poseidon.Domain.Interfaces;
using Poseidon.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Poseidon.UnitTests.Services;

/// <summary>
/// Tests for <see cref="FailClosedGuard.ValidateAnswer"/>.
/// Answer validation is the last safety gate before displaying legally-binding
/// information to users. These tests verify that dangerous answers are flagged.
/// </summary>
public sealed class AnswerValidationTests : IDisposable
{
    private readonly FailClosedGuard _guard;

    public AnswerValidationTests()
    {
        var llm = new Mock<ILlmService>();
        llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var vectorStore = new Mock<IVectorStore>();
        vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VectorStoreHealth { IsHealthy = true });

        var modelIntegrity = new Mock<ModelIntegrityService>(
            MockBehavior.Loose,
            Mock.Of<IConfiguration>(),
            new DataPaths
            {
                DataDirectory = Path.GetTempPath(),
                ModelsDirectory = Path.GetTempPath(),
                VectorDbPath = Path.Combine(Path.GetTempPath(), "v.db"),
                HnswIndexPath = Path.Combine(Path.GetTempPath(), "v.hnsw"),
                DocumentDbPath = Path.Combine(Path.GetTempPath(), "d.db"),
                AuditDbPath = Path.Combine(Path.GetTempPath(), "a.db"),
                WatchDirectory = Path.GetTempPath()
            },
            Mock.Of<ILogger<ModelIntegrityService>>()
        );
        modelIntegrity.SetupGet(m => m.LlmModelExists).Returns(true);
        modelIntegrity.SetupGet(m => m.LlmModelValid).Returns(true);
        modelIntegrity.SetupGet(m => m.EmbeddingModelExists).Returns(true);
        modelIntegrity.SetupGet(m => m.EmbeddingModelValid).Returns(true);

        _guard = new FailClosedGuard(
            llm.Object, vectorStore.Object, modelIntegrity.Object,
            Mock.Of<ILogger<FailClosedGuard>>());
    }

    private static List<Citation> MakeCitations(params string[] snippets)
    {
        return snippets.Select((s, i) => new Citation
        {
            Document = $"doc{i}.pdf",
            Page = i + 1,
            Snippet = s,
            SimilarityScore = 0.85
        }).ToList();
    }

    // ═══════════════════════════════════════
    //  Rule 1: Abstention is always safe
    // ═══════════════════════════════════════

    [Fact]
    public void ValidateAnswer_Abstention_AlwaysSafe()
    {
        var result = _guard.ValidateAnswer(
            answer: "لا توجد أدلة كافية",
            citations: [],
            confidenceScore: 0.1,
            isAbstention: true);

        result.Severity.Should().Be(AnswerSeverity.Safe);
        result.Issues.Should().BeEmpty();
    }

    // ═══════════════════════════════════════
    //  Rule 2: Must have citations
    // ═══════════════════════════════════════

    [Fact]
    public void ValidateAnswer_NoCitations_Critical()
    {
        var result = _guard.ValidateAnswer(
            answer: "المادة 45 تنص على كذا",
            citations: [],
            confidenceScore: 0.8,
            isAbstention: false);

        result.Severity.Should().Be(AnswerSeverity.Critical);
        result.Issues.Should().NotBeEmpty();
    }

    [Fact]
    public void ValidateAnswer_NullCitations_Critical()
    {
        var result = _guard.ValidateAnswer(
            answer: "حكم قانوني مهم",
            citations: null,
            confidenceScore: 0.9,
            isAbstention: false);

        result.Severity.Should().Be(AnswerSeverity.Critical);
    }

    [Fact]
    public void ValidateAnswer_WithCitations_NoCitationIssue()
    {
        var citations = MakeCitations("المادة 45 من قانون العمل");

        var result = _guard.ValidateAnswer(
            answer: "بحسب المادة 45 من قانون العمل",
            citations: citations,
            confidenceScore: 0.85,
            isAbstention: false);

        result.Should().NotBeNull();
        // Should not have citation-count issue (may have other issues)
        result.Issues.Should().NotContain(i =>
            i.Contains("استشهادات كافية") || i.Contains("sufficient citations"));
    }

    // ═══════════════════════════════════════
    //  Rule 3: Confidence thresholds
    // ═══════════════════════════════════════

    [Fact]
    public void ValidateAnswer_VeryLowConfidence_Critical()
    {
        var citations = MakeCitations("some snippet");

        var result = _guard.ValidateAnswer(
            answer: "Answer text",
            citations: citations,
            confidenceScore: 0.20,
            isAbstention: false);

        result.Severity.Should().Be(AnswerSeverity.Critical);
        result.Issues.Should().Contain(i => i.Contains("منخفضة") || i.Contains("low"));
    }

    [Fact]
    public void ValidateAnswer_MediumConfidence_Warning()
    {
        var citations = MakeCitations("referenced snippet with المادة content");

        var result = _guard.ValidateAnswer(
            answer: "Answer based on referenced snippet",
            citations: citations,
            confidenceScore: 0.50,
            isAbstention: false);

        result.Severity.Should().BeOneOf(AnswerSeverity.Warning, AnswerSeverity.Critical);
    }

    [Fact]
    public void ValidateAnswer_HighConfidence_NoConfidenceIssue()
    {
        var citations = MakeCitations("reference snippet المادة 10");

        var result = _guard.ValidateAnswer(
            answer: "بناء على المادة 10",
            citations: citations,
            confidenceScore: 0.90,
            isAbstention: false);

        result.Issues.Should().NotContain(i =>
            i.Contains("منخفضة") || i.Contains("low") || i.Contains("below"));
    }

    // ═══════════════════════════════════════
    //  Rule 4: Fabrication detection
    // ═══════════════════════════════════════

    [Theory]
    [InlineData("بناءً على معرفتي، القانون ينص على...")]
    [InlineData("I believe this is the correct interpretation")]
    [InlineData("من المحتمل أن هذا ينطبق على حالتك")]
    [InlineData("بشكل عام، القوانين تنص على")]
    [InlineData("As an AI, I think the answer is")]
    [InlineData("as a language model I cannot be sure")]
    public void ValidateAnswer_FabricationIndicator_Critical(string answer)
    {
        var citations = MakeCitations("some citation snippet");

        var result = _guard.ValidateAnswer(
            answer: answer,
            citations: citations,
            confidenceScore: 0.85,
            isAbstention: false);

        result.Severity.Should().Be(AnswerSeverity.Critical);
        result.Issues.Should().Contain(i =>
            i.Contains("معرفة خارجية") || i.Contains("external knowledge"));
    }

    [Fact]
    public void ValidateAnswer_CleanAnswer_NoFabricationFlag()
    {
        var citations = MakeCitations("المادة 77 من نظام العمل تنص على حق الفسخ");

        var result = _guard.ValidateAnswer(
            answer: "وفقاً للمادة 77 من نظام العمل، يحق لصاحب العمل إنهاء العقد [المصدر: doc0.pdf، صفحة 1]",
            citations: citations,
            confidenceScore: 0.88,
            isAbstention: false);

        result.Issues.Should().NotContain(i =>
            i.Contains("معرفة خارجية") || i.Contains("external knowledge"));
    }

    // ═══════════════════════════════════════
    //  Rule 5: Citation cross-check
    // ═══════════════════════════════════════

    [Fact]
    public void ValidateAnswer_ArticleInAnswerButNotInCitations_Warning()
    {
        // Answer mentions المادة 99, but citations don't contain "99"
        var citations = MakeCitations("المادة 10 تنص على الحقوق");

        var result = _guard.ValidateAnswer(
            answer: "وفقاً للمادة 99، يحق للمتهم الاستئناف",
            citations: citations,
            confidenceScore: 0.80,
            isAbstention: false);

        result.Issues.Should().Contain(i =>
            i.Contains("غير موجودة") || i.Contains("not found in citations"));
    }

    [Fact]
    public void ValidateAnswer_ArticleFoundInCitations_NoWarning()
    {
        var citations = MakeCitations("المادة 45 من قانون العمل تنص على");

        var result = _guard.ValidateAnswer(
            answer: "بناء على المادة 45 من القانون",
            citations: citations,
            confidenceScore: 0.90,
            isAbstention: false);

        result.Issues.Should().NotContain(i =>
            i.Contains("غير موجودة") || i.Contains("not found in citations"));
    }

    // ═══════════════════════════════════════
    //  Combined issues
    // ═══════════════════════════════════════

    [Fact]
    public void ValidateAnswer_MultipleIssues_AllCaptured()
    {
        // No citations + low confidence + fabrication indicator
        var result = _guard.ValidateAnswer(
            answer: "بناءً على معرفتي، المادة 99 تنص على ذلك",
            citations: null,
            confidenceScore: 0.15,
            isAbstention: false);

        result.Severity.Should().Be(AnswerSeverity.Critical);
        result.Issues.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    // ═══════════════════════════════════════
    //  Safe, well-formed answer
    // ═══════════════════════════════════════

    [Fact]
    public void ValidateAnswer_PerfectAnswer_Safe()
    {
        var citations = MakeCitations(
            "المادة 77: يحق لأي من الطرفين إنهاء عقد العمل",
            "المادة 78: يجب على صاحب العمل دفع التعويض");

        var result = _guard.ValidateAnswer(
            answer: "بموجب المادة 77 و المادة 78 من نظام العمل، يحق لصاحب العمل إنهاء العقد مع التعويض.",
            citations: citations,
            confidenceScore: 0.92,
            isAbstention: false);

        result.Severity.Should().Be(AnswerSeverity.Safe);
        result.Issues.Should().BeEmpty();
    }

    public void Dispose()
    {
        _guard.Dispose();
    }
}


