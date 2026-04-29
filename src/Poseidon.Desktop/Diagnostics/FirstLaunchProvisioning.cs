using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Poseidon.Desktop.Diagnostics;

public enum FirstLaunchProvisioningAction
{
    Proceed,
    ShowWizard,
    Recovery
}

public sealed record FirstLaunchProvisioningDecision(
    FirstLaunchProvisioningAction Action,
    string Reason);

public static class FirstLaunchProvisioning
{
    public static FirstLaunchProvisioningDecision Evaluate(DataPaths paths, IConfiguration configuration)
    {
        var hasUserConfig = !string.IsNullOrWhiteSpace(paths.UserConfigPath) && File.Exists(paths.UserConfigPath);
        var userConfigStatus = ValidateUserConfig(paths);
        if (userConfigStatus is not null)
            return userConfigStatus;

        var llmProvider = (configuration["Llm:Provider"] ?? "llamasharp").Trim().ToLowerInvariant();
        var embeddingProvider = (configuration["Embedding:Provider"] ?? "onnx").Trim().ToLowerInvariant();

        if (llmProvider is not ("llamasharp" or "ollama"))
            return Recovery($"Invalid LLM provider: {llmProvider}");

        if (embeddingProvider is not ("onnx" or "ollama"))
            return Recovery($"Invalid embedding provider: {embeddingProvider}");

        var missingLocalRequirements = new List<string>();

        if (llmProvider == "llamasharp" && !File.Exists(ModelPathResolver.ResolveLlmPath(configuration, paths)))
            missingLocalRequirements.Add("local LLM model");

        if (embeddingProvider == "onnx" && !File.Exists(ModelPathResolver.ResolveEmbeddingPath(configuration, paths)))
            missingLocalRequirements.Add("local embedding model");

        if (missingLocalRequirements.Count > 0)
        {
            var reason = $"Missing required {string.Join(" and ", missingLocalRequirements)}.";
            return hasUserConfig
                ? Recovery(reason)
                : new FirstLaunchProvisioningDecision(FirstLaunchProvisioningAction.ShowWizard, reason);
        }

        if (llmProvider == "ollama" || embeddingProvider == "ollama")
        {
            if (!HasValidOllamaEndpoint(configuration))
                return Recovery("Invalid Ollama endpoint.");

            if (llmProvider == "ollama" && string.IsNullOrWhiteSpace(configuration["Ollama:Model"]))
                return Recovery("Missing Ollama LLM model name.");

            if (embeddingProvider == "ollama" && string.IsNullOrWhiteSpace(configuration["Embedding:Model"]))
                return Recovery("Missing Ollama embedding model name.");
        }

        return new FirstLaunchProvisioningDecision(
            FirstLaunchProvisioningAction.Proceed,
            "Configured provider requirements are available.");
    }

    private static bool HasValidOllamaEndpoint(IConfiguration configuration)
    {
        return Uri.TryCreate(configuration["Ollama:Url"] ?? "", UriKind.Absolute, out var uri) &&
               uri.Scheme is "http" or "https";
    }

    private static FirstLaunchProvisioningDecision? ValidateUserConfig(DataPaths paths)
    {
        if (string.IsNullOrWhiteSpace(paths.UserConfigPath) || !File.Exists(paths.UserConfigPath))
            return null;

        try
        {
            using var stream = File.OpenRead(paths.UserConfigPath);
            using var _ = JsonDocument.Parse(stream);
            return null;
        }
        catch (Exception ex)
        {
            return Recovery($"User configuration is invalid: {ex.Message}");
        }
    }

    private static FirstLaunchProvisioningDecision Recovery(string reason)
        => new(FirstLaunchProvisioningAction.Recovery, reason);
}
