using Poseidon.Application.Commands;
using Poseidon.Domain.Interfaces;
using Poseidon.Security.Configuration;
using Poseidon.Security.Secrets;
using MediatR;

namespace Poseidon.WorkerService;

internal sealed class CentralManagementHeartbeatService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly IMetricsCollector _metrics;
    private readonly IIngestionJobStore _jobs;
    private readonly IMediator _mediator;
    private readonly IAuditService _audit;
    private readonly global::InstanceRuntimeOptions _instance;
    private readonly ILogger<CentralManagementHeartbeatService> _logger;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public CentralManagementHeartbeatService(
        IConfiguration config,
        IMetricsCollector metrics,
        IIngestionJobStore jobs,
        IMediator mediator,
        IAuditService audit,
        global::InstanceRuntimeOptions instance,
        ILogger<CentralManagementHeartbeatService> logger)
    {
        _config = config;
        _metrics = metrics;
        _jobs = jobs;
        _mediator = mediator;
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
                var recentJobs = await _jobs.GetRecentAsync(50, stoppingToken);
                var snapshot = _metrics.GetSnapshot();

                var heartbeat = new ManagementPlaneHeartbeatRequest
                {
                    InstanceId = _instance.InstanceId,
                    InstanceName = _instance.InstanceName,
                    ServiceType = "worker",
                    Environment = _instance.Environment,
                    IsHealthy = true,
                    StartedAt = _startedAt,
                    LastError = null,
                    Metrics = new Dictionary<string, double>
                    {
                        ["jobsRecent"] = recentJobs.Count,
                        ["jobsFailedRecent"] = recentJobs.Count(j => string.Equals(j.Status.ToString(), "Failed", StringComparison.OrdinalIgnoreCase)),
                        ["jobsQuarantinedRecent"] = recentJobs.Count(j => string.Equals(j.Status.ToString(), "Quarantined", StringComparison.OrdinalIgnoreCase)),
                        ["indexingQueueDepth"] = snapshot.IndexingQueueDepth,
                        ["documentsIndexed"] = snapshot.TotalDocumentsIndexed,
                        ["documentsFailed"] = snapshot.DocumentsFailedCount
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
                _logger.LogWarning(ex, "Failed to push worker heartbeat to central management.");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }

    private async Task ProcessCommandsAsync(HttpClient client, string agentKey, string keyVersion, CancellationToken ct)
    {
        var commands = await ManagementPlaneClient.GetSignedJsonAsync<List<ManagementPlaneFleetCommand>>(
            client,
            $"/api/instances/{_instance.InstanceId}/commands/worker",
            agentKey,
            keyVersion,
            ct) ?? [];
        foreach (var command in commands)
        {
            var status = "accepted";
            var message = "Command accepted.";

            try
            {
                switch (command.Action)
                {
                    case "reindex":
                        var targetPath = string.IsNullOrWhiteSpace(command.Payload)
                            ? _config.GetValue("Data:PdfWatchDirectory", "pdfs")!
                            : command.Payload;

                        await _mediator.Send(new IngestDirectoryCommand
                        {
                            DirectoryPath = targetPath,
                            Recursive = true,
                            UserId = "system"
                        }, ct);

                        status = "completed";
                        message = $"Reindex executed for {targetPath}";
                        break;

                    case "restart":
                        status = "accepted";
                        message = "Restart requested; restart must be orchestrated by service supervisor.";
                        break;

                    default:
                        status = "failed";
                        message = $"Unsupported command: {command.Action}";
                        break;
                }
            }
            catch (Exception ex)
            {
                status = "failed";
                message = ex.Message;
            }

            await _audit.LogAsync("CENTRAL_COMMAND_RECEIVED", $"Worker command {command.Action} ({command.Id}) -> {status}", ct: ct);
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
