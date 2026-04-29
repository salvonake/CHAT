using FluentAssertions;
using Poseidon.Ingestion.Embedding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Poseidon.IntegrationTests;

public sealed class OnnxArabicEmbeddingServiceIntegrationTests
{
    private readonly string? _modelPath;
    private readonly bool _enabled;

    public OnnxArabicEmbeddingServiceIntegrationTests()
    {
        _enabled = IntegrationTestGate.IsIntegrationEnabled();
        _modelPath = IntegrationTestGate.TryGetOnnxModelPath();
    }

    [Fact]
    public void Constructor_WithValidModel_DoesNotThrow()
    {
        if (!_enabled || _modelPath is null) return;

        var act = () =>
        {
            using var sut = new OnnxArabicEmbeddingService(
                modelPath: _modelPath,
                logger: NullLogger<OnnxArabicEmbeddingService>.Instance);
        };

        act.Should().NotThrow();
    }

    [Fact]
    public async Task EmbedAsync_ReturnsEmbeddingOfConfiguredDimension()
    {
        if (!_enabled || _modelPath is null) return;

        using var sut = new OnnxArabicEmbeddingService(
            modelPath: _modelPath,
            logger: NullLogger<OnnxArabicEmbeddingService>.Instance,
            embeddingDimension: 768);

        var embedding = await sut.EmbedAsync("Ù†Øµ Ù‚Ø§Ù†ÙˆÙ†ÙŠ Ù„Ù„Ø§Ø®ØªØ¨Ø§Ø±");

        embedding.Should().NotBeNull();
        embedding.Length.Should().Be(768);
    }

    [Fact]
    public async Task EmbedBatchAsync_ReturnsEmbeddingPerInput()
    {
        if (!_enabled || _modelPath is null) return;

        using var sut = new OnnxArabicEmbeddingService(
            modelPath: _modelPath,
            logger: NullLogger<OnnxArabicEmbeddingService>.Instance,
            embeddingDimension: 768);

        var texts = new[]
        {
            "Ø§Ù„Ù†Øµ Ø§Ù„Ø£ÙˆÙ„ Ù„Ù„Ø§Ø®ØªØ¨Ø§Ø±",
            "Ø§Ù„Ù†Øµ Ø§Ù„Ø«Ø§Ù†ÙŠ Ù„Ù„Ø§Ø®ØªØ¨Ø§Ø±",
            "third sample text"
        };

        var embeddings = await sut.EmbedBatchAsync(texts);

        embeddings.Should().HaveCount(3);
        embeddings.Should().OnlyContain(vector => vector.Length == 768);
    }

    [Fact]
    public async Task EmbedAsync_SameInput_UsesStableCachingBehavior()
    {
        if (!_enabled || _modelPath is null) return;

        using var sut = new OnnxArabicEmbeddingService(
            modelPath: _modelPath,
            logger: NullLogger<OnnxArabicEmbeddingService>.Instance,
            embeddingDimension: 768);

        var first = await sut.EmbedAsync("Ù†Øµ Ù…ØªÙƒØ±Ø±");
        var second = await sut.EmbedAsync("Ù†Øµ Ù…ØªÙƒØ±Ø±");

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task EmbedBatchAsync_WithCancellation_ThrowsOperationCanceled()
    {
        if (!_enabled || _modelPath is null) return;

        using var sut = new OnnxArabicEmbeddingService(
            modelPath: _modelPath,
            logger: NullLogger<OnnxArabicEmbeddingService>.Instance,
            embeddingDimension: 768);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await sut.EmbedBatchAsync(new[] { "a", "b" }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

