using Poseidon.Domain.Interfaces;
using Poseidon.Security.Configuration;
using Poseidon.Security.Secrets;

namespace Poseidon.Api.Services;

internal sealed class CentralManagementHeartbeatService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly IMetricsCollector _metrics;
    private readonly IVectorStore _vectorStore;
    private readonly IAuditService _audit;
    private readonly global::InstanceRuntimeOptions _instance;
    private readonly ILogger<CentralManagementHeartbeatService> _logger;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public CentralManagementHeartbeatService(
        IConfiguration config,
        IMetricsCollector metrics,
        IVectorStore vectorStore,
        IAuditService audit,
        global::InstanceRuntimeOptions instance,
        ILogger<CentralManagementHeartbeatService> logger)
    {
        _config = config;
        _metrics = metrics;
        _vectorStore = vectorStore;
        _audit = audit;
        _instance = instance;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("CentralManagement:Enabled", false))
        {
            _logger.LogInformation("Central management heartbeat is disabled.");
            return;
        }

        var baseUrl = _config["CentralManagement:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _logger.LogWarning("Central management enabled but BaseUrl is missing.");
            return;
        }

        var intervalSeconds = Math.Clamp(_config.GetValue("CentralManagement:HeartbeatIntervalSeconds", 30), 5, 300);
        var agentSecret = ConfigurationSecretResolver.ResolveRequiredSecret(
            _config,
            SecurityValidationContext.FromConfiguration(_config),
            "CentralManagement:ApiKey",
            "CentralManagement:ApiKeyRef",
            32);
        var keyVersion = agentSecret.Version ?? "primary";

        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var vectorHealth = await _vectorStore.GetHealthAsync(stoppingToken);
                var snapshot = _metrics.GetSnapshot();

                var heartbeat = new ManagementPlaneHeartbeatRequest
                {
                    InstanceId = _instance.InstanceId,
                    InstanceName = _instance.InstanceName,
                    ServiceType = "api",
                    Environment = _instance.Environment,
                    IsHealthy = vectorHealth.IsHealthy,
                    StartedAt = _startedAt,
                    LastError = vectorHealth.Error,
                    Metrics = new Dictionary<string, double>
                    {
                        ["vectorCount"] = vectorHealth.VectorCount,
                        ["queries"] = snapshot.TotalQueries,
                        ["queryLatencyP50Ms"] = snapshot.RetrievalLatencyP50Ms,
                        ["queryLatencyP95Ms"] = snapshot.RetrievalLatencyP95Ms,
                        ["generationLatencyMs"] = snapshot.AverageGenerationLatencyMs,
                        ["abstentions"] = snapshot.AbstentionCount,
                        ["injectionDetections"] = snapshot.InjectionDetections
                    }
                };

                using var response = await ManagementPlaneClient.SendSignedJsonAsync(
                    client,
                    HttpMethod.Post,
                    "/api/instances/heartbeat",
                    heartbeat,
                    agentSecret.Value,
                    keyVersion,
                    stoppingToken);
                response.EnsureSuccessStatusCode();

                await ProcessCommandsAsync(client, agentSecret.Value, keyVersion, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to push API heartbeat to central management.");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }

    private async Task ProcessCommandsAsync(HttpClient client, string agentKey, string keyVersion, CancellationToken ct)
    {
        var commands = await ManagementPlaneClient.GetSignedJsonAsync<List<ManagementPlaneFleetCommand>>(
            client,
            $"/api/instances/{_instance.InstanceId}/commands/api",
            agentKey,
            keyVersion,
            ct) ?? [];
        foreach (var command in commands)
        {
            var status = "accepted";
            var message = command.Action switch
            {
                "restart" => "Restart requested; service-level restart must be orchestrated by host supervisor.",
                "reindex" => "Reindex command accepted by API agent; execution is delegated to worker service.",
                _ => "Unknown command accepted for tracking."
            };

            await _audit.LogAsync("CENTRAL_COMMAND_RECEIVED", $"API command {command.Action} ({command.Id})", ct: ct);
            using var ackResponse = await ManagementPlaneClient.SendSignedJsonAsync(
                client,
                HttpMethod.Post,
                $"/api/instances/{_instance.InstanceId}/commands/{command.Id}/ack",
                new ManagementPlaneCommandAck { Status = status, Message = message },
                agentKey,
                keyVersion,
                ct);
            ackResponse.EnsureSuccessStatusCode();
        }
    }
}
