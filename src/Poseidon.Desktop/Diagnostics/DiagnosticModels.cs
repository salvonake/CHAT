using Serilog.Events;

namespace Poseidon.Desktop.Diagnostics;

public enum HealthStatus
{
    OK,
    Warning,
    Error
}

public enum StartupMode
{
    Full,
    Degraded,
    Recovery
}

public enum FixAction
{
    EditLlmPath,
    EditEmbeddingPath,
    EditOllamaEndpoint,
    EnableEncryption,
    RetryService,
    OpenSettings,
    OpenLogs,
    RepairConfig
}

public sealed record HealthCheckResult(
    string Component,
    HealthStatus Status,
    string Message,
    FixAction? FixAction,
    IReadOnlyDictionary<string, string>? Details = null);

public sealed record StartupModeDecision(
    StartupMode Mode,
    bool CanAskQuestions);

public sealed record SystemHealthSnapshot(
    StartupMode Mode,
    bool CanAskQuestions,
    DateTimeOffset CheckedAt,
    IReadOnlyList<HealthCheckResult> Results);

public sealed record LiveLogEntry(
    DateTimeOffset Timestamp,
    LogEventLevel Level,
    string Component,
    string Message,
    string? Exception)
{
    public int RepeatCount { get; init; } = 1;
}

public sealed class ConfigurationReloadedEventArgs : EventArgs
{
    public ConfigurationReloadedEventArgs(bool success, string message, Exception? exception = null)
    {
        Success = success;
        Message = message;
        Exception = exception;
    }

    public bool Success { get; }
    public string Message { get; }
    public Exception? Exception { get; }
}
