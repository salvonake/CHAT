using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Poseidon.Security.Configuration;
using Poseidon.Security.Secrets;

var builder = WebApplication.CreateBuilder(args);
SecurityConfigurationValidator.ValidateManagementApi(builder.Configuration, builder.Environment.EnvironmentName);

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

app.MapGet("/", () => Results.Ok(new { Service = "Poseidon.Management.Api" }));

app.MapPost("/api/instances/heartbeat", (
    HeartbeatRequest request,
    IConfiguration cfg,
    HttpContext context,
    ILoggerFactory loggerFactory) =>
{
    if (!IsAgentAuthorized(cfg, context, loggerFactory.CreateLogger("Poseidon.Management.Auth")))
    {
        return Results.Unauthorized();
    }

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

app.MapGet("/api/instances", (
    IConfiguration cfg,
    HttpContext context,
    ILoggerFactory loggerFactory) =>
{
    if (!IsManagementAuthorized(cfg, context, loggerFactory.CreateLogger("Poseidon.Management.Auth")))
    {
        return Results.Unauthorized();
    }

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

app.MapGet("/api/instances/{instanceId}", (
    string instanceId,
    IConfiguration cfg,
    HttpContext context,
    ILoggerFactory loggerFactory) =>
{
    if (!IsManagementAuthorized(cfg, context, loggerFactory.CreateLogger("Poseidon.Management.Auth")))
    {
        return Results.Unauthorized();
    }

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
    HttpContext context,
    ILoggerFactory loggerFactory) =>
{
    if (!IsManagementAuthorized(cfg, context, loggerFactory.CreateLogger("Poseidon.Management.Auth")))
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
    HttpContext context,
    ILoggerFactory loggerFactory) =>
{
    if (!IsAgentAuthorized(cfg, context, loggerFactory.CreateLogger("Poseidon.Management.Auth")))
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
    HttpContext context,
    ILoggerFactory loggerFactory) =>
{
    if (!IsAgentAuthorized(cfg, context, loggerFactory.CreateLogger("Poseidon.Management.Auth")))
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

static bool IsManagementAuthorized(IConfiguration cfg, HttpContext context, ILogger logger)
{
    return IsAuthorized(
        cfg,
        context,
        logger,
        role: "management",
        plaintextKey: "Security:ManagementApiKey",
        referenceKey: "Security:ManagementApiKeyRef",
        secondaryPlaintextKey: "Security:SecondaryManagementApiKey",
        secondaryReferenceKey: "Security:SecondaryManagementApiKeyRef",
        legacyHeader: "X-Management-Key");
}

static bool IsAgentAuthorized(IConfiguration cfg, HttpContext context, ILogger logger)
{
    return IsAuthorized(
        cfg,
        context,
        logger,
        role: "agent",
        plaintextKey: "Security:AgentApiKey",
        referenceKey: "Security:AgentApiKeyRef",
        secondaryPlaintextKey: "Security:SecondaryAgentApiKey",
        secondaryReferenceKey: "Security:SecondaryAgentApiKeyRef",
        legacyHeader: "X-Agent-Key");
}

static bool IsAuthorized(
    IConfiguration cfg,
    HttpContext context,
    ILogger logger,
    string role,
    string plaintextKey,
    string referenceKey,
    string secondaryPlaintextKey,
    string secondaryReferenceKey,
    string legacyHeader)
{
    var validationContext = SecurityValidationContext.FromConfiguration(cfg);
    var requireSigned = cfg.GetValue("Security:RequireSignedManagementRequests", validationContext.IsProductionLike);
    var candidates = ResolveSecretCandidates(
        cfg,
        validationContext,
        plaintextKey,
        referenceKey,
        secondaryPlaintextKey,
        secondaryReferenceKey);

    if (ManagementPlaneSecurity.Verify(context, candidates, role, out var reason))
        return true;

    if (requireSigned)
    {
        logger.LogWarning(
            "Management-plane {Role} authentication failed from {RemoteIp}: {Reason}",
            role,
            context.Connection.RemoteIpAddress,
            reason);
        return false;
    }

    var legacyValue = context.Request.Headers[legacyHeader].ToString();
    var legacyAccepted = candidates.Any(candidate => FixedTimeEquals(candidate.Secret, legacyValue));
    if (!legacyAccepted)
    {
        logger.LogWarning(
            "Legacy management-plane {Role} authentication failed from {RemoteIp}",
            role,
            context.Connection.RemoteIpAddress);
    }

    return legacyAccepted;
}

static IReadOnlyList<(string Secret, string KeyVersion)> ResolveSecretCandidates(
    IConfiguration cfg,
    SecurityValidationContext context,
    string plaintextKey,
    string referenceKey,
    string secondaryPlaintextKey,
    string secondaryReferenceKey)
{
    var primary = ConfigurationSecretResolver.ResolveRequiredSecret(cfg, context, plaintextKey, referenceKey, 32);
    var values = new List<(string Secret, string KeyVersion)>
    {
        (primary.Value, primary.Version ?? "primary")
    };

    var secondary = ConfigurationSecretResolver.ResolveOptionalSecret(cfg, context, secondaryPlaintextKey, secondaryReferenceKey, 32);
    if (secondary is not null)
        values.Add((secondary.Value, secondary.Version ?? "secondary"));

    return values;
}

static bool FixedTimeEquals(string expected, string actual)
{
    if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        return false;

    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    var actualBytes = Encoding.UTF8.GetBytes(actual);
    try
    {
        return expectedBytes.Length == actualBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
    finally
    {
        CryptographicOperations.ZeroMemory(expectedBytes);
        CryptographicOperations.ZeroMemory(actualBytes);
    }
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
