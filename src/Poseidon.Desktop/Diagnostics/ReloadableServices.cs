using System.Net.Http;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Poseidon.Domain.Interfaces;
using Poseidon.Security.Configuration;
using Poseidon.Infrastructure.Llm;
using Poseidon.Ingestion.Embedding;
using Poseidon.Security.Encryption;
using Poseidon.Security.Secrets;

namespace Poseidon.Desktop.Diagnostics;

public interface IReloadableRuntimeService
{
    Task ReloadAsync(CancellationToken ct = default);
}

public sealed class ReloadableLlmService : ILlmService, IReloadableRuntimeService, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly DataPaths _paths;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ReloadableLlmService> _logger;
    private ILlmService _inner;

    public ReloadableLlmService(
        IConfiguration configuration,
        DataPaths paths,
        ILoggerFactory loggerFactory,
        ILogger<ReloadableLlmService> logger)
    {
        _configuration = configuration;
        _paths = paths;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _inner = CreateInner();
    }

    public Task<LlmResponse> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken ct = default)
    {
        return Volatile.Read(ref _inner).GenerateAsync(systemPrompt, userPrompt, ct);
    }

    public IAsyncEnumerable<string> StreamGenerateAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken ct = default)
    {
        return Volatile.Read(ref _inner).StreamGenerateAsync(systemPrompt, userPrompt, ct);
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        return Volatile.Read(ref _inner).IsAvailableAsync(ct);
    }

    public Task ReloadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var replacement = CreateInner();
        var old = Interlocked.Exchange(ref _inner, replacement);
        DisposeAfterGracePeriod(old);
        _logger.LogInformation("LLM service reloaded without rebuilding the host");
        return Task.CompletedTask;
    }

    private ILlmService CreateInner()
    {
        try
        {
            var provider = (_configuration["Llm:Provider"] ?? "llamasharp").Trim().ToLowerInvariant();
            if (provider == "ollama")
            {
                var url = _configuration["Ollama:Url"] ?? "http://localhost:11434";
                var http = new HttpClient { BaseAddress = new Uri(url) };
                return new OllamaLlmService(
                    http,
                    _configuration["Ollama:Model"] ?? "qwen2.5:14b",
                    _loggerFactory.CreateLogger<OllamaLlmService>());
            }

            var modelPath = ResolveLlmPath(_configuration, _paths);
            return new LLamaSharpLlmService(
                modelPath,
                _configuration.GetValue("Llm:GpuLayers", -1),
                _configuration.GetValue("Llm:ContextSize", 8192),
                _loggerFactory.CreateLogger<LLamaSharpLlmService>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create LLM service");
            return new UnavailableLlmService(ex.Message);
        }
    }

    internal static string ResolveLlmPath(IConfiguration configuration, DataPaths paths)
    {
        return ModelPathResolver.ResolveLlmPath(configuration, paths);
    }

    private static void DisposeAfterGracePeriod(object? old)
    {
        if (old is not IDisposable disposable)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(2));
            disposable.Dispose();
        });
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _inner) is IDisposable disposable)
            disposable.Dispose();
    }
}

public sealed class ReloadableEmbeddingService : IEmbeddingService, IReloadableRuntimeService, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly DataPaths _paths;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ReloadableEmbeddingService> _logger;
    private IEmbeddingService _inner;

    public ReloadableEmbeddingService(
        IConfiguration configuration,
        DataPaths paths,
        ILoggerFactory loggerFactory,
        ILogger<ReloadableEmbeddingService> logger)
    {
        _configuration = configuration;
        _paths = paths;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _inner = CreateInner();
    }

    public int EmbeddingDimension => Volatile.Read(ref _inner).EmbeddingDimension;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        return Volatile.Read(ref _inner).EmbedAsync(text, ct);
    }

    public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        return Volatile.Read(ref _inner).EmbedBatchAsync(texts, ct);
    }

    public Task ReloadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var replacement = CreateInner();
        var old = Interlocked.Exchange(ref _inner, replacement);
        DisposeAfterGracePeriod(old);
        _logger.LogInformation("Embedding service reloaded without rebuilding the host");
        return Task.CompletedTask;
    }

    private IEmbeddingService CreateInner()
    {
        try
        {
            var provider = (_configuration["Embedding:Provider"] ?? "onnx").Trim().ToLowerInvariant();
            var dimension = _configuration.GetValue("Embedding:Dimension", 768);
            if (provider == "ollama")
            {
                var url = _configuration["Ollama:Url"] ?? "http://localhost:11434";
                var http = new HttpClient { BaseAddress = new Uri(url) };
                return new OllamaEmbeddingService(
                    http,
                    _configuration["Embedding:Model"] ?? "nomic-embed-text",
                    _loggerFactory.CreateLogger<OllamaEmbeddingService>(),
                    dimension);
            }

            var onnxPath = ResolveEmbeddingPath(_configuration, _paths);
            if (!File.Exists(onnxPath))
            {
                _logger.LogWarning("Embedding model is unavailable at {Path}", onnxPath);
                return new UnavailableEmbeddingService(dimension, $"Embedding model not found: {onnxPath}");
            }

            var vocabPath = _configuration["Embedding:VocabPath"];
            return new OnnxArabicEmbeddingService(
                onnxPath,
                _loggerFactory.CreateLogger<OnnxArabicEmbeddingService>(),
                string.IsNullOrWhiteSpace(vocabPath) ? null : vocabPath.Trim(),
                dimension);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create embedding service");
            return new UnavailableEmbeddingService(
                _configuration.GetValue("Embedding:Dimension", 768),
                ex.Message);
        }
    }

    internal static string ResolveEmbeddingPath(IConfiguration configuration, DataPaths paths)
    {
        return ModelPathResolver.ResolveEmbeddingPath(configuration, paths);
    }

    private static void DisposeAfterGracePeriod(object? old)
    {
        if (old is not IDisposable disposable)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(2));
            disposable.Dispose();
        });
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _inner) is IDisposable disposable)
            disposable.Dispose();
    }
}

public sealed class ReloadableEncryptionService : IEncryptionService, IReloadableRuntimeService
{
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ReloadableEncryptionService> _logger;
    private IEncryptionService _inner;

    public ReloadableEncryptionService(
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        ILogger<ReloadableEncryptionService> logger)
    {
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _inner = CreateInner();
    }

    public bool IsEnabled => Volatile.Read(ref _inner).IsEnabled;

    public byte[] Encrypt(byte[] plaintext, EncryptionPurpose purpose = EncryptionPurpose.VectorData)
        => Volatile.Read(ref _inner).Encrypt(plaintext, purpose);

    public byte[] Decrypt(byte[] ciphertext, EncryptionPurpose purpose = EncryptionPurpose.VectorData)
        => Volatile.Read(ref _inner).Decrypt(ciphertext, purpose);

    public string EncryptString(string plaintext, EncryptionPurpose purpose = EncryptionPurpose.VectorData)
        => Volatile.Read(ref _inner).EncryptString(plaintext, purpose);

    public string DecryptString(string ciphertext, EncryptionPurpose purpose = EncryptionPurpose.VectorData)
        => Volatile.Read(ref _inner).DecryptString(ciphertext, purpose);

    public byte[] ComputeHmac(byte[] data)
        => Volatile.Read(ref _inner).ComputeHmac(data);

    public bool VerifyHmac(byte[] data, byte[] hmac)
        => Volatile.Read(ref _inner).VerifyHmac(data, hmac);

    public Task ReloadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var replacement = CreateInner();
        Interlocked.Exchange(ref _inner, replacement);
        _logger.LogInformation("Encryption service reloaded without rebuilding the host");
        return Task.CompletedTask;
    }

    private IEncryptionService CreateInner()
    {
        var enabled = _configuration.GetValue("Security:EncryptionEnabled", false);
        string? passphrase = null;
        try
        {
            passphrase = ConfigurationSecretResolver.ResolveRequiredSecret(
                _configuration,
                SecurityValidationContext.FromConfiguration(_configuration),
                "Security:EncryptionPassphrase",
                "Security:EncryptionPassphraseRef",
                32).Value;
        }
        catch (Exception ex)
        {
            var protectedSecret = RuntimeSecretProtector.TryUnprotect(_configuration["Security:ProtectedPassphrase"]);
            if (!string.IsNullOrWhiteSpace(protectedSecret))
            {
                _logger.LogWarning(
                    "Legacy user-scoped protected encryption passphrase detected. Migrate to Security:EncryptionPassphraseRef for production deployment.");
                passphrase = protectedSecret;
            }
            else
            {
                _logger.LogError(ex, "Encryption secret could not be resolved from protected storage.");
            }
        }

        return new AesGcmEncryptionService(
            passphrase,
            _loggerFactory.CreateLogger<AesGcmEncryptionService>(),
            enabled);
    }
}

internal sealed class UnavailableLlmService : ILlmService
{
    private readonly string _reason;

    public UnavailableLlmService(string reason)
    {
        _reason = reason;
    }

    public Task<LlmResponse> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken ct = default)
    {
        return Task.FromResult(new LlmResponse
        {
            Content = "",
            Success = false,
            Error = _reason
        });
    }

    public async IAsyncEnumerable<string> StreamGenerateAsync(
        string systemPrompt,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield return _reason;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(false);
}

internal sealed class UnavailableEmbeddingService : IEmbeddingService
{
    private readonly string _reason;

    public UnavailableEmbeddingService(int embeddingDimension, string reason)
    {
        EmbeddingDimension = embeddingDimension;
        _reason = reason;
    }

    public int EmbeddingDimension { get; }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Task.FromException<float[]>(new InvalidOperationException(_reason));

    public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        => Task.FromException<float[][]>(new InvalidOperationException(_reason));
}
