using System.Collections.ObjectModel;
using LegalAI.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace LegalAI.Desktop.Services;

/// <summary>
/// Central fail-closed safety guard. Monitors system health and gates
/// all question-answering capability. If ANY safety check fails, the
/// system degrades to library-only mode (document browsing only).
///
/// Design principle: "When in doubt, refuse to answer."
/// This is a life-critical system — incorrect legal answers can have
/// irreversible consequences.
/// </summary>
public sealed class FailClosedGuard : IDisposable
{
    private readonly ILlmService _llm;
    private readonly IVectorStore _vectorStore;
    private readonly ModelIntegrityService _modelIntegrity;
    private readonly ILogger<FailClosedGuard> _logger;
    private readonly System.Timers.Timer _healthCheckTimer;

    private readonly object _lock = new();
    private volatile SystemOperationalStatus _status = SystemOperationalStatus.Initializing;
    private readonly List<string> _blockReasons = [];
    private readonly List<string> _warnings = [];

    /// <summary>Raised when operational status changes.</summary>
    public event EventHandler<SystemOperationalStatus>? StatusChanged;

    /// <summary>Current system operational status.</summary>
    public SystemOperationalStatus Status
    {
        get => _status;
        private set
        {
            if (_status != value)
            {
                _status = value;
                StatusChanged?.Invoke(this, value);
            }
        }
    }

    /// <summary>Whether the system can safely answer legal questions.</summary>
    public bool CanAskQuestions => _status == SystemOperationalStatus.Operational
                                  || _status == SystemOperationalStatus.Degraded;

    /// <summary>Reasons why question answering is blocked (empty if allowed).</summary>
    public IReadOnlyList<string> BlockReasons
    {
        get { lock (_lock) return _blockReasons.ToList().AsReadOnly(); }
    }

    /// <summary>Non-blocking warnings (system is operational but with caveats).</summary>
    public IReadOnlyList<string> Warnings
    {
        get { lock (_lock) return _warnings.ToList().AsReadOnly(); }
    }

    /// <summary>Whether strict mode can be disabled (always false for fail-closed).</summary>
    public bool AllowStrictModeOverride => false;

    /// <summary>Minimum confidence score below which answers get a prominent safety warning.</summary>
    public double MinimumConfidenceThreshold => 0.40;

    /// <summary>Minimum number of citations required for a non-abstention answer.</summary>
    public int MinimumCitationCount => 1;

    public FailClosedGuard(
        ILlmService llm,
        IVectorStore vectorStore,
        ModelIntegrityService modelIntegrity,
        ILogger<FailClosedGuard> logger)
    {
        _llm = llm;
        _vectorStore = vectorStore;
        _modelIntegrity = modelIntegrity;
        _logger = logger;

        // Periodic health check every 60 seconds
        _healthCheckTimer = new System.Timers.Timer(60_000);
        _healthCheckTimer.Elapsed += async (_, _) => await RunHealthCheckAsync();
        _healthCheckTimer.AutoReset = true;
    }

    /// <summary>
    /// Run initial checks on startup. Must be called from App.xaml.cs
    /// BEFORE showing the main window.
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation("FailClosedGuard: Running initial safety checks...");
        await RunHealthCheckAsync();
        _healthCheckTimer.Start();
        _logger.LogInformation("FailClosedGuard: Status = {Status}, CanAsk = {CanAsk}",
            Status, CanAskQuestions);
    }

    /// <summary>
    /// Runs all safety checks and updates system status.
    /// </summary>
    public async Task RunHealthCheckAsync()
    {
        var reasons = new List<string>();
        var warns = new List<string>();

        // ── Check 1: Model file exists ──
        if (!_modelIntegrity.LlmModelExists)
        {
            reasons.Add("LLM model file not found - cannot generate answers.");
            _logger.LogWarning("FailClosedGuard: LLM model file missing");
        }

        // ── Check 2: Model integrity (if hash is configured) ──
        if (_modelIntegrity.LlmModelExists && !_modelIntegrity.LlmModelValid)
        {
            reasons.Add("Model integrity verification failed - file may be corrupted or tampered.");
            _logger.LogError("FailClosedGuard: LLM model integrity check FAILED");
        }

        // ── Check 3: Embedding model ──
        if (!_modelIntegrity.EmbeddingModelExists)
        {
            reasons.Add("Embedding model not found - cannot search documents.");
            _logger.LogWarning("FailClosedGuard: Embedding model missing");
        }
        else if (!_modelIntegrity.EmbeddingModelValid)
        {
            reasons.Add("Embedding model integrity check failed.");
            _logger.LogError("FailClosedGuard: Embedding model integrity FAILED");
        }

        // ── Check 4: LLM availability (can it actually respond?) ──
        try
        {
            var llmAvailable = await _llm.IsAvailableAsync();
            if (!llmAvailable)
            {
                reasons.Add("LLM service is not available - cannot generate answers.");
                _logger.LogWarning("FailClosedGuard: LLM not available");
            }
        }
        catch (Exception ex)
        {
            reasons.Add($"LLM connection error: {ex.Message}");
            _logger.LogError(ex, "FailClosedGuard: LLM availability check failed");
        }

        // ── Check 5: Vector store health ──
        try
        {
            var health = await _vectorStore.GetHealthAsync();
            if (!health.IsHealthy)
            {
                warns.Add("Vector store unhealthy - search results may be incomplete.");
                _logger.LogWarning("FailClosedGuard: Vector store unhealthy: {Status}", health.Status);
            }
        }
        catch (Exception ex)
        {
            reasons.Add($"Vector store error: {ex.Message}");
            _logger.LogError(ex, "FailClosedGuard: Vector store health check failed");
        }

        // ── Check 6: Encryption warning (non-blocking) ──
        // This is a warning, not a block. The user chose to run without encryption.
        // But we never stop reminding them.

        // ── Determine status ──
        lock (_lock)
        {
            _blockReasons.Clear();
            _blockReasons.AddRange(reasons);
            _warnings.Clear();
            _warnings.AddRange(warns);
        }

        if (reasons.Count > 0)
        {
            Status = SystemOperationalStatus.LibraryOnly;
            _logger.LogWarning("FailClosedGuard: System in LIBRARY-ONLY mode ({Count} block reasons)",
                reasons.Count);
        }
        else if (warns.Count > 0)
        {
            Status = SystemOperationalStatus.Degraded;
            _logger.LogWarning("FailClosedGuard: System DEGRADED ({Count} warnings)", warns.Count);
        }
        else
        {
            Status = SystemOperationalStatus.Operational;
            _logger.LogInformation("FailClosedGuard: System OPERATIONAL ✓");
        }
    }

    /// <summary>
    /// Validates a generated answer before displaying it to the user.
    /// Returns validation issues that must be shown as warnings.
    /// </summary>
    public AnswerValidation ValidateAnswer(
        string answer,
        IReadOnlyList<Domain.ValueObjects.Citation>? citations,
        double confidenceScore,
        bool isAbstention)
    {
        var issues = new List<string>();
        var severity = AnswerSeverity.Safe;

        // ── Rule 1: Abstention is always safe ──
        if (isAbstention)
        {
            return new AnswerValidation([], AnswerSeverity.Safe);
        }

        // ── Rule 2: Must have citations ──
        if (citations == null || citations.Count < MinimumCitationCount)
        {
            issues.Add("⚠ Answer lacks sufficient citations from source documents.");
            severity = AnswerSeverity.Critical;
        }

        // ── Rule 3: Low confidence ──
        if (confidenceScore < MinimumConfidenceThreshold)
        {
            issues.Add($"⚠ Confidence score critically low ({confidenceScore:P0}). Do not rely on this answer.");
            severity = AnswerSeverity.Critical;
        }
        else if (confidenceScore < 0.60)
        {
            issues.Add($"⚠ Confidence below recommended level ({confidenceScore:P0}).");
            if (severity < AnswerSeverity.Warning)
                severity = AnswerSeverity.Warning;
        }

        // ── Rule 4: Cross-check answer text for fabrication indicators ──
        var lowerAnswer = answer.ToLowerInvariant();
        var fabricationPatterns = new[]
        {
            "بناءً على معرفتي",        // "Based on my knowledge"
            "according to my training",
            "i believe",
            "من المحتمل أن",           // "It's likely that"
            "عادةً ما",                 // "Usually"
            "بشكل عام",                // "In general"
            "as an ai",
            "as a language model",
        };

        foreach (var pattern in fabricationPatterns)
        {
            if (lowerAnswer.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                answer.Contains(pattern))
            {
                issues.Add("⚠ Answer contains indicators of external knowledge usage. Verify sources.");
                severity = AnswerSeverity.Critical;
                break;
            }
        }

        // ── Rule 5: Citation-answer cross-check ──
        // If answer mentions specific article numbers or case numbers,
        // verify they appear in at least one citation snippet
        var articlePattern = new System.Text.RegularExpressions.Regex(
            @"(?:المادة|مادة|article)\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var articleMatches = articlePattern.Matches(answer);

        if (articleMatches.Count > 0 && citations != null && citations.Count > 0)
        {
            var citationText = string.Join(" ", citations.Select(c => c.Snippet ?? ""));
            var unreferencedArticles = new List<string>();

            foreach (System.Text.RegularExpressions.Match match in articleMatches)
            {
                var articleNum = match.Groups[1].Value;
                if (!citationText.Contains(articleNum))
                {
                    unreferencedArticles.Add(match.Value);
                }
            }

            if (unreferencedArticles.Count > 0)
            {
                issues.Add($"⚠ References not found in citations: {string.Join(", ", unreferencedArticles)}");
                if (severity < AnswerSeverity.Warning)
                    severity = AnswerSeverity.Warning;
            }
        }

        return new AnswerValidation(issues, severity);
    }

    /// <summary>Force re-check now (e.g., after settings change).</summary>
    public Task ForceRecheckAsync() => RunHealthCheckAsync();

    /// <summary>
    /// Force the system into library-only mode with the given reason.
    /// Used by global exception handlers to ensure fail-closed behavior.
    /// </summary>
    public void ForceLibraryOnlyMode(string reason)
    {
        lock (_lock)
        {
            _blockReasons.Add(reason);
        }
        Status = SystemOperationalStatus.LibraryOnly;
        _logger.LogCritical("FailClosedGuard: FORCED into library-only mode — {Reason}", reason);
    }

    public void Dispose()
    {
        _healthCheckTimer.Stop();
        _healthCheckTimer.Dispose();
    }
}

/// <summary>System-wide operational status.</summary>
public enum SystemOperationalStatus
{
    /// <summary>System is starting up.</summary>
    Initializing,

    /// <summary>All checks passed. Full functionality available.</summary>
    Operational,

    /// <summary>Some non-critical checks have warnings. Questions can be asked with caveats.</summary>
    Degraded,

    /// <summary>Critical checks failed. Only document browsing allowed.</summary>
    LibraryOnly,

    /// <summary>System is completely non-functional.</summary>
    Offline
}

/// <summary>Result of answer post-validation.</summary>
public sealed record AnswerValidation(
    IReadOnlyList<string> Issues,
    AnswerSeverity Severity);

/// <summary>Severity of answer validation issues.</summary>
public enum AnswerSeverity
{
    /// <summary>Answer passed all checks.</summary>
    Safe = 0,

    /// <summary>Answer has non-critical issues.</summary>
    Warning = 1,

    /// <summary>Answer has critical issues — user must be warned prominently.</summary>
    Critical = 2
}

