using LegalAI.Domain.ValueObjects;

namespace LegalAI.Domain.Interfaces;

/// <summary>
/// Collects and exposes system telemetry metrics.
/// </summary>
public interface IMetricsCollector
{
    void IncrementCounter(string name, long value = 1);
    void RecordLatency(string name, double milliseconds);
    void SetGauge(string name, double value);
    SystemMetrics GetSnapshot();
    void Reset();
}
