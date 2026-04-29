using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Poseidon.Ingestion.Embedding;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

namespace Poseidon.UnitTests.Ingestion;

public sealed class OllamaEmbeddingServiceTests : IDisposable
{
    private readonly Mock<HttpMessageHandler> _handler = new();
    private readonly HttpClient _httpClient;
    private readonly Mock<ILogger<OllamaEmbeddingService>> _logger = new();
    private const string Model = "nomic-embed-text";
    private const int Dimension = 4;

    public OllamaEmbeddingServiceTests()
    {
        _httpClient = new HttpClient(_handler.Object)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };
    }

    public void Dispose() => _httpClient.Dispose();

    private OllamaEmbeddingService CreateSut(int dimension = Dimension) =>
        new(_httpClient, Model, _logger.Object, dimension);

    private void SetupEmbedResponse(float[][] embeddings)
    {
        var body = JsonSerializer.Serialize(new { embeddings });

        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private void SetupEmbedResponseSequence(params float[][] embeddings)
    {
        var seq = _handler.Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());

        foreach (var emb in embeddings)
        {
            var body = JsonSerializer.Serialize(new { embeddings = new[] { emb } });
            seq = seq.ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    // ─── EmbedAsync ──────────────────────────────────────────────

    [Fact]
    public async Task EmbedAsync_Success_ReturnsEmbeddingVector()
    {
        var expected = new[] { 0.1f, 0.2f, 0.3f, 0.4f };
        SetupEmbedResponse([expected]);

        var sut = CreateSut();
        var result = await sut.EmbedAsync("مرحبا");

        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task EmbedAsync_SendsToCorrectEndpoint()
    {
        HttpRequestMessage? captured = null;
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((r, _) =>
            {
                captured = r;
                var body = JsonSerializer.Serialize(new { embeddings = new[] { new float[] { 1f, 2f, 3f, 4f } } });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            });

        var sut = CreateSut();
        await sut.EmbedAsync("test");

        captured.Should().NotBeNull();
        captured!.RequestUri!.PathAndQuery.Should().Be("/api/embed");
        captured.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task EmbedAsync_SendsModelInRequest()
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
                var body = JsonSerializer.Serialize(new { embeddings = new[] { new float[] { 1f, 2f, 3f, 4f } } });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });

        var sut = CreateSut();
        await sut.EmbedAsync("test");

        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("nomic-embed-text");
    }

    [Fact]
    public async Task EmbedAsync_EmptyEmbeddings_Throws()
    {
        SetupEmbedResponse([]);

        var sut = CreateSut();

        var act = () => sut.EmbedAsync("test");
        await act.Should().ThrowAsync<InvalidOperationException>()
              .WithMessage("*empty*");
    }

    [Fact]
    public async Task EmbedAsync_NullEmbeddings_Throws()
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });

        var sut = CreateSut();

        var act = () => sut.EmbedAsync("test");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task EmbedAsync_HttpError_Throws()
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var sut = CreateSut();

        var act = () => sut.EmbedAsync("test");
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task EmbedAsync_NetworkError_Throws()
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var sut = CreateSut();

        var act = () => sut.EmbedAsync("test");
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task EmbedAsync_DimensionMismatch_UpdatesDimension()
    {
        // SUT configured with dimension=4 but response returns 3-dim vector
        var vector = new[] { 0.1f, 0.2f, 0.3f };
        SetupEmbedResponse([vector]);

        var sut = CreateSut(4);
        sut.EmbeddingDimension.Should().Be(4);

        await sut.EmbedAsync("test");

        sut.EmbeddingDimension.Should().Be(3);
    }

    [Fact]
    public async Task EmbedAsync_NormalizesArabicText()
    {
        // Arabic normalizer strips diacritics & normalizes alef forms
        string? capturedBody = null;
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (r, _) =>
            {
                capturedBody = await r.Content!.ReadAsStringAsync();
                var body = JsonSerializer.Serialize(new { embeddings = new[] { new float[] { 1f } } });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            });

        var sut = CreateSut(1);
        // Text with diacritics: أَلْمَحْكَمَة
        await sut.EmbedAsync("أَلْمَحْكَمَة");

        // The normalized text should not contain diacritics
        capturedBody.Should().NotBeNull();
        // Diacritics (Fatha \u064E, Sukun \u0652, etc.) should be removed
        capturedBody.Should().NotContain("\u064E");
        capturedBody.Should().NotContain("\u0652");
    }

    // ─── EmbedBatchAsync ────────────────────────────────────────

    [Fact]
    public async Task EmbedBatchAsync_MultipleTexts_ReturnsAll()
    {
        var v1 = new[] { 1f, 2f, 3f, 4f };
        var v2 = new[] { 5f, 6f, 7f, 8f };
        SetupEmbedResponseSequence(v1, v2);

        var sut = CreateSut();
        var results = await sut.EmbedBatchAsync(["text1", "text2"]);

        results.Should().HaveCount(2);
        results[0].Should().BeEquivalentTo(v1);
        results[1].Should().BeEquivalentTo(v2);
    }

    [Fact]
    public async Task EmbedBatchAsync_EmptyList_ReturnsEmpty()
    {
        var sut = CreateSut();
        var results = await sut.EmbedBatchAsync([]);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task EmbedBatchAsync_SingleItem_ReturnsOne()
    {
        var v1 = new[] { 1f, 2f, 3f, 4f };
        SetupEmbedResponse([v1]);

        var sut = CreateSut();
        var results = await sut.EmbedBatchAsync(["only one"]);

        results.Should().HaveCount(1);
        results[0].Should().BeEquivalentTo(v1);
    }

    // ─── EmbeddingDimension ─────────────────────────────────────

    [Fact]
    public void EmbeddingDimension_ReturnsConfiguredValue()
    {
        var sut = CreateSut(768);
        sut.EmbeddingDimension.Should().Be(768);
    }

    [Fact]
    public void EmbeddingDimension_Default768()
    {
        // Default constructor value
        var sut = new OllamaEmbeddingService(_httpClient, Model, _logger.Object);
        sut.EmbeddingDimension.Should().Be(768);
    }

    // ─── Dispose ────────────────────────────────────────────────

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var handler = new Mock<HttpMessageHandler>();
        var client = new HttpClient(handler.Object) { BaseAddress = new Uri("http://localhost:11434") };
        var sut = new OllamaEmbeddingService(client, Model, _logger.Object);

        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }
}


