using System.Collections.ObjectModel;
using System.IO;
using FluentAssertions;
using LegalAI.Application.Queries;
using LegalAI.Desktop;
using LegalAI.Desktop.Services;
using LegalAI.Desktop.ViewModels;
using LegalAI.Domain.Interfaces;
using LegalAI.Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace LegalAI.UnitTests.ViewModels;

/// <summary>
/// Tests for <see cref="AskViewModel"/>. This is the most safety-critical
/// ViewModel — it drives the Evidence-Constrained RAG pipeline, handles
/// fail-closed gating, confidence scoring, abstention, answer validation,
/// and citation display. In a life-critical legal system, every path matters.
/// </summary>
public sealed class AskViewModelTests : IDisposable
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<ILlmService> _llm = new();
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<IDispatcherService> _dispatcher = new();
    private readonly Mock<ILogger<AskViewModel>> _logger = new();
    private readonly Mock<ModelIntegrityService> _modelIntegrity;
    private readonly FailClosedGuard _guard;

    public AskViewModelTests()
    {
        // ModelIntegrityService is not sealed, so we can mock it
        _modelIntegrity = CreateMockModelIntegrity(
            llmExists: true, llmValid: true,
            embExists: true, embValid: true);

        // Default: LLM available, vector store healthy → guard is operational
        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VectorStoreHealth { IsHealthy = true, VectorCount = 100 });

        // Real FailClosedGuard with mocked dependencies
        _guard = new FailClosedGuard(
            _llm.Object,
            _vectorStore.Object,
            _modelIntegrity.Object,
            Mock.Of<ILogger<FailClosedGuard>>());

        // Dispatcher runs actions synchronously in tests
        _dispatcher.Setup(d => d.InvokeAsync(It.IsAny<Action>()))
            .Callback<Action>(a => a())
            .Returns(Task.CompletedTask);
        _dispatcher.Setup(d => d.Invoke(It.IsAny<Action>()))
            .Callback<Action>(a => a());
    }

    public void Dispose()
    {
        _guard.Dispose();
    }

    // ══════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════

    private static Mock<ModelIntegrityService> CreateMockModelIntegrity(
        bool llmExists = true, bool llmValid = true,
        bool embExists = true, bool embValid = true)
    {
        var mock = new Mock<ModelIntegrityService>(
            MockBehavior.Loose,
            Mock.Of<IConfiguration>(),
            new DataPaths
            {
                DataDirectory = Path.GetTempPath(),
                ModelsDirectory = Path.GetTempPath(),
                VectorDbPath = Path.Combine(Path.GetTempPath(), "ask-test.db"),
                HnswIndexPath = Path.Combine(Path.GetTempPath(), "ask-test.hnsw"),
                DocumentDbPath = Path.Combine(Path.GetTempPath(), "ask-docs.db"),
                AuditDbPath = Path.Combine(Path.GetTempPath(), "ask-audit.db"),
                WatchDirectory = Path.GetTempPath()
            },
            Mock.Of<ILogger<ModelIntegrityService>>());

        mock.SetupGet(m => m.LlmModelExists).Returns(llmExists);
        mock.SetupGet(m => m.LlmModelValid).Returns(llmValid);
        mock.SetupGet(m => m.EmbeddingModelExists).Returns(embExists);
        mock.SetupGet(m => m.EmbeddingModelValid).Returns(embValid);

        return mock;
    }

    private async Task<AskViewModel> CreateVmAsync()
    {
        // Initialize the guard so CanAskQuestions is true
        await _guard.InitializeAsync();

        var vm = new AskViewModel(
            _mediator.Object,
            _llm.Object,
            _guard,
            _dispatcher.Object,
            _logger.Object);

        // Allow constructor's fire-and-forget CheckLlmAvailabilityAsync to complete
        await Task.Delay(200);

        return vm;
    }

    private static LegalAnswer CreateHighConfidenceAnswer(
        string text = "الإجابة القانونية بناءً على المادة 42.",
        double confidence = 0.92,
        bool isAbstention = false,
        List<Citation>? citations = null,
        List<string>? warnings = null,
        double generationLatencyMs = 450,
        double retrievalLatencyMs = 120)
    {
        return new LegalAnswer
        {
            Answer = text,
            ConfidenceScore = confidence,
            IsAbstention = isAbstention,
            RetrievedChunksUsed = 5,
            RetrievalSimilarityAvg = 0.85,
            Citations = citations ?? new List<Citation>
            {
                new()
                {
                    Document = "قانون_العقوبات.pdf",
                    Page = 42,
                    Snippet = "المادة 42: يعاقب كل من...",
                    SimilarityScore = 0.95
                },
                new()
                {
                    Document = "قانون_الإجراءات.pdf",
                    Page = 15,
                    Snippet = "المادة 15: تكون المحكمة...",
                    SimilarityScore = 0.88
                }
            },
            Warnings = warnings ?? new List<string>(),
            GenerationLatencyMs = generationLatencyMs,
            RetrievalLatencyMs = retrievalLatencyMs
        };
    }

    private static LegalAnswer CreateLowConfidenceAnswer() =>
        new()
        {
            Answer = "إجابة منخفضة الثقة.",
            ConfidenceScore = 0.30,
            IsAbstention = false,
            RetrievedChunksUsed = 2,
            RetrievalSimilarityAvg = 0.30,
            Citations = new List<Citation>
            {
                new()
                {
                    Document = "doc.pdf",
                    Page = 1,
                    Snippet = "نص",
                    SimilarityScore = 0.3
                }
            },
            Warnings = new List<string> { "تحذير: ثقة منخفضة" },
            GenerationLatencyMs = 200,
            RetrievalLatencyMs = 50
        };

    private static LegalAnswer CreateAbstentionAnswer() =>
        new()
        {
            Answer = "",
            ConfidenceScore = 0.0,
            IsAbstention = true,
            AbstentionReason = "لم يتم العثور على معلومات كافية",
            RetrievedChunksUsed = 0,
            RetrievalSimilarityAvg = 0.0,
            Citations = new List<Citation>(),
            Warnings = new List<string>(),
            GenerationLatencyMs = 100,
            RetrievalLatencyMs = 80
        };

    private static LegalAnswer CreateMediumConfidenceAnswer() =>
        new()
        {
            Answer = "إجابة بثقة متوسطة بناءً على المادة 10.",
            ConfidenceScore = 0.65,
            IsAbstention = false,
            RetrievedChunksUsed = 3,
            RetrievalSimilarityAvg = 0.60,
            Citations = new List<Citation>
            {
                new()
                {
                    Document = "نظام.pdf",
                    Page = 10,
                    Snippet = "المادة 10: ينص النظام...",
                    SimilarityScore = 0.70
                }
            },
            Warnings = new List<string>(),
            GenerationLatencyMs = 300,
            RetrievalLatencyMs = 100
        };

    private void SetupMediatorReturn(LegalAnswer answer)
    {
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(answer);
    }

    // ══════════════════════════════════════
    //  Initial State
    // ══════════════════════════════════════

    [Fact]
    public async Task InitialState_DefaultValues_AreCorrect()
    {
        var vm = await CreateVmAsync();

        vm.Question.Should().BeEmpty();
        vm.AnswerText.Should().BeEmpty();
        vm.HasAnswer.Should().BeFalse();
        vm.IsProcessing.Should().BeFalse();
        vm.IsAbstention.Should().BeFalse();
        vm.ShowSafetyWarning.Should().BeFalse();
        vm.StrictMode.Should().BeTrue("fail-closed: strict mode is always on");
        vm.TopK.Should().Be(10);
        vm.QueryHistory.Should().BeEmpty();
    }

    [Fact]
    public async Task InitialState_LlmAvailable_WhenServiceResponds()
    {
        var vm = await CreateVmAsync();
        vm.LlmAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task InitialState_LlmUnavailable_WhenServiceFails()
    {
        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Recreate guard with LLM unavailable
        using var guard = new FailClosedGuard(
            _llm.Object, _vectorStore.Object,
            _modelIntegrity.Object, Mock.Of<ILogger<FailClosedGuard>>());
        await guard.InitializeAsync();

        var vm = new AskViewModel(
            _mediator.Object, _llm.Object, guard,
            _dispatcher.Object, _logger.Object);
        await Task.Delay(200);

        vm.LlmAvailable.Should().BeFalse();

        guard.Dispose();
    }

    [Fact]
    public async Task InitialState_LlmUnavailable_WhenServiceThrows()
    {
        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("LLM crashed"));

        var vm = new AskViewModel(
            _mediator.Object, _llm.Object, _guard,
            _dispatcher.Object, _logger.Object);
        await Task.Delay(200);

        vm.LlmAvailable.Should().BeFalse("exception means LLM is unavailable");
    }

    // ══════════════════════════════════════
    //  StrictMode Lock
    // ══════════════════════════════════════

    [Fact]
    public async Task StrictMode_AlwaysTrue_CannotBeDisabled()
    {
        var vm = await CreateVmAsync();

        vm.StrictMode = false;
        // StrictMode property is just an [ObservableProperty], but AskQuestionAsync
        // always sets StrictMode = true on the query object.
        // We verify this in the query sent to MediatR.

        var answer = CreateHighConfidenceAnswer();
        SetupMediatorReturn(answer);
        vm.Question = "ما هي المادة 42؟";

        await vm.AskQuestionCommand.ExecuteAsync(null);

        _mediator.Verify(m => m.Send(
            It.Is<AskLegalQuestionQuery>(q => q.StrictMode == true),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ══════════════════════════════════════
    //  Empty Question Rejected
    // ══════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task AskQuestion_EmptyOrWhitespace_DoesNothing(string? question)
    {
        var vm = await CreateVmAsync();
        vm.Question = question ?? "";

        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.HasAnswer.Should().BeFalse();
        vm.AnswerText.Should().BeEmpty();
        _mediator.Verify(m => m.Send(
            It.IsAny<AskLegalQuestionQuery>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ══════════════════════════════════════
    //  Library-Only Mode (Fail-Closed)
    // ══════════════════════════════════════

    [Fact]
    public async Task AskQuestion_LibraryOnlyMode_BlocksWithSafetyWarning()
    {
        var vm = await CreateVmAsync();

        // Activate library-only mode
        vm.SetLibraryOnlyMode(true, new[] { "Model missing", "Integrity failed" });

        vm.Question = "ما هي المادة 42؟";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.AnswerText.Should().Contain("⛔");
        vm.AnswerText.Should().Contain("library-only mode");
        vm.HasAnswer.Should().BeTrue();
        vm.ShowSafetyWarning.Should().BeTrue();
        vm.SafetyWarningText.Should().NotBeEmpty();

        _mediator.Verify(m => m.Send(
            It.IsAny<AskLegalQuestionQuery>(),
            It.IsAny<CancellationToken>()),
            Times.Never, "should NOT send query in library-only mode");
    }

    [Fact]
    public async Task AskQuestion_GuardCannotAsk_BlocksWithSafetyWarning()
    {
        // Create guard where model is missing → CanAskQuestions = false
        _modelIntegrity.SetupGet(m => m.LlmModelExists).Returns(false);

        using var blockedGuard = new FailClosedGuard(
            _llm.Object, _vectorStore.Object,
            _modelIntegrity.Object, Mock.Of<ILogger<FailClosedGuard>>());
        await blockedGuard.InitializeAsync();

        blockedGuard.CanAskQuestions.Should().BeFalse("model is missing");

        var vm = new AskViewModel(
            _mediator.Object, _llm.Object, blockedGuard,
            _dispatcher.Object, _logger.Object);
        await Task.Delay(200);

        vm.Question = "سؤال قانوني";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.AnswerText.Should().Contain("⛔");
        vm.ShowSafetyWarning.Should().BeTrue();

        _mediator.Verify(m => m.Send(
            It.IsAny<AskLegalQuestionQuery>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SetLibraryOnlyMode_True_SetsStateAndClearsLlm()
    {
        var vm = await CreateVmAsync();
        vm.LlmAvailable.Should().BeTrue();

        vm.SetLibraryOnlyMode(true, new[] { "Guard blocked" });

        vm.IsLibraryOnlyMode.Should().BeTrue();
        vm.LlmAvailable.Should().BeFalse();
        vm.LibraryOnlyReason.Should().Contain("Guard blocked");
    }

    [Fact]
    public async Task SetLibraryOnlyMode_False_RestoresState()
    {
        var vm = await CreateVmAsync();
        vm.SetLibraryOnlyMode(true, new[] { "Blocked" });
        vm.LlmAvailable.Should().BeFalse();

        vm.SetLibraryOnlyMode(false, Array.Empty<string>());
        await Task.Delay(200); // Allow CheckLlmAvailabilityAsync

        vm.IsLibraryOnlyMode.Should().BeFalse();
        vm.LibraryOnlyReason.Should().BeEmpty();
        vm.LlmAvailable.Should().BeTrue("LLM check should resume");
    }

    // ══════════════════════════════════════
    //  Normal Answer Flow
    // ══════════════════════════════════════

    [Fact]
    public async Task AskQuestion_NormalAnswer_DisplaysResultCorrectly()
    {
        var answer = CreateHighConfidenceAnswer();
        SetupMediatorReturn(answer);

        var vm = await CreateVmAsync();
        vm.Question = "ما هي المادة 42؟";

        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.AnswerText.Should().Be(answer.Answer);
        vm.HasAnswer.Should().BeTrue();
        vm.IsProcessing.Should().BeFalse();
        vm.ConfidenceScore.Should().Be(0.92);
        vm.IsAbstention.Should().BeFalse();
    }

    [Fact]
    public async Task AskQuestion_PopulatesCitations()
    {
        var answer = CreateHighConfidenceAnswer();
        SetupMediatorReturn(answer);

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.Citations.Should().HaveCount(2);
        vm.Citations[0].FileName.Should().Be("قانون_العقوبات.pdf");
        vm.Citations[0].PageNumber.Should().Be(42);
        vm.Citations[0].Content.Should().Contain("المادة 42");
        vm.Citations[0].SimilarityScore.Should().BeApproximately(0.95, 0.01);

        vm.Citations[1].FileName.Should().Be("قانون_الإجراءات.pdf");
        vm.Citations[1].PageNumber.Should().Be(15);
    }

    [Fact]
    public async Task AskQuestion_PopulatesWarnings()
    {
        var answer = CreateHighConfidenceAnswer(
            warnings: new List<string> { "تحذير 1", "تحذير 2" });
        SetupMediatorReturn(answer);

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.Warnings.Should().HaveCount(2);
        vm.Warnings[0].Should().Be("تحذير 1");
        vm.Warnings[1].Should().Be("تحذير 2");
    }

    [Fact]
    public async Task AskQuestion_CalculatesLatency()
    {
        var answer = CreateHighConfidenceAnswer(
            generationLatencyMs: 500,
            retrievalLatencyMs: 150);
        SetupMediatorReturn(answer);

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.LatencyMs.Should().Be(650);
    }

    [Fact]
    public async Task AskQuestion_SendsQueryWithCorrectParameters()
    {
        var answer = CreateHighConfidenceAnswer();
        SetupMediatorReturn(answer);

        var vm = await CreateVmAsync();
        vm.Question = "ما هي عقوبة السرقة؟";
        vm.CaseNamespace = "criminal_law";
        vm.TopK = 5;

        await vm.AskQuestionCommand.ExecuteAsync(null);

        _mediator.Verify(m => m.Send(
            It.Is<AskLegalQuestionQuery>(q =>
                q.Question == "ما هي عقوبة السرقة؟" &&
                q.CaseNamespace == "criminal_law" &&
                q.StrictMode == true &&
                q.TopK == 5),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AskQuestion_NullCaseNamespace_SendsNull()
    {
        SetupMediatorReturn(CreateHighConfidenceAnswer());

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        vm.CaseNamespace = "   "; // whitespace → treated as null

        await vm.AskQuestionCommand.ExecuteAsync(null);

        _mediator.Verify(m => m.Send(
            It.Is<AskLegalQuestionQuery>(q => q.CaseNamespace == null),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AskQuestion_NullCitations_HandlesGracefully()
    {
        var answer = new LegalAnswer
        {
            Answer = "إجابة بدون استشهادات",
            ConfidenceScore = 0.9,
            IsAbstention = false,
            RetrievedChunksUsed = 0,
            RetrievalSimilarityAvg = 0.0,
            Citations = null!,
            Warnings = new List<string>(),
            GenerationLatencyMs = 100,
            RetrievalLatencyMs = 50
        };
        SetupMediatorReturn(answer);

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.Citations.Should().BeEmpty();
        vm.HasAnswer.Should().BeTrue();
    }

    [Fact]
    public async Task AskQuestion_NullWarnings_HandlesGracefully()
    {
        var answer = new LegalAnswer
        {
            Answer = "إجابة مع تحذيرات فارغة",
            ConfidenceScore = 0.9,
            IsAbstention = false,
            RetrievedChunksUsed = 3,
            RetrievalSimilarityAvg = 0.80,
            Citations = new List<Citation>
            {
                new() { Document = "doc.pdf", Snippet = "text", SimilarityScore = 0.8 }
            },
            Warnings = null!,
            GenerationLatencyMs = 100,
            RetrievalLatencyMs = 50
        };
        SetupMediatorReturn(answer);

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.Warnings.Should().BeEmpty();
        vm.HasAnswer.Should().BeTrue();
    }

    // ══════════════════════════════════════
    //  Confidence Scoring
    // ══════════════════════════════════════

    [Fact]
    public async Task Confidence_High_GreenWithArabicLabel()
    {
        SetupMediatorReturn(CreateHighConfidenceAnswer(confidence: 0.92));

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.ConfidenceLabel.Should().Contain("High confidence");
        vm.ConfidenceColor.Should().Be("#2E7D32"); // Green
    }

    [Fact]
    public async Task Confidence_ExactlyPointEight_IsHigh()
    {
        SetupMediatorReturn(CreateHighConfidenceAnswer(confidence: 0.80));

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.ConfidenceLabel.Should().Contain("High confidence");
        vm.ConfidenceColor.Should().Be("#2E7D32");
    }

    [Fact]
    public async Task Confidence_Medium_OrangeWithArabicLabel()
    {
        SetupMediatorReturn(CreateMediumConfidenceAnswer());

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.ConfidenceLabel.Should().Contain("Medium confidence");
        vm.ConfidenceColor.Should().Be("#F57F17"); // Orange
    }

    [Fact]
    public async Task Confidence_ExactlyPointFive_IsMedium()
    {
        var answer = CreateHighConfidenceAnswer(confidence: 0.50);
        SetupMediatorReturn(answer);

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.ConfidenceLabel.Should().Contain("Medium confidence");
        vm.ConfidenceColor.Should().Be("#F57F17");
    }

    [Fact]
    public async Task Confidence_Low_RedWithArabicLabel()
    {
        SetupMediatorReturn(CreateLowConfidenceAnswer());

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.ConfidenceLabel.Should().Contain("Low confidence");
        vm.ConfidenceColor.Should().Be("#C62828"); // Red
    }

    [Fact]
    public async Task Confidence_Zero_IsLow()
    {
        var answer = CreateHighConfidenceAnswer(confidence: 0.0);
        SetupMediatorReturn(answer);

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.ConfidenceLabel.Should().Contain("Low confidence");
        vm.ConfidenceColor.Should().Be("#C62828");
    }

    // ══════════════════════════════════════
    //  Abstention Handling
    // ══════════════════════════════════════

    [Fact]
    public async Task Abstention_DisplaysSpecialLabeling()
    {
        SetupMediatorReturn(CreateAbstentionAnswer());

        var vm = await CreateVmAsync();
        vm.Question = "سؤال لا توجد له إجابة";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.IsAbstention.Should().BeTrue();
        vm.ConfidenceLabel.Should().Be("Abstained");
        vm.ConfidenceColor.Should().Be("#C62828"); // Red for abstention
        vm.HasAnswer.Should().BeTrue();
    }

    [Fact]
    public async Task Abstention_HistoryEntry_ShowsAbstentionMarker()
    {
        SetupMediatorReturn(CreateAbstentionAnswer());

        var vm = await CreateVmAsync();
        vm.Question = "سؤال لا توجد له إجابة";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.QueryHistory.Should().HaveCount(1);
        vm.QueryHistory[0].AnswerPreview.Should().Be("[Abstained]");
    }

    // ══════════════════════════════════════
    //  Exception Handling
    // ══════════════════════════════════════

    [Fact]
    public async Task AskQuestion_MediatorThrows_ShowsErrorSafely()
    {
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Pipeline failed"));

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.AnswerText.Should().Contain("Pipeline failed");
        vm.AnswerText.Should().Contain("error");
        vm.HasAnswer.Should().BeTrue();
        vm.ShowSafetyWarning.Should().BeTrue();
        vm.SafetyWarningText.Should().NotBeEmpty();
        vm.IsProcessing.Should().BeFalse("finally block resets processing state");
    }

    [Fact]
    public async Task AskQuestion_MediatorThrows_ResetsProcessingState()
    {
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Timeout"));

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.IsProcessing.Should().BeFalse();
        vm.ProcessingStatus.Should().BeEmpty();
    }

    // ══════════════════════════════════════
    //  Safety Validation — Post-Generation
    // ══════════════════════════════════════

    [Fact]
    public async Task SafetyValidation_CleanAnswer_NoWarning()
    {
        // High confidence with citations → passes all FailClosedGuard rules
        var answer = CreateHighConfidenceAnswer(confidence: 0.92);
        SetupMediatorReturn(answer);

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.ShowSafetyWarning.Should().BeFalse();
        vm.ValidationIssues.Should().BeEmpty();
    }

    [Fact]
    public async Task SafetyValidation_NoCitations_CriticalWarning()
    {
        // Answer with NO citations → FailClosedGuard requires at least MinimumCitationCount
        var answer = new LegalAnswer
        {
            Answer = "إجابة بدون استشهادات",
            ConfidenceScore = 0.9,
            IsAbstention = false,
            RetrievedChunksUsed = 0,
            RetrievalSimilarityAvg = 0.0,
            Citations = new List<Citation>(), // Empty!
            Warnings = new List<string>(),
            GenerationLatencyMs = 100,
            RetrievalLatencyMs = 50
        };
        SetupMediatorReturn(answer);

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.ShowSafetyWarning.Should().BeTrue();
        vm.SafetyWarningColor.Should().Be("#C62828", "critical = red");
        vm.SafetyWarningText.Should().Contain("⛔");
        vm.ValidationIssues.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SafetyValidation_VeryLowConfidence_CriticalWarning()
    {
        // Below MinimumConfidenceThreshold (0.40) → Critical
        var answer = CreateHighConfidenceAnswer(confidence: 0.20);
        SetupMediatorReturn(answer);

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.ShowSafetyWarning.Should().BeTrue();
        vm.SafetyWarningColor.Should().Be("#C62828");
        vm.ValidationIssues.Should().Contain(i => i.Contains("critically low"));
    }

    [Fact]
    public async Task SafetyValidation_ModeratelyLowConfidence_WarningLevel()
    {
        // Between 0.40 and 0.60 → Warning severity
        var answer = CreateHighConfidenceAnswer(confidence: 0.50);
        SetupMediatorReturn(answer);

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.ShowSafetyWarning.Should().BeTrue();
        vm.SafetyWarningColor.Should().Be("#F57F17", "warning = orange");
        vm.SafetyWarningText.Should().Contain("⚠");
    }

    [Fact]
    public async Task SafetyValidation_FabricationIndicator_CriticalWarning()
    {
        // Answer contains "بناءً على معرفتي" → fabrication detected
        var answer = CreateHighConfidenceAnswer(
            text: "بناءً على معرفتي العامة، يمكنني القول أن...");
        SetupMediatorReturn(answer);

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.ShowSafetyWarning.Should().BeTrue();
        vm.SafetyWarningColor.Should().Be("#C62828");
        vm.ValidationIssues.Should().Contain(i => i.Contains("external knowledge"));
    }

    [Fact]
    public async Task SafetyValidation_Abstention_AlwaysSafe()
    {
        // Abstention is always safe — FailClosedGuard returns Safe immediately
        SetupMediatorReturn(CreateAbstentionAnswer());

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.ShowSafetyWarning.Should().BeFalse("abstention is always safe");
        vm.ValidationIssues.Should().BeEmpty();
    }

    [Fact]
    public async Task SafetyValidation_UnreferencedArticle_Warning()
    {
        // Answer mentions "المادة 99" but citation snippets don't contain "99"
        var answer = new LegalAnswer
        {
            Answer = "وفقاً للمادة 99 من القانون...",
            ConfidenceScore = 0.85,
            IsAbstention = false,
            RetrievedChunksUsed = 3,
            RetrievalSimilarityAvg = 0.80,
            Citations = new List<Citation>
            {
                new()
                {
                    Document = "law.pdf",
                    Page = 1,
                    Snippet = "المادة 42 من القانون تنص على...",
                    SimilarityScore = 0.8
                }
            },
            Warnings = new List<string>(),
            GenerationLatencyMs = 100,
            RetrievalLatencyMs = 50
        };
        SetupMediatorReturn(answer);

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.ShowSafetyWarning.Should().BeTrue();
        vm.ValidationIssues.Should().Contain(i => i.Contains("99"));
    }

    // ══════════════════════════════════════
    //  Query History
    // ══════════════════════════════════════

    [Fact]
    public async Task History_NormalAnswer_AddsEntryAtTop()
    {
        SetupMediatorReturn(CreateHighConfidenceAnswer());

        var vm = await CreateVmAsync();
        vm.Question = "ما هي المادة 42؟";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.QueryHistory.Should().HaveCount(1);
        var entry = vm.QueryHistory[0];
        entry.Question.Should().Be("ما هي المادة 42؟");
        entry.Confidence.Should().Be(0.92);
        entry.CitationCount.Should().Be(2);
        entry.Timestamp.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task History_LongAnswer_TruncatesPreview()
    {
        var longText = new string('أ', 200);
        SetupMediatorReturn(CreateHighConfidenceAnswer(text: longText));

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.QueryHistory[0].AnswerPreview.Should().HaveLength(103);
        vm.QueryHistory[0].AnswerPreview.Should().EndWith("...");
    }

    [Fact]
    public async Task History_ShortAnswer_NoTruncation()
    {
        SetupMediatorReturn(CreateHighConfidenceAnswer(text: "إجابة قصيرة"));

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.QueryHistory[0].AnswerPreview.Should().Be("إجابة قصيرة");
    }

    [Fact]
    public async Task History_MultipleQueries_InsertedInReverseChronological()
    {
        var answer1 = CreateHighConfidenceAnswer(text: "إجابة أولى");
        var answer2 = CreateHighConfidenceAnswer(text: "إجابة ثانية");

        var vm = await CreateVmAsync();

        SetupMediatorReturn(answer1);
        vm.Question = "سؤال 1";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        SetupMediatorReturn(answer2);
        vm.Question = "سؤال 2";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        vm.QueryHistory.Should().HaveCount(2);
        vm.QueryHistory[0].Question.Should().Be("سؤال 2", "most recent at top");
        vm.QueryHistory[1].Question.Should().Be("سؤال 1");
    }

    // ══════════════════════════════════════
    //  ClearAnswer Command
    // ══════════════════════════════════════

    [Fact]
    public async Task ClearAnswer_ResetsAllState()
    {
        SetupMediatorReturn(CreateHighConfidenceAnswer());

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        // Verify we have state to clear
        vm.HasAnswer.Should().BeTrue();
        vm.AnswerText.Should().NotBeEmpty();
        vm.Citations.Should().NotBeEmpty();

        vm.ClearAnswerCommand.Execute(null);

        vm.Question.Should().BeEmpty();
        vm.AnswerText.Should().BeEmpty();
        vm.HasAnswer.Should().BeFalse();
        vm.Citations.Should().BeEmpty();
        vm.Warnings.Should().BeEmpty();
        vm.ValidationIssues.Should().BeEmpty();
        vm.ShowSafetyWarning.Should().BeFalse();
        vm.IsAbstention.Should().BeFalse();
    }

    // ══════════════════════════════════════
    //  LoadFromHistory Command
    // ══════════════════════════════════════

    [Fact]
    public async Task LoadFromHistory_SetsQuestion()
    {
        var vm = await CreateVmAsync();

        var historyItem = new QueryHistoryItem
        {
            Question = "ما هي عقوبة الاختلاس؟",
            AnswerPreview = "preview",
            Confidence = 0.9,
            Timestamp = DateTime.Now,
            CitationCount = 3
        };

        vm.LoadFromHistoryCommand.Execute(historyItem);

        vm.Question.Should().Be("ما هي عقوبة الاختلاس؟");
    }

    [Fact]
    public async Task LoadFromHistory_Null_DoesNothing()
    {
        var vm = await CreateVmAsync();
        vm.Question = "سؤال حالي";

        vm.LoadFromHistoryCommand.Execute(null);

        vm.Question.Should().Be("سؤال حالي", "null item should not change question");
    }

    // ══════════════════════════════════════
    //  Supporting Types
    // ══════════════════════════════════════

    [Fact]
    public void CitationItem_DefaultValues()
    {
        var item = new CitationItem();
        item.FileName.Should().BeEmpty();
        item.PageNumber.Should().Be(0);
        item.Content.Should().BeEmpty();
        item.SimilarityScore.Should().Be(0);
    }

    [Fact]
    public void QueryHistoryItem_DefaultValues()
    {
        var item = new QueryHistoryItem();
        item.Question.Should().BeEmpty();
        item.AnswerPreview.Should().BeEmpty();
        item.Confidence.Should().Be(0);
        item.CitationCount.Should().Be(0);
    }

    // ══════════════════════════════════════
    //  Dispatcher Integration
    // ══════════════════════════════════════

    [Fact]
    public async Task AskQuestion_UsesDispatcherForUIUpdates()
    {
        SetupMediatorReturn(CreateHighConfidenceAnswer());

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        _dispatcher.Verify(d => d.InvokeAsync(It.IsAny<Action>()), Times.AtLeastOnce,
            "DisplayAnswer + ValidateAnswerSafety run on UI thread via dispatcher");
    }

    // ══════════════════════════════════════
    //  Processing State Guard
    // ══════════════════════════════════════

    [Fact]
    public async Task AskQuestion_WhileProcessing_IsRejected()
    {
        // Use a TaskCompletionSource to hold the MediatR call
        var tcs = new TaskCompletionSource<LegalAnswer>();
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        var vm = await CreateVmAsync();
        vm.Question = "سؤال";

        // Start first query (will hang on tcs)
        var firstQuery = vm.AskQuestionCommand.ExecuteAsync(null);

        // Second query while first is processing (IsProcessing = true)
        vm.Question = "سؤال ثاني";
        await vm.AskQuestionCommand.ExecuteAsync(null);

        // Only one MediatR call should have been made
        _mediator.Verify(m => m.Send(
            It.IsAny<AskLegalQuestionQuery>(),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // Release the first query
        tcs.SetResult(CreateHighConfidenceAnswer());
        await firstQuery;
    }
}
