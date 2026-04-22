using LegalAI.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LegalAI.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "CanIngest")]
public sealed class IngestionJobsController : ControllerBase
{
    private readonly IIngestionJobStore _jobs;

    public IngestionJobsController(IIngestionJobStore jobs)
    {
        _jobs = jobs;
    }

    [HttpGet]
    public async Task<IActionResult> GetRecent([FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var jobs = await _jobs.GetRecentAsync(limit, ct);
        return Ok(jobs.Select(j => new
        {
            j.Id,
            j.FilePath,
            j.ContentHash,
            Status = j.Status.ToString(),
            j.AttemptCount,
            j.MaxAttempts,
            j.LastError,
            j.QuarantinePath,
            j.CreatedAt,
            j.UpdatedAt,
            j.LastAttemptAt,
            j.NextAttemptAt
        }));
    }
}
