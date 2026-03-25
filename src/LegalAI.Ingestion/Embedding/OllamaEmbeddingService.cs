using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LegalAI.Domain.Interfaces;
using LegalAI.Ingestion.Arabic;
using Microsoft.Extensions.Logging;

namespace LegalAI.Ingestion.Embedding;

/// <summary>
/// Embedding service using Ollama's embedding endpoint.
/// Simpler alternative to ONNX — uses whatever embedding model is loaded in Ollama.
/// Recommended model: nomic-embed-text or mxbai-embed-large.
/// </summary>
public sealed class OllamaEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly ILogger<OllamaEmbeddingService> _logger;
    private int _embeddingDimension;

    public int EmbeddingDimension => _embeddingDimension;

    public OllamaEmbeddingService(
        HttpClient httpClient,
        string model,
        ILogger<OllamaEmbeddingService> logger,
        int embeddingDimension = 768)
    {
        _httpClient = httpClient;
        _model = model;
        _logger = logger;
        _embeddingDimension = embeddingDimension;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var normalized = ArabicNormalizer.Normalize(text);

        var request = new OllamaEmbedRequest
        {
            Model = _model,
            Input = normalized
        };

        var response = await _httpClient.PostAsJsonAsync("/api/embed", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(ct);

        if (result?.Embeddings is null || result.Embeddings.Count == 0)
            throw new InvalidOperationException("Ollama returned empty embeddings");

        var embedding = result.Embeddings[0];
        if (_embeddingDimension != embedding.Length)
        {
            _embeddingDimension = embedding.Length;
            _logger.LogInformation("Updated embedding dimension to {Dim}", _embeddingDimension);
        }

        return embedding;
    }

    public async Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        // Ollama doesn't support batch embedding natively, so process sequentially
        // with some parallelism
        var results = new float[texts.Count][];

        // Process in small parallel batches
        const int parallelism = 4;
        var semaphore = new SemaphoreSlim(parallelism);
        var tasks = texts.Select(async (text, index) =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                results[index] = await EmbedAsync(text, ct);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed class OllamaEmbedRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("input")]
        public required string Input { get; init; }
    }

    private sealed class OllamaEmbedResponse
    {
        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("embeddings")]
        public List<float[]>? Embeddings { get; init; }
    }
}
