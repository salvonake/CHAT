using System.IO;
using FluentAssertions;
using LegalAI.Infrastructure.Llm;
using Microsoft.Extensions.Logging;
using Moq;

namespace LegalAI.UnitTests.Infrastructure;

/// <summary>
/// Tests for LLamaSharpLlmService. Since we don't have native LLamaSharp binaries
/// in the test environment, we test the initialization guard paths, error handling,
/// and disposal behavior that don't require the actual model.
/// </summary>
public sealed class LLamaSharpLlmServiceTests : IDisposable
{
    private readonly Mock<ILogger<LLamaSharpLlmService>> _logger = new();
    private readonly string _tempDir;

    public LLamaSharpLlmServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LLama_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    private LLamaSharpLlmService CreateSut(string? modelPath = null) =>
        new(modelPath ?? Path.Combine(_tempDir, "nonexistent.gguf"),
            gpuLayers: 0, contextSize: 2048, _logger.Object);

    // ─── Construction ────────────────────────────────────────────

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var sut = CreateSut();
        sut.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_AcceptsGpuLayersAndContextSize()
    {
        var sut = new LLamaSharpLlmService(
            Path.Combine(_tempDir, "model.gguf"),
            gpuLayers: 35,
            contextSize: 8192,
            _logger.Object);

        sut.Should().NotBeNull();
    }

    // ─── GenerateAsync — model not found ─────────────────────────

    [Fact]
    public async Task GenerateAsync_ModelNotFound_ReturnsFailed()
    {
        var sut = CreateSut();

        var result = await sut.GenerateAsync("system prompt", "user prompt");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
        result.Content.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateAsync_ModelNotFound_ErrorContainsPath()
    {
        var missingPath = Path.Combine(_tempDir, "missing_model.gguf");
        var sut = CreateSut(missingPath);

        var result = await sut.GenerateAsync("system", "user");

        result.Error.Should().Contain("missing_model.gguf");
    }

    [Fact]
    public async Task GenerateAsync_CalledTwice_ReturnsSameError()
    {
        var sut = CreateSut();

        var r1 = await sut.GenerateAsync("s", "u");
        var r2 = await sut.GenerateAsync("s", "u");

        r1.Success.Should().BeFalse();
        r2.Success.Should().BeFalse();
        r1.Error.Should().Be(r2.Error);
    }

    // ─── GenerateAsync — model file exists but invalid ───────────

    [Fact]
    public async Task GenerateAsync_InvalidModelFile_ReturnsFailed()
    {
        var fakePath = Path.Combine(_tempDir, "fake.gguf");
        File.WriteAllText(fakePath, "not a real GGUF model file");

        var sut = CreateSut(fakePath);

        var result = await sut.GenerateAsync("system", "user");

        // LLamaSharp assembly won't be found, so init fails gracefully
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    // ─── IsAvailableAsync ────────────────────────────────────────

    [Fact]
    public async Task IsAvailableAsync_ModelNotFound_ReturnsFalse()
    {
        var sut = CreateSut();

        var available = await sut.IsAvailableAsync();

        available.Should().BeFalse();
    }

    [Fact]
    public async Task IsAvailableAsync_CalledMultipleTimes_Consistent()
    {
        var sut = CreateSut();

        var r1 = await sut.IsAvailableAsync();
        var r2 = await sut.IsAvailableAsync();

        r1.Should().BeFalse();
        r2.Should().BeFalse();
    }

    // ─── StreamGenerateAsync ────────────────────────────────────

    [Fact]
    public async Task StreamGenerateAsync_ModelNotFound_YieldsErrorMessage()
    {
        var sut = CreateSut();

        var tokens = new List<string>();
        await foreach (var token in sut.StreamGenerateAsync("system", "user"))
        {
            tokens.Add(token);
        }

        tokens.Should().NotBeEmpty();
        tokens[0].Should().Contain("not found");
    }

    [Fact]
    public async Task StreamGenerateAsync_ModelNotFound_YieldsOnlyOnce()
    {
        var sut = CreateSut();

        var tokens = new List<string>();
        await foreach (var token in sut.StreamGenerateAsync("system", "user"))
        {
            tokens.Add(token);
        }

        tokens.Should().HaveCount(1);
    }

    // ─── Dispose ─────────────────────────────────────────────────

    [Fact]
    public void Dispose_BeforeInit_DoesNotThrow()
    {
        var sut = CreateSut();

        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_AfterInit_DoesNotThrow()
    {
        var sut = CreateSut();
        // Force initialization
        await sut.IsAvailableAsync();

        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var sut = CreateSut();
        sut.Dispose();

        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }

    // ─── Concurrent initialization safety ────────────────────────

    [Fact]
    public async Task GenerateAsync_ConcurrentCalls_AllReturnSameError()
    {
        var sut = CreateSut();

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => sut.GenerateAsync("sys", "usr"))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r =>
        {
            r.Success.Should().BeFalse();
            r.Error.Should().NotBeNullOrEmpty();
        });
    }
}
