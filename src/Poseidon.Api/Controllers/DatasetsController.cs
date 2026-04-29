using System.Security.Claims;
using Poseidon.Api.Localization;
using Poseidon.Domain.Entities;
using Poseidon.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Poseidon.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class DatasetsController : ControllerBase
{
    private readonly IDatasetStore _datasets;
    private readonly IUserDomainGrantStore _domainGrants;
    private readonly IDomainModuleRegistry _domainRegistry;
    private readonly ApiTextLocalizer _text;

    public DatasetsController(
        IDatasetStore datasets,
        IUserDomainGrantStore domainGrants,
        IDomainModuleRegistry domainRegistry,
        ApiTextLocalizer text)
    {
        _datasets = datasets;
        _domainGrants = domainGrants;
        _domainRegistry = domainRegistry;
        _text = text;
    }

    [HttpGet]
    [Authorize(Policy = "CanRead")]
    public async Task<IActionResult> List(
        [FromQuery] string? domainId,
        [FromQuery] bool includeArchived = false,
        CancellationToken ct = default)
    {
        var language = _text.ResolveLanguage(HttpContext);

        if (User.IsInRole("Admin"))
        {
            if (string.IsNullOrWhiteSpace(domainId))
            {
                return Ok((await _datasets.GetAllAsync(includeArchived, ct)).Select(Map));
            }

            var normalizedDomain = NormalizeDomain(domainId);
            return Ok((await _datasets.GetByDomainAsync(normalizedDomain, includeArchived, ct)).Select(Map));
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new { error = _text.T("UserIdentityNotFound", language) });
        }

        var grants = await _domainGrants.GetForUserAsync(userId, ct);
        if (grants.Count == 0)
        {
            return Ok(Array.Empty<DatasetDto>());
        }

        var datasets = await _datasets.GetAllAsync(includeArchived, ct);
        var normalizedRequestedDomain = string.IsNullOrWhiteSpace(domainId)
            ? null
            : NormalizeDomain(domainId);

        var filtered = datasets.Where(ds => IsDatasetAllowed(ds, grants));
        if (normalizedRequestedDomain is not null)
        {
            filtered = filtered.Where(ds =>
                string.Equals(ds.DomainId, normalizedRequestedDomain, StringComparison.OrdinalIgnoreCase));
        }

        return Ok(filtered.Select(Map));
    }

    [HttpGet("{id}")]
    [Authorize(Policy = "CanRead")]
    public async Task<IActionResult> GetById(string id, CancellationToken ct)
    {
        var language = _text.ResolveLanguage(HttpContext);

        var dataset = await _datasets.GetByIdAsync(id, ct);
        if (dataset is null)
        {
            return NotFound(new { error = _text.T("DatasetNotFound", language) });
        }

        if (User.IsInRole("Admin"))
        {
            return Ok(Map(dataset));
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new { error = _text.T("UserIdentityNotFound", language) });
        }

        var allowed = await _domainGrants.HasAccessAsync(userId, dataset.DomainId, dataset.Name, ct);
        if (!allowed)
        {
            return Forbid();
        }

        return Ok(Map(dataset));
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] CreateDatasetRequest request, CancellationToken ct)
    {
        var language = _text.ResolveLanguage(HttpContext);

        if (string.IsNullOrWhiteSpace(request.DomainId) || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = _text.T("DomainAndNameRequired", language) });
        }

        var domainId = NormalizeDomain(request.DomainId);
        if (!_domainRegistry.TryGet(domainId, out _))
        {
            return BadRequest(new { error = _text.T("UnknownDomain", language, domainId) });
        }

        var datasetName = request.Name.Trim();
        var exists = await _datasets.ExistsByNameAsync(domainId, datasetName, ct);
        if (exists)
        {
            return Conflict(new { error = _text.T("DatasetNameExists", language) });
        }

        var now = DateTimeOffset.UtcNow;
        var dataset = new Dataset
        {
            Id = Guid.NewGuid().ToString("N"),
            DomainId = domainId,
            Name = datasetName,
            Description = request.Description?.Trim(),
            Lifecycle = request.Lifecycle ?? DatasetLifecycle.Active,
            Sensitivity = request.Sensitivity ?? DatasetSensitivity.Internal,
            OwnerUserId = request.OwnerUserId,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _datasets.CreateAsync(dataset, ct);
        return CreatedAtAction(nameof(GetById), new { id = dataset.Id }, Map(dataset));
    }

    [HttpPatch("{id}/lifecycle")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SetLifecycle(
        string id,
        [FromBody] UpdateDatasetLifecycleRequest request,
        CancellationToken ct)
    {
        var language = _text.ResolveLanguage(HttpContext);

        var existing = await _datasets.GetByIdAsync(id, ct);
        if (existing is null)
        {
            return NotFound(new { error = _text.T("DatasetNotFound", language) });
        }

        await _datasets.SetLifecycleAsync(id, request.Lifecycle, ct);
        return Ok(new { message = _text.T("DatasetLifecycleUpdated", language) });
    }

    private static bool IsDatasetAllowed(Dataset dataset, IReadOnlyList<UserDomainGrant> grants)
    {
        return grants.Any(g =>
            string.Equals(g.DomainId, dataset.DomainId, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(g.DatasetScope)
             || string.Equals(g.DatasetScope, dataset.Name, StringComparison.OrdinalIgnoreCase)));
    }

    private static DatasetDto Map(Dataset dataset)
    {
        return new DatasetDto
        {
            Id = dataset.Id,
            DomainId = dataset.DomainId,
            Name = dataset.Name,
            Description = dataset.Description,
            Lifecycle = dataset.Lifecycle.ToString(),
            Sensitivity = dataset.Sensitivity.ToString(),
            OwnerUserId = dataset.OwnerUserId,
            CreatedAt = dataset.CreatedAt,
            UpdatedAt = dataset.UpdatedAt
        };
    }

    private static string NormalizeDomain(string value)
    {
        return value.Trim().ToLowerInvariant();
    }
}

public sealed class CreateDatasetRequest
{
    public string DomainId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public DatasetLifecycle? Lifecycle { get; init; }
    public DatasetSensitivity? Sensitivity { get; init; }
    public string? OwnerUserId { get; init; }
}

public sealed class UpdateDatasetLifecycleRequest
{
    public DatasetLifecycle Lifecycle { get; init; } = DatasetLifecycle.Active;
}

public sealed class DatasetDto
{
    public required string Id { get; init; }
    public required string DomainId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Lifecycle { get; init; }
    public required string Sensitivity { get; init; }
    public string? OwnerUserId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

