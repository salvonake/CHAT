using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Poseidon.Application.Queries;
using Poseidon.Desktop.Services;
using Poseidon.Domain.Interfaces;
using Poseidon.Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Poseidon.Desktop.ViewModels;

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
             Text = "Welcome to Poseidon.\n" +
                 "Ask your legal question and I will search indexed documents to provide an evidence-constrained answer with citations.\n\n" +
                 "⚖ All answers are based only on indexed documents - no external knowledge is used.",
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
                Text = "The system is in recovery mode. Answer generation is currently disabled.\n" +
                       "Open Diagnostics with Ctrl+D.",
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
            ProcessingStatus = "Searching indexed documents...";

            var query = new AskLegalQuestionQuery
            {
                Question = text,
                CaseNamespace = string.IsNullOrWhiteSpace(CaseNamespace) ? null : CaseNamespace,
                StrictMode = true,
                TopK = TopK
            };

            ProcessingStatus = "Generating answer...";
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
                assistantMsg.Text = $"An error occurred while processing the question:\n{ex.Message}";
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
            msg.ConfidenceLabel = $"High confidence ({answer.ConfidenceScore:P0})";
            msg.ConfidenceColor = "#2E7D32";
        }
        else if (answer.ConfidenceScore >= 0.5)
        {
            msg.ConfidenceLabel = $"Medium confidence ({answer.ConfidenceScore:P0})";
            msg.ConfidenceColor = "#F57F17";
        }
        else
        {
            msg.ConfidenceLabel = $"Low confidence ({answer.ConfidenceScore:P0})";
            msg.ConfidenceColor = "#C62828";
        }

        if (answer.IsAbstention)
        {
            msg.ConfidenceLabel = "Abstained";
            msg.ConfidenceColor = "#C62828";
            msg.Text = "Not enough evidence was found in indexed documents to answer this question.";
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
                ? "⛔ Critical safety warning. Do not rely on this answer without independent verification."
                : "⚠ Safety warnings present. Verify sources.";

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
            Text = "Chat was cleared. Ask a new question.",
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


