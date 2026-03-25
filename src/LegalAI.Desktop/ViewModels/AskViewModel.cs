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
/// ViewModel for the legal question-answering view. Handles EC-RAG pipeline
/// queries with citation display, confidence scoring, abstention handling,
/// and fail-closed safety validation.
/// </summary>
public partial class AskViewModel : ObservableObject
{
    private readonly IMediator _mediator;
    private readonly ILlmService _llm;
    private readonly FailClosedGuard _guard;
    private readonly IDispatcherService _dispatcher;
    private readonly ILogger<AskViewModel> _logger;

    // ── Input ──
    [ObservableProperty]
    private string _question = "";

    [ObservableProperty]
    private string? _caseNamespace;

    // StrictMode is ALWAYS true — fail-closed requirement
    // Users cannot disable strict mode in a life-critical system.
    [ObservableProperty]
    private bool _strictMode = true;

    [ObservableProperty]
    private int _topK = 10;

    // ── Output ──
    [ObservableProperty]
    private string _answerText = "";

    [ObservableProperty]
    private double _confidenceScore;

    [ObservableProperty]
    private string _confidenceLabel = "";

    [ObservableProperty]
    private string _confidenceColor = "#888888";

    [ObservableProperty]
    private bool _isAbstention;

    [ObservableProperty]
    private string _abstentionReason = "";

    [ObservableProperty]
    private ObservableCollection<CitationItem> _citations = [];

    [ObservableProperty]
    private ObservableCollection<string> _warnings = [];

    // ── Safety Validation ──
    [ObservableProperty]
    private bool _showSafetyWarning;

    [ObservableProperty]
    private string _safetyWarningText = "";

    [ObservableProperty]
    private string _safetyWarningColor = "#C62828";

    [ObservableProperty]
    private ObservableCollection<string> _validationIssues = [];

    // ── State ──
    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private bool _hasAnswer;

    [ObservableProperty]
    private bool _llmAvailable = true;

    [ObservableProperty]
    private string _processingStatus = "";

    [ObservableProperty]
    private double _latencyMs;

    // ── Library-Only Mode (fail-closed) ──
    [ObservableProperty]
    private bool _isLibraryOnlyMode;

    [ObservableProperty]
    private string _libraryOnlyReason = "";

    // ── History ──
    public ObservableCollection<QueryHistoryItem> QueryHistory { get; } = [];

    public AskViewModel(
        IMediator mediator,
        ILlmService llm,
        FailClosedGuard guard,
        IDispatcherService dispatcher,
        ILogger<AskViewModel> logger)
    {
        _mediator = mediator;
        _llm = llm;
        _guard = guard;
        _dispatcher = dispatcher;
        _logger = logger;

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
            if (IsLibraryOnlyMode)
            {
                LlmAvailable = false;
                return;
            }
            LlmAvailable = await _llm.IsAvailableAsync();
        }
        catch
        {
            LlmAvailable = false;
        }
    }

    [RelayCommand]
    private async Task AskQuestionAsync()
    {
        if (string.IsNullOrWhiteSpace(Question)) return;
        if (IsProcessing) return;

        // ── Fail-closed gate ──
        if (IsLibraryOnlyMode || !_guard.CanAskQuestions)
        {
            AnswerText = "⛔ النظام في وضع المكتبة فقط. لا يمكن توليد إجابات حالياً.\n" +
                         "System is in library-only mode. Cannot generate answers.";
            HasAnswer = true;
            ShowSafetyWarning = true;
            SafetyWarningText = "الاستعلام محظور — تحقق من حالة النظام في لوحة الصحة.";
            return;
        }

        IsProcessing = true;
        HasAnswer = false;
        ProcessingStatus = "جارٍ تحليل السؤال...";
        AnswerText = "";
        Citations.Clear();
        Warnings.Clear();
        ValidationIssues.Clear();
        ShowSafetyWarning = false;
        IsAbstention = false;

        try
        {
            ProcessingStatus = "جارٍ البحث في الوثائق...";

            var query = new AskLegalQuestionQuery
            {
                Question = Question,
                CaseNamespace = string.IsNullOrWhiteSpace(CaseNamespace) ? null : CaseNamespace,
                StrictMode = true,   // ALWAYS true — fail-closed
                TopK = TopK
            };

            ProcessingStatus = "جارٍ توليد الإجابة...";
            var answer = await _mediator.Send(query);

            await _dispatcher.InvokeAsync(() =>
            {
                DisplayAnswer(answer);
                ValidateAnswerSafety(answer);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process question: {Q}", Question);
            AnswerText = $"حدث خطأ أثناء معالجة السؤال:\n{ex.Message}";
            HasAnswer = true;
            ShowSafetyWarning = true;
            SafetyWarningText = "فشل في معالجة السؤال — لم يتم توليد إجابة.";
        }
        finally
        {
            IsProcessing = false;
            ProcessingStatus = "";
        }
    }

    private void DisplayAnswer(LegalAnswer answer)
    {
        AnswerText = answer.Answer;
        ConfidenceScore = answer.ConfidenceScore;
        IsAbstention = answer.IsAbstention;
        HasAnswer = true;

        // Confidence label and color
        if (answer.ConfidenceScore >= 0.8)
        {
            ConfidenceLabel = $"ثقة عالية ({answer.ConfidenceScore:P0})";
            ConfidenceColor = "#2E7D32"; // Green
        }
        else if (answer.ConfidenceScore >= 0.5)
        {
            ConfidenceLabel = $"ثقة متوسطة ({answer.ConfidenceScore:P0})";
            ConfidenceColor = "#F57F17"; // Yellow/Orange
        }
        else
        {
            ConfidenceLabel = $"ثقة منخفضة ({answer.ConfidenceScore:P0})";
            ConfidenceColor = "#C62828"; // Red
        }

        if (answer.IsAbstention)
        {
            AbstentionReason = "لم يتم العثور على أدلة كافية في الوثائق المفهرسة للإجابة على هذا السؤال.";
            ConfidenceLabel = "امتناع عن الإجابة";
            ConfidenceColor = "#C62828";
        }

        // Citations
        Citations.Clear();
        if (answer.Citations != null)
        {
            foreach (var citation in answer.Citations)
            {
                Citations.Add(new CitationItem
                {
                    FileName = citation.Document,
                    PageNumber = citation.Page,
                    Content = citation.Snippet,
                    SimilarityScore = citation.SimilarityScore
                });
            }
        }

        // Warnings
        Warnings.Clear();
        if (answer.Warnings != null)
        {
            foreach (var warning in answer.Warnings)
            {
                Warnings.Add(warning);
            }
        }

        LatencyMs = answer.GenerationLatencyMs + answer.RetrievalLatencyMs;

        // Add to history
        QueryHistory.Insert(0, new QueryHistoryItem
        {
            Question = Question,
            AnswerPreview = answer.IsAbstention
                ? "[امتناع]"
                : (answer.Answer.Length > 100 ? answer.Answer[..100] + "..." : answer.Answer),
            Confidence = answer.ConfidenceScore,
            Timestamp = DateTime.Now,
            CitationCount = answer.Citations?.Count ?? 0
        });
    }

    /// <summary>
    /// Post-generation safety validation. Runs the FailClosedGuard's answer
    /// validator to detect fabrication, missing citations, and low confidence.
    /// </summary>
    private void ValidateAnswerSafety(LegalAnswer answer)
    {
        var validation = _guard.ValidateAnswer(
            answer.Answer,
            answer.Citations,
            answer.ConfidenceScore,
            answer.IsAbstention);

        ValidationIssues.Clear();

        if (validation.Issues.Count > 0)
        {
            foreach (var issue in validation.Issues)
            {
                ValidationIssues.Add(issue);
            }

            ShowSafetyWarning = true;

            if (validation.Severity == AnswerSeverity.Critical)
            {
                SafetyWarningText = "⛔ تحذير أمان حرج — لا تعتمد على هذه الإجابة دون تحقق مستقل.";
                SafetyWarningColor = "#C62828"; // Red
            }
            else
            {
                SafetyWarningText = "⚠ تحذيرات أمان — تحقق من المصادر قبل الاعتماد على الإجابة.";
                SafetyWarningColor = "#F57F17"; // Orange
            }

            _logger.LogWarning("Answer safety validation: {Severity}, {Count} issues",
                validation.Severity, validation.Issues.Count);
        }
        else
        {
            ShowSafetyWarning = false;
        }
    }

    [RelayCommand]
    private void ClearAnswer()
    {
        Question = "";
        AnswerText = "";
        HasAnswer = false;
        Citations.Clear();
        Warnings.Clear();
        ValidationIssues.Clear();
        ShowSafetyWarning = false;
        IsAbstention = false;
    }

    [RelayCommand]
    private void LoadFromHistory(QueryHistoryItem? item)
    {
        if (item == null) return;
        Question = item.Question;
    }
}

// ── Supporting types ──

public sealed class CitationItem
{
    public string FileName { get; init; } = "";
    public int PageNumber { get; init; }
    public string Content { get; init; } = "";
    public double SimilarityScore { get; init; }
}

public sealed class QueryHistoryItem
{
    public string Question { get; init; } = "";
    public string AnswerPreview { get; init; } = "";
    public double Confidence { get; init; }
    public DateTime Timestamp { get; init; }
    public int CitationCount { get; init; }
}
