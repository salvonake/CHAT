using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LegalAI.Infrastructure.Llm;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

namespace LegalAI.UnitTests.Infrastructure;

public sealed class OllamaLlmServiceTests : IDisposable
{
    private readonly Mock<HttpMessageHandler> _handler = new();
    private readonly HttpClient _httpClient;
    private readonly Mock<ILogger<OllamaLlmService>> _logger = new();
    private readonly OllamaLlmService _sut;
    private const string Model = "llama3";

    public OllamaLlmServiceTests()
    {
        _httpClient = new HttpClient(_handler.Object)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };
        _sut = new OllamaLlmService(_httpClient, Model, _logger.Object);
    }

    public void Dispose() => _httpClient.Dispose();

    private void SetupResponse(HttpStatusCode status, object? body = null)
    {
        var json = body is not null
            ? JsonSerializer.Serialize(body)
            : "";

        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private void SetupChatResponse(
        string content = "إجابة",
        int promptEvalCount = 100,
        int evalCount = 50)
    {
        SetupResponse(HttpStatusCode.OK, new
        {
            message = new { role = "assistant", content },
            done = true,
            prompt_eval_count = promptEvalCount,
            eval_count = evalCount
        });
    }

    // ─── GenerateAsync ──────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_Success_ReturnsContent()
    {
        SetupChatResponse("الجواب هو...");

        var result = await _sut.GenerateAsync("system", "user prompt");

        result.Success.Should().BeTrue();
        result.Content.Should().Be("الجواب هو...");
    }

    [Fact]
    public async Task GenerateAsync_Success_ReturnsTokenCounts()
    {
        SetupChatResponse(promptEvalCount: 120, evalCount: 80);

        var result = await _sut.GenerateAsync("system", "user prompt");

        result.PromptTokens.Should().Be(120);
        result.CompletionTokens.Should().Be(80);
        result.TotalTokens.Should().Be(200);
    }

    [Fact]
    public async Task GenerateAsync_Success_MeasuresLatency()
    {
        SetupChatResponse();

        var result = await _sut.GenerateAsync("system", "user prompt");

        result.LatencyMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GenerateAsync_HttpError_ReturnsFailed()
    {
        SetupResponse(HttpStatusCode.InternalServerError);

        var result = await _sut.GenerateAsync("system", "user prompt");

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        result.Content.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateAsync_NullResponse_ReturnsFailed()
    {
        // Return valid HTTP but body that deserializes to null
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("null", Encoding.UTF8, "application/json")
            });

        var result = await _sut.GenerateAsync("system", "user prompt");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Empty response");
    }

    [Fact]
    public async Task GenerateAsync_Timeout_ReturnsFailed()
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("timeout"));

        var result = await _sut.GenerateAsync("system", "user prompt");

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GenerateAsync_NetworkError_ReturnsFailed()
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var result = await _sut.GenerateAsync("system", "user prompt");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("connection refused");
        result.LatencyMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GenerateAsync_SendsCorrectEndpoint()
    {
        HttpRequestMessage? capturedRequest = null;

        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        message = new { role = "assistant", content = "ok" },
                        done = true,
                        prompt_eval_count = 0,
                        eval_count = 0
                    }),
                    Encoding.UTF8, "application/json")
            });

        await _sut.GenerateAsync("system", "user");

        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestUri!.PathAndQuery.Should().Be("/api/chat");
        capturedRequest.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task GenerateAsync_SendsSystemAndUserMessages()
    {
        string? capturedBody = null;

        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (r, _) =>
            {
                capturedBody = await r.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new
                        {
                            message = new { role = "assistant", content = "ok" },
                            done = true,
                            prompt_eval_count = 0,
                            eval_count = 0
                        }),
                        Encoding.UTF8, "application/json")
                };
            });

        await _sut.GenerateAsync("my-system-prompt", "my-user-prompt");

        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("my-system-prompt");
        capturedBody.Should().Contain("my-user-prompt");
    }

    [Fact]
    public async Task GenerateAsync_ResponseWithNullMessage_ReturnsEmptyContent()
    {
        SetupResponse(HttpStatusCode.OK, new
        {
            message = (object?)null,
            done = true,
            prompt_eval_count = 0,
            eval_count = 0
        });

        var result = await _sut.GenerateAsync("system", "user");

        // The code checks result is null first, then result.Message?.Content ?? ""
        // Since the entire deserialized object is not null, but message is null
        result.Success.Should().BeTrue();
        result.Content.Should().BeEmpty();
    }

    // ─── StreamGenerateAsync ────────────────────────────────────

    [Fact]
    public async Task StreamGenerateAsync_YieldsTokens()
    {
        var lines = new[]
        {
            JsonSerializer.Serialize(new { message = new { role = "assistant", content = "مرحبا" }, done = false }),
            JsonSerializer.Serialize(new { message = new { role = "assistant", content = " عالم" }, done = false }),
            JsonSerializer.Serialize(new { message = new { role = "assistant", content = "" }, done = true })
        };

        var streamContent = string.Join("\n", lines);

        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(streamContent, Encoding.UTF8, "application/json")
            });

        var tokens = new List<string>();
        await foreach (var token in _sut.StreamGenerateAsync("system", "user"))
        {
            tokens.Add(token);
        }

        tokens.Should().Contain("مرحبا");
        tokens.Should().Contain(" عالم");
    }

    [Fact]
    public async Task StreamGenerateAsync_DoneTrue_StopsYielding()
    {
        var lines = new[]
        {
            JsonSerializer.Serialize(new { message = new { role = "assistant", content = "أ" }, done = false }),
            JsonSerializer.Serialize(new { message = new { role = "assistant", content = "ب" }, done = true }),
            JsonSerializer.Serialize(new { message = new { role = "assistant", content = "ج" }, done = false })
        };

        var streamContent = string.Join("\n", lines);

        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(streamContent, Encoding.UTF8, "application/json")
            });

        var tokens = new List<string>();
        await foreach (var token in _sut.StreamGenerateAsync("system", "user"))
        {
            tokens.Add(token);
        }

        tokens.Should().HaveCount(2);
        tokens.Should().NotContain("ج");
    }

    // ─── IsAvailableAsync ───────────────────────────────────────

    [Fact]
    public async Task IsAvailableAsync_HealthyOllama_ReturnsTrue()
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.PathAndQuery == "/api/tags"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });

        var result = await _sut.IsAvailableAsync();

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsAvailableAsync_ServerDown_ReturnsFalse()
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var result = await _sut.IsAvailableAsync();

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsAvailableAsync_ServerError_ReturnsFalse()
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await _sut.IsAvailableAsync();

        result.Should().BeFalse();
    }
}
