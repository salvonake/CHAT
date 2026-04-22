using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

var instances = new ConcurrentDictionary<string, InstanceRuntimeStatus>();
var commandsByTarget = new ConcurrentDictionary<string, ConcurrentDictionary<string, FleetCommand>>();

app.MapGet("/", () => Results.Ok(new { Service = "LegalAI.Management.Api" }));

app.MapPost("/api/instances/heartbeat", (HeartbeatRequest request) =>
{
    var key = BuildTargetKey(request.InstanceId, request.ServiceType);
    var status = new InstanceRuntimeStatus
    {
        InstanceId = request.InstanceId,
        InstanceName = request.InstanceName,
        ServiceType = request.ServiceType,
        Environment = request.Environment,
        IsHealthy = request.IsHealthy,
        LastSeenAt = DateTimeOffset.UtcNow,
        StartedAt = request.StartedAt,
        Metrics = request.Metrics ?? new Dictionary<string, double>(),
        LastError = request.LastError
    };

    instances[key] = status;
    return Results.Ok(new { accepted = true });
});

app.MapGet("/api/instances", () =>
{
    var now = DateTimeOffset.UtcNow;
    var list = instances.Values
        .OrderBy(s => s.InstanceName)
        .ThenBy(s => s.ServiceType)
        .Select(s => new
        {
            s.InstanceId,
            s.InstanceName,
            s.ServiceType,
            s.Environment,
            Status = now - s.LastSeenAt <= TimeSpan.FromSeconds(90) ? "Online" : "Offline",
            s.IsHealthy,
            s.LastSeenAt,
            s.StartedAt,
            s.LastError,
            s.Metrics
        })
        .ToList();

    return Results.Ok(list);
});

app.MapGet("/api/instances/{instanceId}", (string instanceId) =>
{
    var list = instances.Values
        .Where(s => string.Equals(s.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
        .OrderBy(s => s.ServiceType)
        .ToList();

    return list.Count == 0 ? Results.NotFound() : Results.Ok(list);
});

app.MapPost("/api/instances/{instanceId}/commands", (
    string instanceId,
    CreateFleetCommandRequest request,
    IConfiguration cfg,
    HttpContext context) =>
{
    if (!IsManagementAuthorized(cfg, context))
    {
        return Results.Unauthorized();
    }

    var serviceType = string.IsNullOrWhiteSpace(request.ServiceType)
        ? "worker"
        : request.ServiceType.Trim().ToLowerInvariant();

    var targetKey = BuildTargetKey(instanceId, serviceType);
    var command = new FleetCommand
    {
        Id = Guid.NewGuid().ToString("N"),
        InstanceId = instanceId,
        ServiceType = serviceType,
        Action = request.Action.Trim().ToLowerInvariant(),
        Payload = request.Payload,
        RequestedBy = request.RequestedBy ?? "admin",
        Status = "pending",
        CreatedAt = DateTimeOffset.UtcNow
    };

    var target = commandsByTarget.GetOrAdd(targetKey, _ => new ConcurrentDictionary<string, FleetCommand>());
    target[command.Id] = command;

    return Results.Ok(new
    {
        command.Id,
        command.InstanceId,
        command.ServiceType,
        command.Action,
        command.Status,
        command.CreatedAt
    });
});

app.MapGet("/api/instances/{instanceId}/commands/{serviceType}", (
    string instanceId,
    string serviceType,
    IConfiguration cfg,
    HttpContext context) =>
{
    if (!IsAgentAuthorized(cfg, context))
    {
        return Results.Unauthorized();
    }

    var targetKey = BuildTargetKey(instanceId, serviceType);
    if (!commandsByTarget.TryGetValue(targetKey, out var targetCommands))
    {
        return Results.Ok(Array.Empty<object>());
    }

    var pending = targetCommands.Values
        .Where(c => string.Equals(c.Status, "pending", StringComparison.OrdinalIgnoreCase))
        .OrderBy(c => c.CreatedAt)
        .Take(20)
        .Select(c => new
        {
            c.Id,
            c.Action,
            c.Payload,
            c.CreatedAt
        })
        .ToList();

    return Results.Ok(pending);
});

app.MapPost("/api/instances/{instanceId}/commands/{commandId}/ack", (
    string instanceId,
    string commandId,
    AcknowledgeFleetCommandRequest request,
    IConfiguration cfg,
    HttpContext context) =>
{
    if (!IsAgentAuthorized(cfg, context))
    {
        return Results.Unauthorized();
    }

    var matched = commandsByTarget
        .Where(kvp => kvp.Key.StartsWith(instanceId + ":", StringComparison.OrdinalIgnoreCase))
        .Select(kvp => kvp.Value)
        .FirstOrDefault(dict => dict.TryGetValue(commandId, out _));

    if (matched is null || !matched.TryGetValue(commandId, out var cmd))
    {
        return Results.NotFound();
    }

    cmd.Status = request.Status.Trim().ToLowerInvariant();
    cmd.AcknowledgedAt = DateTimeOffset.UtcNow;
    cmd.AcknowledgementMessage = request.Message;

    return Results.Ok(new { updated = true });
});

app.MapGet("/api/health", () => Results.Ok(new
{
    Status = "Healthy",
    InstanceCount = instances.Count,
    CommandTargets = commandsByTarget.Count,
    Timestamp = DateTimeOffset.UtcNow
}));

app.Run();

static bool IsManagementAuthorized(IConfiguration cfg, HttpContext context)
{
    var key = cfg["Security:ManagementApiKey"];
    if (string.IsNullOrWhiteSpace(key))
    {
        return true;
    }

    return string.Equals(context.Request.Headers["X-Management-Key"], key, StringComparison.Ordinal);
}

static bool IsAgentAuthorized(IConfiguration cfg, HttpContext context)
{
    var key = cfg["Security:AgentApiKey"];
    if (string.IsNullOrWhiteSpace(key))
    {
        return true;
    }

    return string.Equals(context.Request.Headers["X-Agent-Key"], key, StringComparison.Ordinal);
}

static string BuildTargetKey(string instanceId, string serviceType)
    => $"{instanceId.Trim().ToLowerInvariant()}:{serviceType.Trim().ToLowerInvariant()}";

sealed class HeartbeatRequest
{
    public required string InstanceId { get; init; }
    public required string InstanceName { get; init; }
    public required string ServiceType { get; init; }
    public required string Environment { get; init; }
    public bool IsHealthy { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public string? LastError { get; init; }
    public Dictionary<string, double>? Metrics { get; init; }
}

sealed class CreateFleetCommandRequest
{
    public required string Action { get; init; }
    public string? ServiceType { get; init; }
    public string? Payload { get; init; }
    public string? RequestedBy { get; init; }
}

sealed class AcknowledgeFleetCommandRequest
{
    public required string Status { get; init; }
    public string? Message { get; init; }
}

sealed class InstanceRuntimeStatus
{
    public required string InstanceId { get; init; }
    public required string InstanceName { get; init; }
    public required string ServiceType { get; init; }
    public required string Environment { get; init; }
    public required bool IsHealthy { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
    public string? LastError { get; init; }
    public required Dictionary<string, double> Metrics { get; init; }
}

sealed class FleetCommand
{
    public required string Id { get; init; }
    public required string InstanceId { get; init; }
    public required string ServiceType { get; init; }
    public required string Action { get; init; }
    public string? Payload { get; init; }
    public required string RequestedBy { get; init; }
    public required string Status { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? AcknowledgementMessage { get; set; }
}
