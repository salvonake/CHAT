using System.Net.Sockets;

namespace Poseidon.IntegrationTests;

internal static class IntegrationTestGate
{
    private const string RunFlag = "POSEIDON_RUN_INTEGRATION";

    public static bool IsIntegrationEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RunFlag);
        return IsTrue(value);
    }

    public static string? TryGetOnnxModelPath()
    {
        var modelPath = Environment.GetEnvironmentVariable("POSEIDON_ONNX_MODEL_PATH");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            return null;
        }

        return modelPath;
    }

    public static (string host, int port) GetQdrantEndpoint()
    {
        var host = Environment.GetEnvironmentVariable("POSEIDON_QDRANT_HOST") ?? "127.0.0.1";
        var portValue = Environment.GetEnvironmentVariable("POSEIDON_QDRANT_PORT");
        var port = int.TryParse(portValue, out var parsed) ? parsed : 6334;
        return (host, port);
    }

    public static bool IsQdrantReachable(string host, int port)
    {
        using var client = new TcpClient();
        try
        {
            var connectTask = client.ConnectAsync(host, port);
            if (!connectTask.Wait(TimeSpan.FromSeconds(3)) || !client.Connected)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static bool IsTrue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
               || value.Equals("true", StringComparison.OrdinalIgnoreCase)
               || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}

