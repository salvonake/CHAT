namespace Poseidon.Desktop.Diagnostics;

public sealed class StartupModeController
{
    private static readonly HashSet<string> RecoveryComponents = new(StringComparer.OrdinalIgnoreCase)
    {
        "Configuration",
        "LLM",
        "Embeddings"
    };

    public StartupModeDecision Evaluate(IReadOnlyList<HealthCheckResult> results)
    {
        var hasRecoveryError = results.Any(r =>
            r.Status == HealthStatus.Error && RecoveryComponents.Contains(r.Component));

        if (hasRecoveryError)
            return new StartupModeDecision(StartupMode.Recovery, CanAskQuestions: false);

        if (results.Any(r => r.Status == HealthStatus.Error))
            return new StartupModeDecision(StartupMode.Recovery, CanAskQuestions: false);

        if (results.Any(r => r.Status == HealthStatus.Warning))
            return new StartupModeDecision(StartupMode.Degraded, CanAskQuestions: true);

        return new StartupModeDecision(StartupMode.Full, CanAskQuestions: true);
    }
}
