using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Poseidon.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Poseidon.Infrastructure.Llm;

/// <summary>
/// Ollama LLM service for local inference.
/// Connects to a running Ollama instance at the configured endpoint.
/// </summary>
public sealed class OllamaLlmService : ILlmService
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly ILogger<OllamaLlmService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public OllamaLlmService(
        HttpClient httpClient,
        string model,
        ILogger<OllamaLlmService> logger)
    {
        _httpClient = httpClient;
        _model = model;
        _logger = logger;
    }

    public async Task<LlmResponse> GenerateAsync(
        string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var request = new OllamaChatRequest
            {
                Model = _model,
                Messages =
                [
                    new OllamaMessage { Role = "system", Content = systemPrompt },
                    new OllamaMessage { Role = "user", Content = userPrompt }
                ],
                Stream = false,
                Options = new OllamaOptions
                {
                    Temperature = 0.1,  // Low temperature for factual legal responses
                    TopP = 0.9,
                    NumCtx = 8192       // Context window size
                }
            };

            var response = await _httpClient.PostAsJsonAsync("/api/chat", request, JsonOptions, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(JsonOptions, ct);
            sw.Stop();

            if (result is null)
            {
                return new LlmResponse
                {
                    Content = "",
                    Success = false,
                    Error = "Empty response from Ollama",
                    LatencyMs = sw.Elapsed.TotalMilliseconds
                };
            }

            return new LlmResponse
            {
                Content = result.Message?.Content ?? "",
                PromptTokens = result.PromptEvalCount,
                CompletionTokens = result.EvalCount,
                TotalTokens = result.PromptEvalCount + result.EvalCount,
                LatencyMs = sw.Elapsed.TotalMilliseconds,
                Success = true
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Ollama generation failed");
            return new LlmResponse
            {
                Content = "",
                Success = false,
                Error = ex.Message,
                LatencyMs = sw.Elapsed.TotalMilliseconds
            };
        }
    }

    public async IAsyncEnumerable<string> StreamGenerateAsync(
        string systemPrompt, string userPrompt,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new OllamaChatRequest
        {
            Model = _model,
            Messages =
            [
                new OllamaMessage { Role = "system", Content = systemPrompt },
                new OllamaMessage { Role = "user", Content = userPrompt }
            ],
            Stream = true,
            Options = new OllamaOptions
            {
                Temperature = 0.1,
                TopP = 0.9,
                NumCtx = 8192
            }
        };

        var jsonContent = JsonContent.Create(request, options: JsonOptions);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = jsonContent };

        using var response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;

            var chunk = JsonSerializer.Deserialize<OllamaChatResponse>(line, JsonOptions);
            if (chunk?.Message?.Content is not null)
            {
                yield return chunk.Message.Content;
            }

            if (chunk?.Done == true)
                yield break;
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // Ollama API models
    private sealed class OllamaChatRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("messages")]
        public required List<OllamaMessage> Messages { get; init; }

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }

        [JsonPropertyName("options")]
        public OllamaOptions? Options { get; init; }
    }

    private sealed class OllamaMessage
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("content")]
        public required string Content { get; init; }
    }

    private sealed class OllamaOptions
    {
        [JsonPropertyName("temperature")]
        public double Temperature { get; init; }

        [JsonPropertyName("top_p")]
        public double TopP { get; init; }

        [JsonPropertyName("num_ctx")]
        public int NumCtx { get; init; }
    }

    private sealed class OllamaChatResponse
    {
        [JsonPropertyName("message")]
        public OllamaMessage? Message { get; init; }

        [JsonPropertyName("done")]
        public bool Done { get; init; }

        [JsonPropertyName("prompt_eval_count")]
        public int PromptEvalCount { get; init; }

        [JsonPropertyName("eval_count")]
        public int EvalCount { get; init; }
    }
}

