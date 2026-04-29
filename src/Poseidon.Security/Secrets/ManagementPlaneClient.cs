using System.Net.Http.Json;

namespace Poseidon.Security.Secrets;

public sealed class ManagementPlaneHeartbeatRequest
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

public sealed class ManagementPlaneFleetCommand
{
    public required string Id { get; init; }
    public required string Action { get; init; }
    public string? Payload { get; init; }
}

public sealed class ManagementPlaneCommandAck
{
    public required string Status { get; init; }
    public string? Message { get; init; }
}

public static class ManagementPlaneClient
{
    public static async Task<HttpResponseMessage> SendSignedJsonAsync<T>(
        HttpClient client,
        HttpMethod method,
        string path,
        T payload,
        string agentKey,
        string keyVersion,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload)
        };

        ManagementPlaneSecurity.Sign(request, agentKey, keyVersion, "agent");
        return await client.SendAsync(request, ct);
    }

    public static async Task<T?> GetSignedJsonAsync<T>(
        HttpClient client,
        string path,
        string agentKey,
        string keyVersion,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        ManagementPlaneSecurity.Sign(request, agentKey, keyVersion, "agent");

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }
}
