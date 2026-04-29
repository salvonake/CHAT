using Poseidon.Desktop.Diagnostics;
using Poseidon.Domain.Interfaces;
using Poseidon.Domain.ValueObjects;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Poseidon.Desktop.Services;

/// <summary>
/// Central fail-closed safety guard. Operational state comes from
/// <see cref="SystemHealthService"/>; answer validation remains here.
/// </summary>
public sealed class FailClosedGuard : IDisposable
{
    private readonly SystemHealthService _healthService;
    private readonly ILogger<FailClosedGuard> _logger;
    private readonly System.Timers.Timer _healthCheckTimer;
    private readonly object _lock = new();
    private volatile SystemOperationalStatus _status = SystemOperationalStatus.Initializing;
    private readonly List<string> _blockReasons = [];
    private readonly List<string> _warnings = [];

    public FailClosedGuard(
        SystemHealthService healthService,
        ILogger<FailClosedGuard> logger)
    {
        _healthService = healthService;
        _logger = logger;
        _healthService.HealthChanged += OnHealthChanged;

        _healthCheckTimer = new System.Timers.Timer(60_000);
        _healthCheckTimer.Elapsed += async (_, _) => await RunHealthCheckAsync();
        _healthCheckTimer.AutoReset = true;
    }

    public FailClosedGuard(
        ILlmService llm,
        IVectorStore vectorStore,
        ModelIntegrityService modelIntegrity,
        ILogger<FailClosedGuard> logger)
        : this(
            CreateCompatibilityHealthService(llm, vectorStore, modelIntegrity),
            logger)
    {
    }

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
    public bool CanAskQuestions => _status is SystemOperationalStatus.Full or SystemOperationalStatus.Degraded;

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

    public bool AllowStrictModeOverride => false;
    public double MinimumConfidenceThreshold => 0.40;
    public int MinimumCitationCount => 1;

    public async Task InitializeAsync()
    {
        _logger.LogInformation("FailClosedGuard: Running initial safety checks...");
        await RunHealthCheckAsync();
        _healthCheckTimer.Start();
        _logger.LogInformation("FailClosedGuard: Status = {Status}, CanAsk = {CanAsk}",
            Status, CanAskQuestions);
    }

    public async Task RunHealthCheckAsync()
    {
        var snapshot = await _healthService.CheckAllAsync();
        ApplySnapshot(snapshot);
    }

    public Task ForceRecheckAsync() => RunHealthCheckAsync();

    public AnswerValidation ValidateAnswer(
        string answer,
        IReadOnlyList<Citation>? citations,
        double confidenceScore,
        bool isAbstention)
    {
        var issues = new List<string>();
        var severity = AnswerSeverity.Safe;

        if (isAbstention)
            return new AnswerValidation([], AnswerSeverity.Safe);

        if (citations == null || citations.Count < MinimumCitationCount)
        {
            issues.Add("⚠ Answer lacks sufficient citations from source documents.");
            severity = AnswerSeverity.Critical;
        }

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

        var lowerAnswer = answer.ToLowerInvariant();
        var fabricationPatterns = new[]
        {
            "بناء على معرفتي",
            "بناءً على معرفتي",
            "according to my training",
            "i believe",
            "من المحتمل ان",
            "من المحتمل أن",
            "عادة ما",
            "عادةً ما",
            "بشكل عام",
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

        var articlePattern = new System.Text.RegularExpressions.Regex(
            @"(?:الماد[ةه]|ماد[ةه]|article)\s+(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var articleMatches = articlePattern.Matches(answer);

        if (articleMatches.Count > 0 && citations != null && citations.Count > 0)
        {
            var citationText = string.Join(" ", citations.Select(c => c.Snippet ?? ""));
            var unreferencedArticles = new List<string>();

            foreach (System.Text.RegularExpressions.Match match in articleMatches)
            {
                var articleNum = match.Groups[1].Value;
                if (!citationText.Contains(articleNum))
                    unreferencedArticles.Add(match.Value);
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

    public void ForceRecoveryMode(string reason)
    {
        lock (_lock)
            _blockReasons.Add(reason);

        Status = SystemOperationalStatus.Recovery;
        _logger.LogCritical("FailClosedGuard: FORCED into recovery mode - {Reason}", reason);
    }

    public void ForceLibraryOnlyMode(string reason) => ForceRecoveryMode(reason);

    private void OnHealthChanged(object? sender, SystemHealthSnapshot snapshot)
    {
        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(SystemHealthSnapshot snapshot)
    {
        var reasons = snapshot.Results
            .Where(r => r.Status == HealthStatus.Error)
            .Select(r => r.Message)
            .ToList();

        var warnings = snapshot.Results
            .Where(r => r.Status == HealthStatus.Warning)
            .Select(r => r.Message)
            .ToList();

        lock (_lock)
        {
            _blockReasons.Clear();
            _blockReasons.AddRange(reasons);
            _warnings.Clear();
            _warnings.AddRange(warnings);
        }

        Status = snapshot.Mode switch
        {
            StartupMode.Full => SystemOperationalStatus.Full,
            StartupMode.Degraded => SystemOperationalStatus.Degraded,
            StartupMode.Recovery => SystemOperationalStatus.Recovery,
            _ => SystemOperationalStatus.Recovery
        };
    }

    private static SystemHealthService CreateCompatibilityHealthService(
        ILlmService llm,
        IVectorStore vectorStore,
        ModelIntegrityService modelIntegrity)
    {
        var temp = Path.GetTempPath();
        var paths = new DataPaths
        {
            DataDirectory = temp,
            ModelsDirectory = temp,
            VectorDbPath = Path.Combine(temp, "vectors.db"),
            HnswIndexPath = Path.Combine(temp, "vectors.hnsw"),
            DocumentDbPath = Path.Combine(temp, "documents.db"),
            AuditDbPath = Path.Combine(temp, "audit.db"),
            WatchDirectory = temp,
            UserConfigPath = Path.Combine(temp, "appsettings.user.json"),
            LogsDirectory = temp,
            AppLogPath = Path.Combine(temp, "app.log"),
            StartupLogPath = Path.Combine(temp, "startup.log")
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:Provider"] = "llamasharp",
                ["Embedding:Provider"] = "onnx",
                ["Retrieval:StrictMode"] = "true",
                ["Security:EncryptionEnabled"] = "true"
            })
            .Build();

        return new SystemHealthService(
            config,
            paths,
            llm,
            new CompatibilityEmbeddingService(),
            vectorStore,
            new CompatibilityEncryptionService(),
            new StartupModeController(),
            NullLogger<SystemHealthService>.Instance,
            modelIntegrity);
    }

    public void Dispose()
    {
        _healthService.HealthChanged -= OnHealthChanged;
        _healthCheckTimer.Stop();
        _healthCheckTimer.Dispose();
    }
}

public enum SystemOperationalStatus
{
    Initializing,
    Full,
    Operational = Full,
    Degraded,
    Recovery,
    LibraryOnly = Recovery,
    Offline
}

file sealed class CompatibilityEmbeddingService : IEmbeddingService
{
    public int EmbeddingDimension => 768;
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Task.FromResult(new float[EmbeddingDimension]);
    public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        => Task.FromResult(texts.Select(_ => new float[EmbeddingDimension]).ToArray());
}

file sealed class CompatibilityEncryptionService : IEncryptionService
{
    public bool IsEnabled => true;
    public byte[] Encrypt(byte[] plaintext, EncryptionPurpose purpose = EncryptionPurpose.VectorData) => plaintext;
    public byte[] Decrypt(byte[] ciphertext, EncryptionPurpose purpose = EncryptionPurpose.VectorData) => ciphertext;
    public string EncryptString(string plaintext, EncryptionPurpose purpose = EncryptionPurpose.VectorData) => plaintext;
    public string DecryptString(string ciphertext, EncryptionPurpose purpose = EncryptionPurpose.VectorData) => ciphertext;
    public byte[] ComputeHmac(byte[] data) => [];
    public bool VerifyHmac(byte[] data, byte[] hmac) => true;
}

public sealed record AnswerValidation(
    IReadOnlyList<string> Issues,
    AnswerSeverity Severity);

public enum AnswerSeverity
{
    Safe = 0,
    Warning = 1,
    Critical = 2
}
