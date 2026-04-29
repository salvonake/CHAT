using System.IO;
using Microsoft.Extensions.Configuration;

namespace Poseidon.Desktop;

public static class ModelPathResolver
{
    public static readonly string[] KnownLlmModelFileNames =
    [
        "qwen2.5-14b.Q5_K_M.gguf",
        "Qwen_Qwen3.5-9B-Q5_K_M.gguf",
        "Qwen3.5-9B-Q5_K_M.gguf",
        "model.gguf"
    ];

    public static string ResolveLlmPath(IConfiguration configuration, DataPaths paths)
    {
        var configured = configuration["Llm:ModelPath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return ExpandConfiguredPath(configured, paths);

        return ResolveModelPath(
            paths.ModelsDirectory,
            paths.InstalledModelsDirectory,
            KnownLlmModelFileNames,
            "*.gguf",
            Path.Combine(paths.ModelsDirectory, KnownLlmModelFileNames[0]));
    }

    public static string ResolveEmbeddingPath(IConfiguration configuration, DataPaths paths)
    {
        var configured = configuration["Embedding:OnnxModelPath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return ExpandConfiguredPath(configured, paths);

        return ResolveModelPath(
            paths.ModelsDirectory,
            paths.InstalledModelsDirectory,
            ["arabert.onnx", "model.onnx"],
            "*.onnx",
            Path.Combine(paths.ModelsDirectory, "arabert.onnx"));
    }

    public static bool HasRequiredLocalModels(IConfiguration configuration, DataPaths paths)
    {
        return File.Exists(ResolveLlmPath(configuration, paths)) &&
               File.Exists(ResolveEmbeddingPath(configuration, paths));
    }

    private static string ResolveModelPath(
        string userModelsDirectory,
        string installedModelsDirectory,
        IReadOnlyList<string> preferredNames,
        string searchPattern,
        string fallbackPath)
    {
        var userPreferred = FindPreferred(userModelsDirectory, preferredNames);
        if (userPreferred is not null)
            return userPreferred;

        var userDiscovered = FindFirst(userModelsDirectory, searchPattern);
        if (userDiscovered is not null)
            return userDiscovered;

        var installedPreferred = FindPreferred(installedModelsDirectory, preferredNames);
        if (installedPreferred is not null)
            return installedPreferred;

        var installedDiscovered = FindFirst(installedModelsDirectory, searchPattern);
        return installedDiscovered ?? fallbackPath;
    }

    private static string? FindPreferred(string directory, IReadOnlyList<string> preferredNames)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        return preferredNames
            .Select(name => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }

    private static string? FindFirst(string directory, string searchPattern)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        return Directory
            .GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string ExpandConfiguredPath(string configured, DataPaths paths)
    {
        var value = configured.Trim();
        if (!value.Contains("[INSTALLDIR]", StringComparison.OrdinalIgnoreCase))
            return value;

        var installDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(paths.InstalledModelsDirectory))
        {
            installDir = Directory.GetParent(paths.InstalledModelsDirectory)?.FullName ?? installDir;
        }

        installDir = Path.TrimEndingDirectorySeparator(installDir) + Path.DirectorySeparatorChar;
        return value.Replace("[INSTALLDIR]", installDir, StringComparison.OrdinalIgnoreCase);
    }
}
