using System.Net.Http.Json;
using LegalAI.Domain.Interfaces;

namespace LegalAI.Api.Services;

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
        var apiKey = _config["CentralManagement:ApiKey"];

        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            client.DefaultRequestHeaders.Add("X-Agent-Key", apiKey);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var vectorHealth = await _vectorStore.GetHealthAsync(stoppingToken);
                var snapshot = _metrics.GetSnapshot();

                var heartbeat = new HeartbeatRequest
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

                var response = await client.PostAsJsonAsync("/api/instances/heartbeat", heartbeat, stoppingToken);
                response.EnsureSuccessStatusCode();

                await ProcessCommandsAsync(client, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to push API heartbeat to central management.");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }

    private async Task ProcessCommandsAsync(HttpClient client, CancellationToken ct)
    {
        var commands = await client.GetFromJsonAsync<List<FleetCommand>>(
            $"/api/instances/{_instance.InstanceId}/commands/api",
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
            await client.PostAsJsonAsync(
                $"/api/instances/{_instance.InstanceId}/commands/{command.Id}/ack",
                new AckRequest { Status = status, Message = message },
                ct);
        }
    }

    private sealed class HeartbeatRequest
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

    private sealed class FleetCommand
    {
        public required string Id { get; init; }
        public required string Action { get; init; }
        public string? Payload { get; init; }
    }

    private sealed class AckRequest
    {
        public required string Status { get; init; }
        public string? Message { get; init; }
    }
}
