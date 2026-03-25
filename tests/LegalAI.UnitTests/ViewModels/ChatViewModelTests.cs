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
/// Tests for <see cref="ChatViewModel"/>. Verifies conversational EC-RAG
/// pipeline, fail-closed gating, message handling, safety validation,
/// and citation display in the chat bubble UI.
/// </summary>
public sealed class ChatViewModelTests : IDisposable
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<ILlmService> _llm = new();
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<IDispatcherService> _dispatcher = new();
    private readonly Mock<ILogger<ChatViewModel>> _logger = new();
    private readonly Mock<ModelIntegrityService> _modelIntegrity;
    private readonly FailClosedGuard _guard;

    public ChatViewModelTests()
    {
        _modelIntegrity = CreateMockModelIntegrity();

        _llm.Setup(l => l.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VectorStoreHealth { IsHealthy = true, VectorCount = 100 });

        _guard = new FailClosedGuard(
            _llm.Object, _vectorStore.Object,
            _modelIntegrity.Object, Mock.Of<ILogger<FailClosedGuard>>());

        _dispatcher.Setup(d => d.InvokeAsync(It.IsAny<Action>()))
            .Callback<Action>(a => a())
            .Returns(Task.CompletedTask);
        _dispatcher.Setup(d => d.Invoke(It.IsAny<Action>()))
            .Callback<Action>(a => a());
    }

    public void Dispose() => _guard.Dispose();

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
                VectorDbPath = Path.Combine(Path.GetTempPath(), "chat-test.db"),
                HnswIndexPath = Path.Combine(Path.GetTempPath(), "chat-test.hnsw"),
                DocumentDbPath = Path.Combine(Path.GetTempPath(), "chat-docs.db"),
                AuditDbPath = Path.Combine(Path.GetTempPath(), "chat-audit.db"),
                WatchDirectory = Path.GetTempPath()
            },
            Mock.Of<ILogger<ModelIntegrityService>>());

        mock.SetupGet(m => m.LlmModelExists).Returns(llmExists);
        mock.SetupGet(m => m.LlmModelValid).Returns(llmValid);
        mock.SetupGet(m => m.EmbeddingModelExists).Returns(embExists);
        mock.SetupGet(m => m.EmbeddingModelValid).Returns(embValid);

        return mock;
    }

    private async Task<ChatViewModel> CreateVmAsync()
    {
        await _guard.InitializeAsync();

        var vm = new ChatViewModel(
            _mediator.Object, _llm.Object, _guard,
            _dispatcher.Object, _logger.Object);

        await Task.Delay(200);
        return vm;
    }

    private static LegalAnswer CreateAnswer(
        string text = "الإجابة القانونية بناءً على المادة 42.",
        double confidence = 0.92,
        bool isAbstention = false,
        List<Citation>? citations = null,
        List<string>? warnings = null)
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
                    Document = "قانون.pdf",
                    Page = 42,
                    Snippet = "المادة 42: يعاقب كل من...",
                    SimilarityScore = 0.95
                }
            },
            Warnings = warnings ?? new List<string>(),
            GenerationLatencyMs = 450,
            RetrievalLatencyMs = 120
        };
    }

    private void SetupMediatorReturn(LegalAnswer answer)
    {
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(answer);
    }

    // ══════════════════════════════════════
    //  Initial State
    // ══════════════════════════════════════

    [Fact]
    public async Task Constructor_AddsWelcomeSystemMessage()
    {
        var vm = await CreateVmAsync();

        vm.Messages.Should().HaveCount(1);
        vm.Messages[0].Role.Should().Be(ChatRole.System);
        vm.Messages[0].Text.Should().Contain("مرحباً");
    }

    [Fact]
    public async Task InitialState_DefaultValues()
    {
        var vm = await CreateVmAsync();

        vm.InputText.Should().BeEmpty();
        vm.IsProcessing.Should().BeFalse();
        vm.IsLibraryOnlyMode.Should().BeFalse();
        vm.LlmAvailable.Should().BeTrue();
        vm.TopK.Should().Be(10);
    }

    // ══════════════════════════════════════
    //  Send Message — Normal Flow
    // ══════════════════════════════════════

    [Fact]
    public async Task SendMessage_AddsUserAndAssistantMessages()
    {
        SetupMediatorReturn(CreateAnswer());

        var vm = await CreateVmAsync();
        vm.InputText = "ما هي المادة 42؟";
        await vm.SendMessageCommand.ExecuteAsync(null);

        // Welcome + User + Assistant = 3
        vm.Messages.Should().HaveCount(3);
        vm.Messages[1].Role.Should().Be(ChatRole.User);
        vm.Messages[1].Text.Should().Be("ما هي المادة 42؟");
        vm.Messages[2].Role.Should().Be(ChatRole.Assistant);
        vm.Messages[2].Text.Should().Contain("المادة 42");
    }

    [Fact]
    public async Task SendMessage_ClearsInputText()
    {
        SetupMediatorReturn(CreateAnswer());

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        vm.InputText.Should().BeEmpty();
    }

    [Fact]
    public async Task SendMessage_SetsConfidenceOnAssistantMessage()
    {
        SetupMediatorReturn(CreateAnswer(confidence: 0.88));

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        var assistant = vm.Messages[2];
        assistant.ConfidenceScore.Should().Be(0.88);
        assistant.ConfidenceLabel.Should().Contain("ثقة عالية");
        assistant.ConfidenceColor.Should().Be("#2E7D32");
    }

    [Fact]
    public async Task SendMessage_PopulatesCitationsOnMessage()
    {
        SetupMediatorReturn(CreateAnswer());

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        var assistant = vm.Messages[2];
        assistant.Citations.Should().HaveCount(1);
        assistant.Citations[0].FileName.Should().Be("قانون.pdf");
        assistant.Citations[0].PageNumber.Should().Be(42);
        assistant.HasCitations.Should().BeTrue();
    }

    [Fact]
    public async Task SendMessage_CalculatesLatency()
    {
        SetupMediatorReturn(CreateAnswer());

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        vm.Messages[2].LatencyMs.Should().Be(570); // 450 + 120
    }

    [Fact]
    public async Task SendMessage_StrictModeAlwaysTrue()
    {
        SetupMediatorReturn(CreateAnswer());

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        _mediator.Verify(m => m.Send(
            It.Is<AskLegalQuestionQuery>(q => q.StrictMode == true),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SendMessage_PassesCaseNamespaceAndTopK()
    {
        SetupMediatorReturn(CreateAnswer());

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        vm.CaseNamespace = "criminal";
        vm.TopK = 5;
        await vm.SendMessageCommand.ExecuteAsync(null);

        _mediator.Verify(m => m.Send(
            It.Is<AskLegalQuestionQuery>(q =>
                q.CaseNamespace == "criminal" && q.TopK == 5),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SendMessage_WhitespaceCaseNamespace_SentAsNull()
    {
        SetupMediatorReturn(CreateAnswer());

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        vm.CaseNamespace = "   ";
        await vm.SendMessageCommand.ExecuteAsync(null);

        _mediator.Verify(m => m.Send(
            It.Is<AskLegalQuestionQuery>(q => q.CaseNamespace == null),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ══════════════════════════════════════
    //  Empty / Whitespace Rejected
    // ══════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task SendMessage_EmptyInput_DoesNotSend(string? input)
    {
        var vm = await CreateVmAsync();
        vm.InputText = input ?? "";
        await vm.SendMessageCommand.ExecuteAsync(null);

        // Only welcome message remains
        vm.Messages.Should().HaveCount(1);
        _mediator.Verify(m => m.Send(
            It.IsAny<AskLegalQuestionQuery>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ══════════════════════════════════════
    //  Confidence Tiers
    // ══════════════════════════════════════

    [Fact]
    public async Task Confidence_High_GreenLabel()
    {
        SetupMediatorReturn(CreateAnswer(confidence: 0.80));
        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        vm.Messages[2].ConfidenceLabel.Should().Contain("ثقة عالية");
        vm.Messages[2].ConfidenceColor.Should().Be("#2E7D32");
    }

    [Fact]
    public async Task Confidence_Medium_OrangeLabel()
    {
        SetupMediatorReturn(CreateAnswer(confidence: 0.65));
        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        vm.Messages[2].ConfidenceLabel.Should().Contain("ثقة متوسطة");
        vm.Messages[2].ConfidenceColor.Should().Be("#F57F17");
    }

    [Fact]
    public async Task Confidence_Low_RedLabel()
    {
        SetupMediatorReturn(CreateAnswer(confidence: 0.30));
        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        vm.Messages[2].ConfidenceLabel.Should().Contain("ثقة منخفضة");
        vm.Messages[2].ConfidenceColor.Should().Be("#C62828");
    }

    // ══════════════════════════════════════
    //  Abstention
    // ══════════════════════════════════════

    [Fact]
    public async Task Abstention_DisplaysSpecialMessage()
    {
        SetupMediatorReturn(CreateAnswer(text: "", confidence: 0.0, isAbstention: true));

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال لا توجد له إجابة";
        await vm.SendMessageCommand.ExecuteAsync(null);

        var assistant = vm.Messages[2];
        assistant.IsAbstention.Should().BeTrue();
        assistant.ConfidenceLabel.Should().Be("امتناع عن الإجابة");
        assistant.ConfidenceColor.Should().Be("#C62828");
        assistant.Text.Should().Contain("أدلة كافية");
    }

    // ══════════════════════════════════════
    //  Library-Only Mode (Fail-Closed)
    // ══════════════════════════════════════

    [Fact]
    public async Task SendMessage_LibraryOnlyMode_BlocksWithSystemMessage()
    {
        var vm = await CreateVmAsync();
        vm.SetLibraryOnlyMode(true, new[] { "Model missing" });

        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        // Welcome + User + System error = 3
        vm.Messages.Should().HaveCount(3);
        vm.Messages[2].Role.Should().Be(ChatRole.System);
        vm.Messages[2].Text.Should().Contain("⛔");
        vm.Messages[2].IsError.Should().BeTrue();

        _mediator.Verify(m => m.Send(
            It.IsAny<AskLegalQuestionQuery>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SendMessage_GuardBlocked_BlocksWithSystemMessage()
    {
        _modelIntegrity.SetupGet(m => m.LlmModelExists).Returns(false);

        using var blockedGuard = new FailClosedGuard(
            _llm.Object, _vectorStore.Object,
            _modelIntegrity.Object, Mock.Of<ILogger<FailClosedGuard>>());
        await blockedGuard.InitializeAsync();

        var vm = new ChatViewModel(
            _mediator.Object, _llm.Object, blockedGuard,
            _dispatcher.Object, _logger.Object);
        await Task.Delay(200);

        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        // Welcome + User + System error = 3
        vm.Messages.Should().HaveCount(3);
        vm.Messages[2].IsError.Should().BeTrue();
        vm.Messages[2].Text.Should().Contain("⛔");
    }

    [Fact]
    public async Task SetLibraryOnlyMode_True_DisablesLlm()
    {
        var vm = await CreateVmAsync();
        vm.LlmAvailable.Should().BeTrue();

        vm.SetLibraryOnlyMode(true, new[] { "Blocked" });

        vm.IsLibraryOnlyMode.Should().BeTrue();
        vm.LlmAvailable.Should().BeFalse();
        vm.LibraryOnlyReason.Should().Contain("Blocked");
    }

    [Fact]
    public async Task SetLibraryOnlyMode_False_RestoresLlm()
    {
        var vm = await CreateVmAsync();
        vm.SetLibraryOnlyMode(true, new[] { "Blocked" });
        vm.SetLibraryOnlyMode(false, Array.Empty<string>());
        await Task.Delay(200);

        vm.IsLibraryOnlyMode.Should().BeFalse();
        vm.LibraryOnlyReason.Should().BeEmpty();
        vm.LlmAvailable.Should().BeTrue();
    }

    // ══════════════════════════════════════
    //  Exception Handling
    // ══════════════════════════════════════

    [Fact]
    public async Task SendMessage_MediatorThrows_ShowsErrorInAssistantMessage()
    {
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Pipeline failure"));

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        var assistant = vm.Messages[2];
        assistant.Text.Should().Contain("Pipeline failure");
        assistant.Text.Should().Contain("خطأ");
        assistant.IsError.Should().BeTrue();
        assistant.IsStreaming.Should().BeFalse();
        vm.IsProcessing.Should().BeFalse();
    }

    [Fact]
    public async Task SendMessage_MediatorThrows_ResetsProcessingState()
    {
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Timeout"));

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        vm.IsProcessing.Should().BeFalse();
        vm.ProcessingStatus.Should().BeEmpty();
    }

    // ══════════════════════════════════════
    //  Safety Validation
    // ══════════════════════════════════════

    [Fact]
    public async Task SafetyValidation_CleanAnswer_NoWarning()
    {
        SetupMediatorReturn(CreateAnswer(confidence: 0.92));

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        vm.Messages[2].HasSafetyWarning.Should().BeFalse();
        vm.Messages[2].ValidationIssues.Should().BeEmpty();
    }

    [Fact]
    public async Task SafetyValidation_NoCitations_CriticalWarning()
    {
        SetupMediatorReturn(CreateAnswer(
            confidence: 0.9,
            citations: new List<Citation>()));

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        vm.Messages[2].HasSafetyWarning.Should().BeTrue();
        vm.Messages[2].SafetyWarningText.Should().Contain("⛔");
        vm.Messages[2].ValidationIssues.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SafetyValidation_FabricationIndicator_CriticalWarning()
    {
        SetupMediatorReturn(CreateAnswer(
            text: "بناءً على معرفتي العامة، الإجابة هي..."));

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        vm.Messages[2].HasSafetyWarning.Should().BeTrue();
        vm.Messages[2].SafetyWarningText.Should().Contain("⛔");
    }

    [Fact]
    public async Task SafetyValidation_Abstention_Safe()
    {
        SetupMediatorReturn(CreateAnswer(
            text: "", confidence: 0.0, isAbstention: true,
            citations: new List<Citation>()));

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        vm.Messages[2].HasSafetyWarning.Should().BeFalse();
    }

    // ══════════════════════════════════════
    //  Warnings from Answer
    // ══════════════════════════════════════

    [Fact]
    public async Task SendMessage_AnswerWarnings_AddedToMessage()
    {
        SetupMediatorReturn(CreateAnswer(
            warnings: new List<string> { "تحذير أول", "تحذير ثاني" }));

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        vm.Messages[2].Warnings.Should().HaveCount(2);
        vm.Messages[2].Warnings[0].Should().Be("تحذير أول");
    }

    // ══════════════════════════════════════
    //  ClearChat Command
    // ══════════════════════════════════════

    [Fact]
    public async Task ClearChat_RemovesAllAndAddsNewSystemMessage()
    {
        SetupMediatorReturn(CreateAnswer());

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);
        vm.Messages.Should().HaveCount(3);

        vm.ClearChatCommand.Execute(null);

        vm.Messages.Should().HaveCount(1);
        vm.Messages[0].Role.Should().Be(ChatRole.System);
        vm.Messages[0].Text.Should().Contain("مسح");
    }

    // ══════════════════════════════════════
    //  ToggleCitations Command
    // ══════════════════════════════════════

    [Fact]
    public async Task ToggleCitations_TogglesVisibility()
    {
        SetupMediatorReturn(CreateAnswer());

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        var msg = vm.Messages[2];
        msg.ShowCitations.Should().BeFalse();

        vm.ToggleCitationsCommand.Execute(msg);
        msg.ShowCitations.Should().BeTrue();

        vm.ToggleCitationsCommand.Execute(msg);
        msg.ShowCitations.Should().BeFalse();
    }

    [Fact]
    public async Task ToggleCitations_Null_DoesNotThrow()
    {
        var vm = await CreateVmAsync();
        var act = () => vm.ToggleCitationsCommand.Execute(null);
        act.Should().NotThrow();
    }

    // ══════════════════════════════════════
    //  CopyMessage Command
    // ══════════════════════════════════════

    [Fact]
    public async Task CopyMessage_Null_DoesNotThrow()
    {
        var vm = await CreateVmAsync();
        // CopyMessage(null) should return without error
        var act = () => vm.CopyMessageCommand.Execute(null);
        act.Should().NotThrow();
    }

    // ══════════════════════════════════════
    //  Processing Guard
    // ══════════════════════════════════════

    [Fact]
    public async Task SendMessage_WhileProcessing_IsRejected()
    {
        var tcs = new TaskCompletionSource<LegalAnswer>();
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال 1";

        var firstQuery = vm.SendMessageCommand.ExecuteAsync(null);

        // Second query while first is processing
        vm.InputText = "سؤال 2";
        await vm.SendMessageCommand.ExecuteAsync(null);

        _mediator.Verify(m => m.Send(
            It.IsAny<AskLegalQuestionQuery>(),
            It.IsAny<CancellationToken>()),
            Times.Once);

        tcs.SetResult(CreateAnswer());
        await firstQuery;
    }

    // ══════════════════════════════════════
    //  Dispatcher Integration
    // ══════════════════════════════════════

    [Fact]
    public async Task SendMessage_UsesDispatcherForUIUpdates()
    {
        SetupMediatorReturn(CreateAnswer());

        var vm = await CreateVmAsync();
        vm.InputText = "سؤال";
        await vm.SendMessageCommand.ExecuteAsync(null);

        _dispatcher.Verify(d => d.InvokeAsync(It.IsAny<Action>()), Times.AtLeastOnce);
    }

    // ══════════════════════════════════════
    //  ChatMessage Model
    // ══════════════════════════════════════

    [Fact]
    public void ChatMessage_DefaultValues()
    {
        var msg = new ChatMessage();
        msg.Text.Should().BeEmpty();
        msg.IsStreaming.Should().BeFalse();
        msg.IsError.Should().BeFalse();
        msg.IsAbstention.Should().BeFalse();
        msg.ConfidenceScore.Should().Be(0);
        msg.ConfidenceLabel.Should().BeEmpty();
        msg.ConfidenceColor.Should().Be("#888888");
        msg.HasCitations.Should().BeFalse();
        msg.HasSafetyWarning.Should().BeFalse();
        msg.SafetyWarningText.Should().BeEmpty();
    }

    [Fact]
    public void ChatMessage_IsCompleteAssistant_Conditions()
    {
        var msg = new ChatMessage { Role = ChatRole.Assistant, Text = "test" };
        msg.IsCompleteAssistant.Should().BeTrue();

        msg.IsStreaming = true;
        msg.IsCompleteAssistant.Should().BeFalse("streaming assistant is not complete");

        msg.IsStreaming = false;
        msg.Text = "";
        msg.IsCompleteAssistant.Should().BeFalse("empty text is not complete");
    }

    [Fact]
    public void ChatMessage_HasCitations_ReflectsCollection()
    {
        var msg = new ChatMessage();
        msg.HasCitations.Should().BeFalse();

        msg.Citations.Add(new CitationItem
        {
            FileName = "test.pdf",
            PageNumber = 1,
            Content = "text",
            SimilarityScore = 0.9
        });
        msg.HasCitations.Should().BeTrue();
    }

    // ══════════════════════════════════════
    //  Multi-turn Conversation
    // ══════════════════════════════════════

    [Fact]
    public async Task MultiTurn_AccumulatesMessages()
    {
        var vm = await CreateVmAsync();

        SetupMediatorReturn(CreateAnswer(text: "إجابة أولى"));
        vm.InputText = "سؤال 1";
        await vm.SendMessageCommand.ExecuteAsync(null);

        SetupMediatorReturn(CreateAnswer(text: "إجابة ثانية"));
        vm.InputText = "سؤال 2";
        await vm.SendMessageCommand.ExecuteAsync(null);

        // Welcome + (User1 + Assistant1) + (User2 + Assistant2) = 5
        vm.Messages.Should().HaveCount(5);
        vm.Messages[1].Role.Should().Be(ChatRole.User);
        vm.Messages[1].Text.Should().Be("سؤال 1");
        vm.Messages[2].Role.Should().Be(ChatRole.Assistant);
        vm.Messages[2].Text.Should().Contain("إجابة أولى");
        vm.Messages[3].Role.Should().Be(ChatRole.User);
        vm.Messages[3].Text.Should().Be("سؤال 2");
        vm.Messages[4].Role.Should().Be(ChatRole.Assistant);
        vm.Messages[4].Text.Should().Contain("إجابة ثانية");
    }
}
