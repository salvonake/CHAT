using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Poseidon.Desktop.Services;

namespace Poseidon.Desktop.Diagnostics;

public sealed class RuntimeConfigurationService : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly DataPaths _paths;
    private readonly IEnumerable<IReloadableRuntimeService> _reloadableServices;
    private readonly ModelIntegrityService? _modelIntegrity;
    private readonly ILogger<RuntimeConfigurationService> _logger;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly object _watchLock = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private DateTime _suppressWatcherUntilUtc = DateTime.MinValue;
    private bool _started;

    public RuntimeConfigurationService(
        IConfiguration configuration,
        DataPaths paths,
        IEnumerable<IReloadableRuntimeService> reloadableServices,
        ILogger<RuntimeConfigurationService> logger,
        ModelIntegrityService? modelIntegrity = null)
    {
        _configuration = configuration;
        _paths = paths;
        _reloadableServices = reloadableServices;
        _logger = logger;
        _modelIntegrity = modelIntegrity;
    }

    public event EventHandler<ConfigurationReloadedEventArgs>? ConfigurationReloaded;

    public string UserConfigPath => ResolveUserConfigPath();
    public ConfigurationReloadedEventArgs? LastReloadResult { get; private set; }

    public void Start()
    {
        if (_started)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(UserConfigPath)!);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(UserConfigPath)!, Path.GetFileName(UserConfigPath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnConfigFileChanged;
        _watcher.Created += OnConfigFileChanged;
        _watcher.Renamed += OnConfigFileChanged;
        _watcher.Error += (_, e) => _logger.LogError(e.GetException(), "Runtime config watcher failed");
        _started = true;
        _logger.LogInformation("Runtime configuration watcher started on {Path}", UserConfigPath);
    }

    public async Task SaveSettingAsync(
        string section,
        string key,
        object? value,
        CancellationToken ct = default)
    {
        var applied = await MutateAndReloadWithRollbackAsync(root =>
        {
            var sectionObject = EnsureSection(root, section);
            sectionObject[key] = ToJsonNode(value);
        }, "Configuration updated from recovery UI", ct);

        if (!applied)
            throw new InvalidOperationException("Le rechargement a echoue; la configuration precedente a ete restauree.");
    }

    public async Task SaveEncryptionSecretAsync(string passphrase, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
            throw new ArgumentException("Encryption passphrase cannot be empty.", nameof(passphrase));

        var protectedSecret = RuntimeSecretProtector.Protect(passphrase);
        var applied = await MutateAndReloadWithRollbackAsync(root =>
        {
            var security = EnsureSection(root, "Security");
            security["EncryptionEnabled"] = true;
            security["ProtectedPassphrase"] = protectedSecret;
            security["EncryptionPassphrase"] = "";
        }, "Encryption settings updated", ct);

        if (!applied)
            throw new InvalidOperationException("Le rechargement du chiffrement a echoue; la configuration precedente a ete restauree.");
    }

    public async Task RepairConfigAsync(CancellationToken ct = default)
    {
        var applied = await MutateAndReloadWithRollbackAsync(root =>
        {
            SetIfMissing(EnsureSection(root, "Ui"), "Culture", "fr-FR");
            SetIfMissing(EnsureSection(root, "Llm"), "Provider", "llamasharp");
            SetIfMissing(EnsureSection(root, "Embedding"), "Provider", "onnx");
            EnsureSection(root, "Retrieval")["StrictMode"] = true;
            SetIfMissing(EnsureSection(root, "Retrieval"), "EnableDualPassValidation", true);
            SetIfMissing(EnsureSection(root, "Security"), "EncryptionEnabled", false);
            SetIfMissing(EnsureSection(root, "Ingestion"), "WatchDirectory", _paths.WatchDirectory);
        }, "Configuration repaired", ct);

        if (!applied)
            throw new InvalidOperationException("La reparation a echoue; la configuration precedente a ete restauree.");
    }

    public async Task<bool> ReloadAsync(string reason = "Configuration reload requested", CancellationToken ct = default)
    {
        await _reloadLock.WaitAsync(ct);
        try
        {
            try
            {
                if (_configuration is IConfigurationRoot root)
                    root.Reload();

                if (_modelIntegrity is not null)
                    await _modelIntegrity.VerifyOnStartupAsync();

                foreach (var reloadable in _reloadableServices)
                    await reloadable.ReloadAsync(ct);

                _logger.LogInformation("Runtime configuration reloaded: {Reason}", reason);
                LastReloadResult = new ConfigurationReloadedEventArgs(true, reason);
                ConfigurationReloaded?.Invoke(this, LastReloadResult);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Runtime configuration reload failed: {Reason}", reason);
                LastReloadResult = new ConfigurationReloadedEventArgs(false, reason, ex);
                ConfigurationReloaded?.Invoke(this, LastReloadResult);
                return false;
            }
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private async Task<bool> MutateAndReloadWithRollbackAsync(
        Action<JsonObject> mutate,
        string reason,
        CancellationToken ct)
    {
        var previousJson = File.Exists(UserConfigPath)
            ? await File.ReadAllTextAsync(UserConfigPath, ct)
            : null;

        await MutateUserConfigAsync(mutate, ct);
        if (await ReloadAsync(reason, ct))
            return true;

        _logger.LogWarning("Restoring previous runtime configuration after failed reload: {Reason}", reason);
        _suppressWatcherUntilUtc = DateTime.UtcNow.AddSeconds(1);
        if (previousJson is null)
        {
            if (File.Exists(UserConfigPath))
                File.Delete(UserConfigPath);
        }
        else
        {
            await File.WriteAllTextAsync(UserConfigPath, previousJson, ct);
        }

        await ReloadAsync("Configuration rollback after failed reload", ct);
        return false;
    }

    public bool ValidateLlmPath(string path, out string error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Le chemin du modele LLM est vide.";
            return false;
        }

        if (!File.Exists(path))
        {
            error = "Le fichier du modele LLM est introuvable.";
            return false;
        }

        if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            error = "Le modele LLM doit etre un fichier .gguf.";
            return false;
        }

        error = "";
        return true;
    }

    public bool ValidateEmbeddingPath(string path, out string error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Le chemin du modele d'embedding est vide.";
            return false;
        }

        if (!File.Exists(path))
        {
            error = "Le fichier du modele d'embedding est introuvable.";
            return false;
        }

        if (!path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            error = "Le modele d'embedding doit etre un fichier .onnx.";
            return false;
        }

        error = "";
        return true;
    }

    public bool ValidateOllamaEndpoint(string url, out string error)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            error = "L'endpoint Ollama doit etre une URL HTTP valide.";
            return false;
        }

        error = "";
        return true;
    }

    private async Task MutateUserConfigAsync(Action<JsonObject> mutate, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserConfigPath)!);
        var root = await LoadUserConfigAsync(ct);
        mutate(root);

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var tempPath = UserConfigPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, ct);

        _suppressWatcherUntilUtc = DateTime.UtcNow.AddSeconds(1);
        File.Move(tempPath, UserConfigPath, overwrite: true);
    }

    private async Task<JsonObject> LoadUserConfigAsync(CancellationToken ct)
    {
        if (!File.Exists(UserConfigPath))
            return new JsonObject();

        await using var stream = File.OpenRead(UserConfigPath);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: ct);
        return node as JsonObject ?? new JsonObject();
    }

    private static JsonObject EnsureSection(JsonObject root, string section)
    {
        if (root[section] is JsonObject existing)
            return existing;

        var created = new JsonObject();
        root[section] = created;
        return created;
    }

    private static void SetIfMissing(JsonObject section, string key, object value)
    {
        if (section[key] is null)
            section[key] = ToJsonNode(value);
    }

    private static JsonNode? ToJsonNode(object? value)
    {
        return value switch
        {
            null => null,
            string s => JsonValue.Create(s),
            bool b => JsonValue.Create(b),
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            double d => JsonValue.Create(d),
            float f => JsonValue.Create(f),
            decimal m => JsonValue.Create(m),
            _ => JsonSerializer.SerializeToNode(value)
        };
    }

    private string ResolveUserConfigPath()
    {
        if (!string.IsNullOrWhiteSpace(_paths.UserConfigPath))
            return _paths.UserConfigPath;

        return Path.Combine(_paths.DataDirectory, "appsettings.user.json");
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        if (DateTime.UtcNow < _suppressWatcherUntilUtc)
            return;

        lock (_watchLock)
        {
            _debounceTimer ??= new Timer(_ =>
            {
                _ = ReloadAsync("Configuration file changed");
            });

            _debounceTimer.Change(TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
        _reloadLock.Dispose();
    }
}
