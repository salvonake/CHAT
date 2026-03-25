using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LegalAI.Application.Queries;
using LegalAI.Desktop.Services;
using LegalAI.Domain.Interfaces;
using LegalAI.Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;

namespace LegalAI.Desktop.ViewModels;

/// <summary>
/// ViewModel for the conversational chat interface. Provides a multi-turn
/// message-bubble UI that wraps the EC-RAG pipeline. Each message keeps
/// its own citations, confidence badge, and safety validation.
/// </summary>
public partial class ChatViewModel : ObservableObject
{
    private readonly IMediator _mediator;
    private readonly ILlmService _llm;
    private readonly FailClosedGuard _guard;
    private readonly IDispatcherService _dispatcher;
    private readonly ILogger<ChatViewModel> _logger;

    // ── Messages ──
    public ObservableCollection<ChatMessage> Messages { get; } = [];

    // ── Input ──
    [ObservableProperty]
    private string _inputText = "";

    [ObservableProperty]
    private string? _caseNamespace;

    [ObservableProperty]
    private int _topK = 10;

    // ── State ──
    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private string _processingStatus = "";

    // ── Library-Only Mode (fail-closed) ──
    [ObservableProperty]
    private bool _isLibraryOnlyMode;

    [ObservableProperty]
    private string _libraryOnlyReason = "";

    [ObservableProperty]
    private bool _llmAvailable = true;

    public ChatViewModel(
        IMediator mediator,
        ILlmService llm,
        FailClosedGuard guard,
        IDispatcherService dispatcher,
        ILogger<ChatViewModel> logger)
    {
        _mediator = mediator;
        _llm = llm;
        _guard = guard;
        _dispatcher = dispatcher;
        _logger = logger;

        // Add a welcome system message
        Messages.Add(new ChatMessage
        {
            Role = ChatRole.System,
            Text = "مرحباً بك في نظام الذكاء القانوني.\n" +
                   "اطرح سؤالك القانوني وسأبحث في الوثائق المفهرسة لتقديم إجابة مقيّدة بالأدلة مع الاستشهادات.\n\n" +
                   "⚖ جميع الإجابات مستندة فقط إلى الوثائق المفهرسة — لا يتم استخدام أي معرفة خارجية.",
            Timestamp = DateTime.Now
        });

        _ = CheckLlmAvailabilityAsync();
    }

    /// <summary>
    /// Called by MainViewModel when the fail-closed guard changes state.
    /// </summary>
    public void SetLibraryOnlyMode(bool isLibraryOnly, IReadOnlyList<string> reasons)
    {
        IsLibraryOnlyMode = isLibraryOnly;
        if (isLibraryOnly)
        {
            LibraryOnlyReason = string.Join("\n", reasons);
            LlmAvailable = false;
        }
        else
        {
            LibraryOnlyReason = "";
            _ = CheckLlmAvailabilityAsync();
        }
    }

    private async Task CheckLlmAvailabilityAsync()
    {
        try
        {
            if (IsLibraryOnlyMode) { LlmAvailable = false; return; }
            LlmAvailable = await _llm.IsAvailableAsync();
        }
        catch { LlmAvailable = false; }
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        var text = InputText?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        if (IsProcessing) return;

        // ── Add user message ──
        var userMsg = new ChatMessage
        {
            Role = ChatRole.User,
            Text = text,
            Timestamp = DateTime.Now
        };
        Messages.Add(userMsg);
        InputText = "";

        // ── Fail-closed gate ──
        if (IsLibraryOnlyMode || !_guard.CanAskQuestions)
        {
            Messages.Add(new ChatMessage
            {
                Role = ChatRole.System,
                Text = "⛔ النظام في وضع المكتبة فقط. لا يمكن توليد إجابات حالياً.\n" +
                       "تحقق من حالة النظام في لوحة الصحة.",
                Timestamp = DateTime.Now,
                IsError = true
            });
            return;
        }

        IsProcessing = true;

        // ── Add placeholder assistant message ──
        var assistantMsg = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Text = "",
            Timestamp = DateTime.Now,
            IsStreaming = true
        };
        Messages.Add(assistantMsg);

        try
        {
            ProcessingStatus = "جارٍ البحث في الوثائق...";

            var query = new AskLegalQuestionQuery
            {
                Question = text,
                CaseNamespace = string.IsNullOrWhiteSpace(CaseNamespace) ? null : CaseNamespace,
                StrictMode = true,
                TopK = TopK
            };

            ProcessingStatus = "جارٍ توليد الإجابة...";
            var answer = await _mediator.Send(query);

            await _dispatcher.InvokeAsync(() =>
            {
                PopulateAssistantMessage(assistantMsg, answer);
                ValidateMessageSafety(assistantMsg, answer);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat: Failed to process question");
            await _dispatcher.InvokeAsync(() =>
            {
                assistantMsg.Text = $"حدث خطأ أثناء معالجة السؤال:\n{ex.Message}";
                assistantMsg.IsError = true;
                assistantMsg.IsStreaming = false;
            });
        }
        finally
        {
            IsProcessing = false;
            ProcessingStatus = "";
        }
    }

    private void PopulateAssistantMessage(ChatMessage msg, LegalAnswer answer)
    {
        msg.IsStreaming = false;
        msg.Text = answer.Answer;
        msg.ConfidenceScore = answer.ConfidenceScore;
        msg.IsAbstention = answer.IsAbstention;
        msg.LatencyMs = answer.GenerationLatencyMs + answer.RetrievalLatencyMs;

        // Confidence
        if (answer.ConfidenceScore >= 0.8)
        {
            msg.ConfidenceLabel = $"ثقة عالية ({answer.ConfidenceScore:P0})";
            msg.ConfidenceColor = "#2E7D32";
        }
        else if (answer.ConfidenceScore >= 0.5)
        {
            msg.ConfidenceLabel = $"ثقة متوسطة ({answer.ConfidenceScore:P0})";
            msg.ConfidenceColor = "#F57F17";
        }
        else
        {
            msg.ConfidenceLabel = $"ثقة منخفضة ({answer.ConfidenceScore:P0})";
            msg.ConfidenceColor = "#C62828";
        }

        if (answer.IsAbstention)
        {
            msg.ConfidenceLabel = "امتناع عن الإجابة";
            msg.ConfidenceColor = "#C62828";
            msg.Text = "لم يتم العثور على أدلة كافية في الوثائق المفهرسة للإجابة على هذا السؤال.";
        }

        // Citations
        if (answer.Citations is { Count: > 0 })
        {
            foreach (var c in answer.Citations)
            {
                msg.Citations.Add(new CitationItem
                {
                    FileName = c.Document,
                    PageNumber = c.Page,
                    Content = c.Snippet,
                    SimilarityScore = c.SimilarityScore
                });
            }
        }

        // Warnings
        if (answer.Warnings != null)
        {
            foreach (var w in answer.Warnings)
                msg.Warnings.Add(w);
        }
    }

    private void ValidateMessageSafety(ChatMessage msg, LegalAnswer answer)
    {
        var validation = _guard.ValidateAnswer(
            answer.Answer, answer.Citations, answer.ConfidenceScore, answer.IsAbstention);

        if (validation.Issues.Count > 0)
        {
            msg.HasSafetyWarning = true;
            msg.SafetyWarningText = validation.Severity == AnswerSeverity.Critical
                ? "⛔ تحذير أمان حرج — لا تعتمد على هذه الإجابة دون تحقق مستقل."
                : "⚠ تحذيرات أمان — تحقق من المصادر.";

            foreach (var issue in validation.Issues)
                msg.ValidationIssues.Add(issue);
        }
    }

    [RelayCommand]
    private void ClearChat()
    {
        Messages.Clear();
        Messages.Add(new ChatMessage
        {
            Role = ChatRole.System,
            Text = "تم مسح المحادثة. اطرح سؤالاً جديداً.",
            Timestamp = DateTime.Now
        });
    }

    [RelayCommand]
    private void CopyMessage(ChatMessage? msg)
    {
        if (msg == null) return;
        try
        {
            System.Windows.Clipboard.SetText(msg.Text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to copy to clipboard");
        }
    }

    /// <summary>Toggle citation panel visibility on a message.</summary>
    [RelayCommand]
    private void ToggleCitations(ChatMessage? msg)
    {
        if (msg == null) return;
        msg.ShowCitations = !msg.ShowCitations;
    }
}

// ═══════════════════════════════════════
//  Chat message model
// ═══════════════════════════════════════

public enum ChatRole
{
    System,
    User,
    Assistant
}

/// <summary>
/// Single message in the chat conversation. Observable so the UI
/// can update as assistant responses stream in.
/// </summary>
public partial class ChatMessage : ObservableObject
{
    [ObservableProperty] private ChatRole _role;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private DateTime _timestamp;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isAbstention;

    // ── Confidence ──
    [ObservableProperty] private double _confidenceScore;
    [ObservableProperty] private string _confidenceLabel = "";
    [ObservableProperty] private string _confidenceColor = "#888888";
    [ObservableProperty] private double _latencyMs;

    // ── Citations ──
    public ObservableCollection<CitationItem> Citations { get; } = [];
    [ObservableProperty] private bool _showCitations;

    // ── Warnings ──
    public ObservableCollection<string> Warnings { get; } = [];

    // ── Safety ──
    [ObservableProperty] private bool _hasSafetyWarning;
    [ObservableProperty] private string _safetyWarningText = "";
    public ObservableCollection<string> ValidationIssues { get; } = [];

    /// <summary>Whether this message has citations to show.</summary>
    public bool HasCitations => Citations.Count > 0;

    /// <summary>Whether this is an assistant message with content (not streaming).</summary>
    public bool IsCompleteAssistant => Role == ChatRole.Assistant && !IsStreaming && !string.IsNullOrEmpty(Text);
}
