using System.Net.Http.Json;
using LegalAI.Application.Commands;
using LegalAI.Domain.Interfaces;
using MediatR;

namespace LegalAI.WorkerService;

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
                var recentJobs = await _jobs.GetRecentAsync(50, stoppingToken);
                var snapshot = _metrics.GetSnapshot();

                var heartbeat = new HeartbeatRequest
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

                var response = await client.PostAsJsonAsync("/api/instances/heartbeat", heartbeat, stoppingToken);
                response.EnsureSuccessStatusCode();

                await ProcessCommandsAsync(client, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to push worker heartbeat to central management.");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }

    private async Task ProcessCommandsAsync(HttpClient client, CancellationToken ct)
    {
        var commands = await client.GetFromJsonAsync<List<FleetCommand>>(
            $"/api/instances/{_instance.InstanceId}/commands/worker",
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
