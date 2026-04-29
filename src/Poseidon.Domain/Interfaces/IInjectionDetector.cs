namespace Poseidon.Domain.Interfaces;

/// <summary>
/// Detects prompt injection attempts in user queries.
/// </summary>
public interface IInjectionDetector
{
    /// <summary>
    /// Analyzes a query for injection patterns. Returns sanitized query and detection result.
    /// </summary>
    InjectionDetectionResult Analyze(string query);
}

public sealed class InjectionDetectionResult
{
    public required string SanitizedQuery { get; init; }
    public bool IsInjectionDetected { get; init; }
    public double InjectionConfidence { get; init; }
    public List<string> DetectedPatterns { get; init; } = [];
    public bool ShouldBlock { get; init; }
}

