using System.Collections.Concurrent;
using LegalAI.Domain.Interfaces;
using LegalAI.Domain.ValueObjects;

namespace LegalAI.Infrastructure.Telemetry;

/// <summary>
/// In-memory metrics collector for system observability.
/// Thread-safe counters, gauges, and latency histograms.
/// </summary>
public sealed class InMemoryMetricsCollector : IMetricsCollector
{
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new();
    private readonly ConcurrentDictionary<string, LatencyTracker> _latencies = new();

    public void IncrementCounter(string name, long value = 1)
    {
        _counters.AddOrUpdate(name, value, (_, existing) => existing + value);
    }

    public void RecordLatency(string name, double milliseconds)
    {
        var tracker = _latencies.GetOrAdd(name, _ => new LatencyTracker());
        tracker.Record(milliseconds);
    }

    public void SetGauge(string name, double value)
    {
        _gauges[name] = value;
    }

    public SystemMetrics GetSnapshot()
    {
        return new SystemMetrics
        {
            // Retrieval
            AverageSimilarityScore = GetGauge("avg_similarity"),
            ContextCompressionRatio = GetGauge("compression_ratio"),
            RerankUpliftDelta = GetGauge("rerank_uplift"),
            TotalQueries = GetCounter("total_queries"),
            RetrievalLatencyP50Ms = GetLatencyPercentile("retrieval_pipeline", 0.50),
            RetrievalLatencyP95Ms = GetLatencyPercentile("retrieval_pipeline", 0.95),

            // LLM
            TotalTokensUsed = GetCounter("total_tokens"),
            AverageGenerationLatencyMs = GetLatencyAverage("generation_latency"),
            HallucinationFallbackTriggers = GetCounter("hallucination_fallback"),
            AbstentionCount = GetCounter("abstention_count"),

            // Indexing
            TotalDocumentsIndexed = GetCounter("documents_indexed"),
            TotalChunksStored = (long)GetGauge("total_chunks"),
            DocumentsFailedCount = GetCounter("documents_failed"),
            QuarantinedCount = GetCounter("documents_quarantined"),
            AverageReindexTimeMs = GetLatencyAverage("indexing_latency"),
            IndexingQueueDepth = (int)GetGauge("indexing_queue_depth"),

            // Security
            InjectionDetections = GetCounter("injection_blocked"),
            FailedAuthAttempts = GetCounter("auth_failed"),

            // Cache
            CacheHitRatio = ComputeCacheHitRatio(),
            CacheEntries = GetCounter("cache_entries")
        };
    }

    public void Reset()
    {
        _counters.Clear();
        _gauges.Clear();
        _latencies.Clear();
    }

    private long GetCounter(string name) =>
        _counters.TryGetValue(name, out var v) ? v : 0;

    private double GetGauge(string name) =>
        _gauges.TryGetValue(name, out var v) ? v : 0;

    private double GetLatencyPercentile(string name, double percentile) =>
        _latencies.TryGetValue(name, out var tracker) ? tracker.GetPercentile(percentile) : 0;

    private double GetLatencyAverage(string name) =>
        _latencies.TryGetValue(name, out var tracker) ? tracker.Average : 0;

    private double ComputeCacheHitRatio()
    {
        var hits = GetCounter("cache_hit");
        var misses = GetCounter("cache_miss");
        var total = hits + misses;
        return total > 0 ? (double)hits / total : 0;
    }

    /// <summary>
    /// Tracks latency values for percentile calculation.
    /// </summary>
    private sealed class LatencyTracker
    {
        private readonly List<double> _values = [];
        private readonly object _lock = new();

        public double Average
        {
            get
            {
                lock (_lock)
                    return _values.Count > 0 ? _values.Average() : 0;
            }
        }

        public void Record(double value)
        {
            lock (_lock)
            {
                _values.Add(value);
                // Keep last 1000 values
                if (_values.Count > 1000)
                    _values.RemoveRange(0, _values.Count - 1000);
            }
        }

        public double GetPercentile(double percentile)
        {
            lock (_lock)
            {
                if (_values.Count == 0) return 0;
                var sorted = _values.OrderBy(v => v).ToList();
                var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
                return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
            }
        }
    }
}
