using LegalAI.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LegalAI.Api.Controllers;

/// <summary>
/// System health, metrics, and operational observability endpoints.
/// </summary>
[ApiController]
[Route("api")]
[Authorize(Roles = "Admin")]
public sealed class OpsController : ControllerBase
{
    private readonly IVectorStore _vectorStore;
    private readonly ILlmService _llm;
    private readonly IMetricsCollector _metrics;
    private readonly IAuditService _audit;

    public OpsController(
        IVectorStore vectorStore,
        ILlmService llm,
        IMetricsCollector metrics,
        IAuditService audit)
    {
        _vectorStore = vectorStore;
        _llm = llm;
        _metrics = metrics;
        _audit = audit;
    }

    /// <summary>
    /// System health check — verifies Qdrant, Ollama, and audit chain integrity.
    /// </summary>
    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        var vectorHealth = await _vectorStore.GetHealthAsync(ct);
        var llmAvailable = await _llm.IsAvailableAsync(ct);
        var auditIntegrity = await _audit.VerifyChainIntegrityAsync(ct);

        var isHealthy = vectorHealth.IsHealthy && llmAvailable && auditIntegrity;

        return isHealthy ? Ok(new
        {
            Status = "Healthy",
            VectorStore = new { vectorHealth.IsHealthy, vectorHealth.VectorCount, vectorHealth.Status },
            LlmAvailable = llmAvailable,
            AuditChainIntegrity = auditIntegrity,
            Timestamp = DateTimeOffset.UtcNow
        }) : StatusCode(503, new
        {
            Status = "Unhealthy",
            VectorStore = new { vectorHealth.IsHealthy, vectorHealth.Error },
            LlmAvailable = llmAvailable,
            AuditChainIntegrity = auditIntegrity,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Retrieval and pipeline metrics.
    /// </summary>
    [HttpGet("metrics")]
    public IActionResult GetMetrics()
    {
        var snapshot = _metrics.GetSnapshot();
        return Ok(snapshot);
    }

    /// <summary>
    /// Operations overview: combined health + key metrics.
    /// </summary>
    [HttpGet("ops/overview")]
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        var vectorHealth = await _vectorStore.GetHealthAsync(ct);
        var snapshot = _metrics.GetSnapshot();

        return Ok(new
        {
            VectorStore = new { vectorHealth.IsHealthy, vectorHealth.VectorCount },
            Metrics = new
            {
                snapshot.TotalQueries,
                snapshot.AbstentionCount,
                snapshot.TotalDocumentsIndexed,
                snapshot.QuarantinedCount,
                snapshot.InjectionDetections,
                snapshot.CacheHitRatio,
                AvgRetrievalLatencyMs = snapshot.RetrievalLatencyP50Ms,
                AvgGenerationLatencyMs = snapshot.AverageGenerationLatencyMs
            },
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Audit log entries.
    /// </summary>
    [HttpGet("audit")]
    public async Task<IActionResult> GetAuditLog(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0, CancellationToken ct = default)
    {
        var entries = await _audit.GetEntriesAsync(limit, offset, ct);
        return Ok(entries);
    }

    /// <summary>
    /// Verify audit chain integrity.
    /// </summary>
    [HttpGet("audit/verify")]
    public async Task<IActionResult> VerifyAudit(CancellationToken ct)
    {
        var isValid = await _audit.VerifyChainIntegrityAsync(ct);
        return Ok(new
        {
            ChainIntegrity = isValid ? "VALID" : "BROKEN",
            Timestamp = DateTimeOffset.UtcNow
        });
    }
}
