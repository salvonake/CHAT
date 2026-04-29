using FluentAssertions;
using Poseidon.Infrastructure.Telemetry;

namespace Poseidon.UnitTests.Infrastructure;

/// <summary>
/// Tests for <see cref="InMemoryMetricsCollector"/>. Verifies counters,
/// gauges, latency tracking, percentile calculations, sliding window,
/// cache hit ratio, and snapshot mapping. Thread-safety is validated
/// via concurrent stress test.
/// </summary>
public sealed class InMemoryMetricsCollectorTests
{
    private readonly InMemoryMetricsCollector _collector = new();

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Counters
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void Counter_IncrementOnce_ReturnsOne()
    {
        _collector.IncrementCounter("total_queries");

        var snapshot = _collector.GetSnapshot();
        snapshot.TotalQueries.Should().Be(1);
    }

    [Fact]
    public void Counter_IncrementMultiple_Accumulates()
    {
        _collector.IncrementCounter("total_queries");
        _collector.IncrementCounter("total_queries");
        _collector.IncrementCounter("total_queries");

        _collector.GetSnapshot().TotalQueries.Should().Be(3);
    }

    [Fact]
    public void Counter_IncrementByValue_AddsCorrectAmount()
    {
        _collector.IncrementCounter("total_tokens", 500);
        _collector.IncrementCounter("total_tokens", 300);

        _collector.GetSnapshot().TotalTokensUsed.Should().Be(800);
    }

    [Fact]
    public void Counter_Unset_ReturnsZero()
    {
        _collector.GetSnapshot().TotalQueries.Should().Be(0);
    }

    [Fact]
    public void Counter_AllMappedCorrectly()
    {
        _collector.IncrementCounter("total_queries", 10);
        _collector.IncrementCounter("total_tokens", 5000);
        _collector.IncrementCounter("hallucination_fallback", 2);
        _collector.IncrementCounter("abstention_count", 3);
        _collector.IncrementCounter("documents_indexed", 100);
        _collector.IncrementCounter("documents_failed", 5);
        _collector.IncrementCounter("documents_quarantined", 2);
        _collector.IncrementCounter("injection_blocked", 7);
        _collector.IncrementCounter("auth_failed", 1);
        _collector.IncrementCounter("cache_entries", 50);

        var s = _collector.GetSnapshot();
        s.TotalQueries.Should().Be(10);
        s.TotalTokensUsed.Should().Be(5000);
        s.HallucinationFallbackTriggers.Should().Be(2);
        s.AbstentionCount.Should().Be(3);
        s.TotalDocumentsIndexed.Should().Be(100);
        s.DocumentsFailedCount.Should().Be(5);
        s.QuarantinedCount.Should().Be(2);
        s.InjectionDetections.Should().Be(7);
        s.FailedAuthAttempts.Should().Be(1);
        s.CacheEntries.Should().Be(50);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Gauges
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void Gauge_SetAndRead()
    {
        _collector.SetGauge("avg_similarity", 0.85);
        _collector.GetSnapshot().AverageSimilarityScore.Should().Be(0.85);
    }

    [Fact]
    public void Gauge_Overwrite_TakesLatestValue()
    {
        _collector.SetGauge("compression_ratio", 0.5);
        _collector.SetGauge("compression_ratio", 0.75);
        _collector.GetSnapshot().ContextCompressionRatio.Should().Be(0.75);
    }

    [Fact]
    public void Gauge_Unset_ReturnsZero()
    {
        _collector.GetSnapshot().AverageSimilarityScore.Should().Be(0);
    }

    [Fact]
    public void Gauge_AllMappedCorrectly()
    {
        _collector.SetGauge("avg_similarity", 0.9);
        _collector.SetGauge("compression_ratio", 0.6);
        _collector.SetGauge("rerank_uplift", 0.15);
        _collector.SetGauge("total_chunks", 1500);
        _collector.SetGauge("indexing_queue_depth", 3);

        var s = _collector.GetSnapshot();
        s.AverageSimilarityScore.Should().Be(0.9);
        s.ContextCompressionRatio.Should().Be(0.6);
        s.RerankUpliftDelta.Should().Be(0.15);
        s.TotalChunksStored.Should().Be(1500);
        s.IndexingQueueDepth.Should().Be(3);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Latency Tracking
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void Latency_SingleValue_AverageEqualsValue()
    {
        _collector.RecordLatency("generation_latency", 100);

        _collector.GetSnapshot().AverageGenerationLatencyMs.Should().Be(100);
    }

    [Fact]
    public void Latency_MultipleValues_AverageIsCorrect()
    {
        _collector.RecordLatency("generation_latency", 100);
        _collector.RecordLatency("generation_latency", 200);
        _collector.RecordLatency("generation_latency", 300);

        _collector.GetSnapshot().AverageGenerationLatencyMs.Should().Be(200);
    }

    [Fact]
    public void Latency_Unrecorded_AverageIsZero()
    {
        _collector.GetSnapshot().AverageGenerationLatencyMs.Should().Be(0);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Percentile Calculations
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void Percentile_SingleValue_ReturnsIt()
    {
        _collector.RecordLatency("retrieval_pipeline", 42);

        var s = _collector.GetSnapshot();
        s.RetrievalLatencyP50Ms.Should().Be(42);
        s.RetrievalLatencyP95Ms.Should().Be(42);
    }

    [Fact]
    public void Percentile_TenValues_P50IsMedian()
    {
        // Record 1..10
        for (int i = 1; i <= 10; i++)
            _collector.RecordLatency("retrieval_pipeline", i * 10);

        // P50 of {10,20,30,40,50,60,70,80,90,100}
        // ceiling(0.50 * 10) - 1 = 4 â†’ sorted[4] = 50
        _collector.GetSnapshot().RetrievalLatencyP50Ms.Should().Be(50);
    }

    [Fact]
    public void Percentile_TenValues_P95IsHighEnd()
    {
        for (int i = 1; i <= 10; i++)
            _collector.RecordLatency("retrieval_pipeline", i * 10);

        // P95: ceiling(0.95 * 10) - 1 = 9 â†’ sorted[9] = 100
        _collector.GetSnapshot().RetrievalLatencyP95Ms.Should().Be(100);
    }

    [Fact]
    public void Percentile_UnsortedData_SortsCorrectly()
    {
        _collector.RecordLatency("retrieval_pipeline", 80);
        _collector.RecordLatency("retrieval_pipeline", 20);
        _collector.RecordLatency("retrieval_pipeline", 60);
        _collector.RecordLatency("retrieval_pipeline", 40);

        // Sorted: {20,40,60,80}, P50: ceiling(0.50*4)-1 = 1 â†’ sorted[1] = 40
        _collector.GetSnapshot().RetrievalLatencyP50Ms.Should().Be(40);
    }

    [Fact]
    public void Percentile_Empty_ReturnsZero()
    {
        _collector.GetSnapshot().RetrievalLatencyP50Ms.Should().Be(0);
        _collector.GetSnapshot().RetrievalLatencyP95Ms.Should().Be(0);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Sliding Window (1000 values)
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void SlidingWindow_Over1000_TrimsOldEntries()
    {
        // Add 1050 values: 1, 2, ..., 1050
        for (int i = 1; i <= 1050; i++)
            _collector.RecordLatency("retrieval_pipeline", i);

        // After trim, should keep last 1000: 51..1050
        // Average of 51..1050 = (51+1050)/2 = 550.5
        // But retrieval_pipeline maps to percentile, not average. Use a known tracker.
        // Let's check via generation_latency average.

        var collector2 = new InMemoryMetricsCollector();
        for (int i = 1; i <= 1050; i++)
            collector2.RecordLatency("generation_latency", i);

        // Average of 51..1050 = 550.5
        collector2.GetSnapshot().AverageGenerationLatencyMs.Should().Be(550.5);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Cache Hit Ratio
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void CacheHitRatio_NoData_ReturnsZero()
    {
        _collector.GetSnapshot().CacheHitRatio.Should().Be(0);
    }

    [Fact]
    public void CacheHitRatio_AllHits_ReturnsOne()
    {
        _collector.IncrementCounter("cache_hit", 100);
        _collector.GetSnapshot().CacheHitRatio.Should().Be(1.0);
    }

    [Fact]
    public void CacheHitRatio_AllMisses_ReturnsZero()
    {
        _collector.IncrementCounter("cache_miss", 100);
        _collector.GetSnapshot().CacheHitRatio.Should().Be(0);
    }

    [Fact]
    public void CacheHitRatio_Mixed_CalculatesCorrectly()
    {
        _collector.IncrementCounter("cache_hit", 75);
        _collector.IncrementCounter("cache_miss", 25);

        _collector.GetSnapshot().CacheHitRatio.Should().Be(0.75);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Reset
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void Reset_ClearsAllState()
    {
        _collector.IncrementCounter("total_queries", 100);
        _collector.SetGauge("avg_similarity", 0.9);
        _collector.RecordLatency("retrieval_pipeline", 42);

        _collector.Reset();

        var s = _collector.GetSnapshot();
        s.TotalQueries.Should().Be(0);
        s.AverageSimilarityScore.Should().Be(0);
        s.RetrievalLatencyP50Ms.Should().Be(0);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Thread Safety
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task ConcurrentWrites_DoNotLoseData()
    {
        const int threadsPerOp = 10;
        const int opsPerThread = 100;

        var tasks = new List<Task>();
        for (int t = 0; t < threadsPerOp; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < opsPerThread; i++)
                {
                    _collector.IncrementCounter("total_queries");
                    _collector.RecordLatency("retrieval_pipeline", i);
                    _collector.SetGauge("avg_similarity", 0.5);
                }
            }));
        }

        await Task.WhenAll(tasks);

        _collector.GetSnapshot().TotalQueries.Should().Be(threadsPerOp * opsPerThread);
    }
}

