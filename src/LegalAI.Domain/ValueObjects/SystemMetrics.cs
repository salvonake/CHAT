namespace LegalAI.Domain.ValueObjects;

/// <summary>
/// Telemetry and metrics snapshot for observability.
/// </summary>
public sealed class SystemMetrics
{
    // Retrieval Metrics
    public double AverageSimilarityScore { get; set; }
    public double ContextCompressionRatio { get; set; }
    public double RerankUpliftDelta { get; set; }
    public int HitDiversityScore { get; set; }
    public double RetrievalLatencyP50Ms { get; set; }
    public double RetrievalLatencyP95Ms { get; set; }
    public long TotalQueries { get; set; }

    // LLM Metrics
    public long TotalTokensUsed { get; set; }
    public double AverageGenerationLatencyMs { get; set; }
    public long HallucinationFallbackTriggers { get; set; }
    public long AbstentionCount { get; set; }

    // Indexing Metrics
    public long TotalDocumentsIndexed { get; set; }
    public long TotalChunksStored { get; set; }
    public long DocumentsFailedCount { get; set; }
    public long QuarantinedCount { get; set; }
    public double AverageReindexTimeMs { get; set; }
    public double FileChurnRate { get; set; }
    public int IndexingQueueDepth { get; set; }

    // Security Metrics
    public long InjectionDetections { get; set; }
    public long FailedAuthAttempts { get; set; }
    public bool AuditChainIntegrity { get; set; } = true;

    // Cache Metrics
    public double CacheHitRatio { get; set; }
    public long CacheEntries { get; set; }

    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.UtcNow;
}
