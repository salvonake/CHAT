namespace LegalAI.Domain.Interfaces;

/// <summary>
/// Local LLM service for generating evidence-constrained answers.
/// </summary>
public interface ILlmService
{
    /// <summary>
    /// Generates a response given a system prompt and context-enriched user prompt.
    /// </summary>
    Task<LlmResponse> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken ct = default);

    /// <summary>
    /// Streams a response token by token.
    /// </summary>
    IAsyncEnumerable<string> StreamGenerateAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken ct = default);

    /// <summary>
    /// Checks if the LLM service is available.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

public sealed class LlmResponse
{
    public required string Content { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }
    public double LatencyMs { get; init; }
    public bool Success { get; init; } = true;
    public string? Error { get; init; }
}
