using System;
using System.IO;
using FluentAssertions;
using Poseidon.Ingestion.Embedding;
using Microsoft.Extensions.Logging;
using Moq;

namespace Poseidon.UnitTests.Ingestion;

public sealed class OnnxArabicEmbeddingServiceTests
{
    private readonly Mock<ILogger<OnnxArabicEmbeddingService>> _logger = new();

    [Fact]
    public void Constructor_MissingModel_ThrowsFileNotFound()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.onnx");

        var act = () => new OnnxArabicEmbeddingService(
            modelPath: missingPath,
            logger: _logger.Object);

        act.Should().Throw<FileNotFoundException>()
            .WithMessage("*ONNX embedding model not found*");
    }

    [Fact]
    public void Constructor_MissingModel_ContainsPathInMessage()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "arabic-model-missing.onnx");

        var act = () => new OnnxArabicEmbeddingService(
            modelPath: missingPath,
            logger: _logger.Object);

        act.Should().Throw<FileNotFoundException>()
            .Where(ex => ex.Message.Contains("arabic-model-missing.onnx"));
    }

    [Fact]
    public void Constructor_MissingModel_WithCustomVocabPath_StillThrowsFileNotFound()
    {
        var missingModelPath = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.onnx");
        var vocabPath = Path.Combine(Path.GetTempPath(), $"vocab_{Guid.NewGuid():N}.txt");

        var act = () => new OnnxArabicEmbeddingService(
            modelPath: missingModelPath,
            logger: _logger.Object,
            vocabPath: vocabPath,
            embeddingDimension: 1024,
            maxSequenceLength: 256,
            maxCacheSize: 100,
            maxConcurrency: 2);

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Constructor_MissingModel_WithArabicPath_ThrowsFileNotFound()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "نموذج_مفقود.onnx");

        var act = () => new OnnxArabicEmbeddingService(
            modelPath: missingPath,
            logger: _logger.Object);

        act.Should().Throw<FileNotFoundException>();
    }
}


