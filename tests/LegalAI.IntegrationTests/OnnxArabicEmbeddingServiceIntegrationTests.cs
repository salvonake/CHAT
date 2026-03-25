using FluentAssertions;
using LegalAI.Ingestion.Embedding;
using Microsoft.Extensions.Logging.Abstractions;

namespace LegalAI.IntegrationTests;

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

        var embedding = await sut.EmbedAsync("نص قانوني للاختبار");

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
            "النص الأول للاختبار",
            "النص الثاني للاختبار",
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

        var first = await sut.EmbedAsync("نص متكرر");
        var second = await sut.EmbedAsync("نص متكرر");

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
