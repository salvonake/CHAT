using System.Net.Http;
using System.Text.Json;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Poseidon.Desktop.Services;
using Poseidon.Domain.Interfaces;

namespace Poseidon.Desktop.Diagnostics;

public sealed class SystemHealthService
{
    private readonly IConfiguration _configuration;
    private readonly DataPaths _paths;
    private readonly ILlmService _llm;
    private readonly IEmbeddingService _embedding;
    private readonly IVectorStore _vectorStore;
    private readonly IEncryptionService _encryption;
    private readonly ModelIntegrityService? _modelIntegrity;
    private readonly StartupModeController _startupModeController;
    private readonly ILogger<SystemHealthService> _logger;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private SystemHealthSnapshot? _lastSnapshot;

    public SystemHealthService(
        IConfiguration configuration,
        DataPaths paths,
        ILlmService llm,
        IEmbeddingService embedding,
        IVectorStore vectorStore,
        IEncryptionService encryption,
        StartupModeController startupModeController,
        ILogger<SystemHealthService> logger,
        ModelIntegrityService? modelIntegrity = null)
    {
        _configuration = configuration;
        _paths = paths;
        _llm = llm;
        _embedding = embedding;
        _vectorStore = vectorStore;
        _encryption = encryption;
        _startupModeController = startupModeController;
        _logger = logger;
        _modelIntegrity = modelIntegrity;
    }

    public event EventHandler<SystemHealthSnapshot>? HealthChanged;

    public SystemHealthSnapshot? LastSnapshot => _lastSnapshot;

    public async Task<SystemHealthSnapshot> CheckAllAsync(CancellationToken ct = default)
    {
        await _checkLock.WaitAsync(ct);
        try
        {
            var results = new List<HealthCheckResult>
            {
                CheckConfiguration(),
                await CheckLlmAsync(ct),
                await CheckEmbeddingsAsync(ct),
                await CheckVectorStoreAsync(ct),
                CheckEncryption()
            };

            EnsureErrorsHaveFixActions(results);

            var decision = _startupModeController.Evaluate(results);
            var snapshot = new SystemHealthSnapshot(
                decision.Mode,
                decision.CanAskQuestions,
                DateTimeOffset.Now,
                results);

            _lastSnapshot = snapshot;
            HealthChanged?.Invoke(this, snapshot);
            _logger.LogInformation(
                "System health checked. Mode={Mode}, CanAsk={CanAsk}, Errors={Errors}, Warnings={Warnings}",
                snapshot.Mode,
                snapshot.CanAskQuestions,
                results.Count(r => r.Status == HealthStatus.Error),
                results.Count(r => r.Status == HealthStatus.Warning));

            return snapshot;
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private HealthCheckResult CheckConfiguration()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_paths.UserConfigPath) && File.Exists(_paths.UserConfigPath))
            {
                using var stream = File.OpenRead(_paths.UserConfigPath);
                using var _ = JsonDocument.Parse(stream);
            }

            var strictMode = _configuration.GetValue("Retrieval:StrictMode", true);
            if (!strictMode)
            {
                return Error(
                    "Configuration",
                    "Le mode strict est desactive dans la configuration. Le systeme juridique exige le mode strict.",
                    FixAction.RepairConfig,
                    ("ConfigPath", ResolveUserConfigPath()));
            }

            var llmProvider = (_configuration["Llm:Provider"] ?? "llamasharp").Trim().ToLowerInvariant();
            var embeddingProvider = (_configuration["Embedding:Provider"] ?? "onnx").Trim().ToLowerInvariant();
            if (llmProvider is not ("llamasharp" or "ollama"))
            {
                return Error(
                    "Configuration",
                    $"Fournisseur LLM invalide: {llmProvider}",
                    FixAction.RepairConfig,
                    ("ConfigPath", ResolveUserConfigPath()));
            }

            if (embeddingProvider is not ("onnx" or "ollama"))
            {
                return Error(
                    "Configuration",
                    $"Fournisseur d'embedding invalide: {embeddingProvider}",
                    FixAction.RepairConfig,
                    ("ConfigPath", ResolveUserConfigPath()));
            }

            if ((llmProvider == "ollama" || embeddingProvider == "ollama") &&
                !Uri.TryCreate(_configuration["Ollama:Url"] ?? "", UriKind.Absolute, out _))
            {
                return Error(
                    "Configuration",
                    "L'endpoint Ollama est invalide.",
                    FixAction.EditOllamaEndpoint,
                    ("OllamaUrl", _configuration["Ollama:Url"] ?? ""));
            }

            return Ok(
                "Configuration",
                "Configuration valide.",
                ("ConfigPath", ResolveUserConfigPath()));
        }
        catch (Exception ex)
        {
            return Error(
                "Configuration",
                $"Configuration invalide: {ex.Message}",
                FixAction.RepairConfig,
                ("ConfigPath", ResolveUserConfigPath()));
        }
    }

    private async Task<HealthCheckResult> CheckLlmAsync(CancellationToken ct)
    {
        var provider = (_configuration["Llm:Provider"] ?? "llamasharp").Trim().ToLowerInvariant();
        if (provider == "ollama")
        {
            var model = _configuration["Ollama:Model"] ?? "";
            var ollama = await CheckOllamaModelAvailableAsync(model, ct);
            if (!ollama.Connected)
            {
                return Error(
                    "LLM",
                    "Service Ollama indisponible pour le LLM.",
                    FixAction.RetryService,
                    ("Provider", "ollama"),
                    ("OllamaUrl", _configuration["Ollama:Url"] ?? ""));
            }

            if (!ollama.ModelAvailable)
            {
                return Error(
                    "LLM",
                    $"Modele Ollama LLM indisponible: {model}",
                    FixAction.RetryService,
                    ("Provider", "ollama"),
                    ("Model", model));
            }

            return Ok(
                "LLM",
                "Service LLM Ollama disponible.",
                ("Provider", "ollama"),
                ("Model", model));
        }

        var modelPath = ReloadableLlmService.ResolveLlmPath(_configuration, _paths);
        var modelIntegritySatisfied = false;
        if (_modelIntegrity is not null)
        {
            if (!_modelIntegrity.LlmModelExists)
            {
                return Error(
                    "LLM",
                    _modelIntegrity.LlmError ?? $"Modele LLM introuvable: {modelPath}",
                    FixAction.EditLlmPath,
                    ("Provider", "llamasharp"),
                    ("ModelPath", modelPath));
            }

            if (!_modelIntegrity.LlmModelValid)
            {
                return Error(
                    "LLM",
                    _modelIntegrity.LlmError ?? "Le modele LLM a echoue la verification d'integrite.",
                    FixAction.EditLlmPath,
                    ("Provider", "llamasharp"),
                    ("ModelPath", modelPath));
            }

            modelIntegritySatisfied = true;
        }

        if (!modelIntegritySatisfied && !File.Exists(modelPath))
        {
            return Error(
                "LLM",
                $"Modele LLM introuvable: {modelPath}",
                FixAction.EditLlmPath,
                ("Provider", "llamasharp"),
                ("ModelPath", modelPath));
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var available = await _llm.IsAvailableAsync(timeout.Token);
            return available
                ? Ok("LLM", "Modele LLM disponible.", ("Provider", "llamasharp"), ("ModelPath", modelPath))
                : Error("LLM", "Le service LLM ne repond pas.", FixAction.RetryService, ("ModelPath", modelPath));
        }
        catch (Exception ex)
        {
            return Error(
                "LLM",
                $"Erreur LLM: {ex.Message}",
                FixAction.RetryService,
                ("ModelPath", modelPath),
                ("LastException", ex.Message));
        }
    }

    private async Task<HealthCheckResult> CheckEmbeddingsAsync(CancellationToken ct)
    {
        var provider = (_configuration["Embedding:Provider"] ?? "onnx").Trim().ToLowerInvariant();
        if (provider == "ollama")
        {
            var model = _configuration["Embedding:Model"] ?? "";
            var ollama = await CheckOllamaModelAvailableAsync(model, ct);
            if (!ollama.Connected)
            {
                return Error(
                    "Embeddings",
                    "Service Ollama indisponible pour les embeddings.",
                    FixAction.RetryService,
                    ("Provider", "ollama"),
                    ("OllamaUrl", _configuration["Ollama:Url"] ?? ""));
            }

            if (!ollama.ModelAvailable)
            {
                return Error(
                    "Embeddings",
                    $"Modele Ollama d'embedding indisponible: {model}",
                    FixAction.RetryService,
                    ("Provider", "ollama"),
                    ("Model", model));
            }

            return Ok(
                "Embeddings",
                "Embeddings Ollama disponibles.",
                ("Provider", "ollama"),
                ("Model", model));
        }

        var embeddingPath = ReloadableEmbeddingService.ResolveEmbeddingPath(_configuration, _paths);
        var embeddingIntegritySatisfied = false;
        if (_modelIntegrity is not null)
        {
            if (!_modelIntegrity.EmbeddingModelExists)
            {
                return Error(
                    "Embeddings",
                    _modelIntegrity.EmbeddingError ?? $"Modele d'embedding introuvable: {embeddingPath}",
                    FixAction.EditEmbeddingPath,
                    ("Provider", "onnx"),
                    ("ModelPath", embeddingPath));
            }

            if (!_modelIntegrity.EmbeddingModelValid)
            {
                return Error(
                    "Embeddings",
                    _modelIntegrity.EmbeddingError ?? "Le modele d'embedding a echoue la verification d'integrite.",
                    FixAction.EditEmbeddingPath,
                    ("Provider", "onnx"),
                    ("ModelPath", embeddingPath));
            }

            embeddingIntegritySatisfied = true;
        }

        if (!embeddingIntegritySatisfied && !File.Exists(embeddingPath))
        {
            return Error(
                "Embeddings",
                $"Modele d'embedding introuvable: {embeddingPath}",
                FixAction.EditEmbeddingPath,
                ("Provider", "onnx"),
                ("ModelPath", embeddingPath));
        }

        return Ok(
            "Embeddings",
            $"Modele d'embedding disponible. Dimension: {_embedding.EmbeddingDimension}",
            ("Provider", "onnx"),
            ("ModelPath", embeddingPath));
    }

    private async Task<HealthCheckResult> CheckVectorStoreAsync(CancellationToken ct)
    {
        try
        {
            var health = await _vectorStore.GetHealthAsync(ct);
            if (!health.IsHealthy)
            {
                return Warning(
                    "Vector DB",
                    health.Status ?? health.Error ?? "Stockage vectoriel degrade.",
                    FixAction.RetryService,
                    ("VectorDbPath", _paths.VectorDbPath),
                    ("HnswIndexPath", _paths.HnswIndexPath));
            }

            return Ok(
                "Vector DB",
                $"Stockage vectoriel disponible ({health.VectorCount:N0} vecteurs).",
                ("VectorDbPath", _paths.VectorDbPath),
                ("HnswIndexPath", _paths.HnswIndexPath));
        }
        catch (Exception ex)
        {
            return Error(
                "Vector DB",
                $"Erreur du stockage vectoriel: {ex.Message}",
                FixAction.RetryService,
                ("VectorDbPath", _paths.VectorDbPath),
                ("HnswIndexPath", _paths.HnswIndexPath),
                ("LastException", ex.Message));
        }
    }

    private HealthCheckResult CheckEncryption()
    {
        var enabledInConfig = _configuration.GetValue("Security:EncryptionEnabled", false);
        if (enabledInConfig && !_encryption.IsEnabled)
        {
            return Error(
                "Encryption",
                "Le chiffrement est active mais aucune phrase secrete protegee n'est disponible.",
                FixAction.EnableEncryption);
        }

        if (!_encryption.IsEnabled)
        {
            return Warning(
                "Encryption",
                "Le chiffrement est desactive. Les donnees confidentielles ne sont pas protegees.",
                FixAction.EnableEncryption);
        }

        return Ok("Encryption", "Chiffrement actif.");
    }

    private async Task<(bool Connected, bool ModelAvailable)> CheckOllamaModelAvailableAsync(string modelName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return (false, false);

        try
        {
            var url = _configuration["Ollama:Url"] ?? "http://localhost:11434";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var http = new HttpClient { BaseAddress = new Uri(url) };
            using var response = await http.GetAsync("/api/tags", timeout.Token);
            if (!response.IsSuccessStatusCode)
                return (false, false);

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return (true, body.Contains(modelName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return (false, false);
        }
    }

    private string ResolveUserConfigPath()
    {
        return string.IsNullOrWhiteSpace(_paths.UserConfigPath)
            ? Path.Combine(_paths.DataDirectory, "appsettings.user.json")
            : _paths.UserConfigPath;
    }

    private static HealthCheckResult Ok(
        string component,
        string message,
        params (string Key, string Value)[] details)
    {
        return new HealthCheckResult(
            component,
            HealthStatus.OK,
            message,
            null,
            ToDetails(details));
    }

    private static HealthCheckResult Warning(
        string component,
        string message,
        FixAction? fixAction,
        params (string Key, string Value)[] details)
    {
        return new HealthCheckResult(
            component,
            HealthStatus.Warning,
            message,
            fixAction,
            ToDetails(details));
    }

    private static HealthCheckResult Error(
        string component,
        string message,
        FixAction fixAction,
        params (string Key, string Value)[] details)
    {
        return new HealthCheckResult(
            component,
            HealthStatus.Error,
            message,
            fixAction,
            ToDetails(details));
    }

    private static IReadOnlyDictionary<string, string>? ToDetails((string Key, string Value)[] details)
    {
        return details.Length == 0
            ? null
            : details.ToDictionary(d => d.Key, d => d.Value);
    }

    private static void EnsureErrorsHaveFixActions(IEnumerable<HealthCheckResult> results)
    {
        var broken = results.FirstOrDefault(r => r.Status == HealthStatus.Error && r.FixAction is null);
        if (broken is not null)
            throw new InvalidOperationException($"Health error '{broken.Component}' has no recovery action.");
    }
}
