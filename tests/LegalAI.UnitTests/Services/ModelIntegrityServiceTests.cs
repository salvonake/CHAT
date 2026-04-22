using System.IO;
using System.Security.Cryptography;
using FluentAssertions;
using LegalAI.Desktop;
using LegalAI.Desktop.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace LegalAI.UnitTests.Services;

/// <summary>
/// Tests for ModelIntegrityService — verifies LLM and embedding model
/// file integrity on startup via SHA-256 hash checking.
/// Safety-critical: corrupted models produce incorrect legal advice.
/// </summary>
public sealed class ModelIntegrityServiceTests : IDisposable
{
    private readonly Mock<ILogger<ModelIntegrityService>> _logger = new();
    private readonly string _tempDir;
    private readonly DataPaths _paths;

    public ModelIntegrityServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LegalAI_ModelTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var modelsDir = Path.Combine(_tempDir, "models");
        Directory.CreateDirectory(modelsDir);
        _paths = new DataPaths
        {
            DataDirectory = _tempDir,
            ModelsDirectory = modelsDir,
            VectorDbPath = Path.Combine(_tempDir, "vec.db"),
            HnswIndexPath = Path.Combine(_tempDir, "hnsw"),
            DocumentDbPath = Path.Combine(_tempDir, "doc.db"),
            AuditDbPath = Path.Combine(_tempDir, "audit.db"),
            WatchDirectory = Path.Combine(_tempDir, "watch")
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
    }

    private ModelIntegrityService CreateService(Dictionary<string, string?>? configOverrides = null)
    {
        var configData = configOverrides ?? new Dictionary<string, string?>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();
        return new ModelIntegrityService(config, _paths, _logger.Object);
    }

    private string CreateModelFile(string name, string content = "model binary data")
    {
        var path = Path.Combine(_paths.ModelsDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string ComputeFileHash(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var buffer = new byte[8192 * 16];
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
        }
        sha256.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }

    // ══════════════════════════════════════
    //  LLM Model — File Missing
    // ══════════════════════════════════════

    [Fact]
    public async Task VerifyOnStartup_LlmModelMissing_SetsInvalid()
    {
        var svc = CreateService();
        await svc.VerifyOnStartupAsync();

        svc.LlmModelExists.Should().BeFalse();
        svc.LlmModelValid.Should().BeFalse();
        svc.LlmError.Should().Contain("not found");
    }

    // ══════════════════════════════════════
    //  LLM Model — No Hash Configured
    // ══════════════════════════════════════

    [Fact]
    public async Task VerifyOnStartup_LlmModelExists_NoHash_AcceptsAsValid()
    {
        CreateModelFile("qwen2.5-14b.Q5_K_M.gguf");
        var svc = CreateService();
        await svc.VerifyOnStartupAsync();

        svc.LlmModelExists.Should().BeTrue();
        svc.LlmModelValid.Should().BeTrue();
        svc.LlmError.Should().BeNull();
    }

    // ══════════════════════════════════════
    //  LLM Model — Hash Match
    // ══════════════════════════════════════

    [Fact]
    public async Task VerifyOnStartup_LlmModelHashMatches_Valid()
    {
        var path = CreateModelFile("qwen2.5-14b.Q5_K_M.gguf", "known content");
        var hash = ComputeFileHash(path);

        var svc = CreateService(new Dictionary<string, string?>
        {
            ["ModelIntegrity:ExpectedLlmHash"] = hash
        });
        await svc.VerifyOnStartupAsync();

        svc.LlmModelValid.Should().BeTrue();
        svc.LlmError.Should().BeNull();
    }

    // ══════════════════════════════════════
    //  LLM Model — Hash Mismatch
    // ══════════════════════════════════════

    [Fact]
    public async Task VerifyOnStartup_LlmModelHashMismatch_Invalid()
    {
        CreateModelFile("qwen2.5-14b.Q5_K_M.gguf", "actual content");

        var svc = CreateService(new Dictionary<string, string?>
        {
            ["ModelIntegrity:ExpectedLlmHash"] = "0000000000000000000000000000000000000000000000000000000000000000"
        });
        await svc.VerifyOnStartupAsync();

        svc.LlmModelValid.Should().BeFalse();
        (svc.LlmError!.Contains("integrity") || svc.LlmError.Contains("سلامة")).Should().BeTrue();
    }

    // ══════════════════════════════════════
    //  LLM Model — Custom Path
    // ══════════════════════════════════════

    [Fact]
    public async Task VerifyOnStartup_LlmCustomPath_UsesConfiguredPath()
    {
        var customPath = Path.Combine(_tempDir, "custom_llm.gguf");
        File.WriteAllText(customPath, "custom model");

        var svc = CreateService(new Dictionary<string, string?>
        {
            ["Llm:ModelPath"] = customPath
        });
        await svc.VerifyOnStartupAsync();

        svc.LlmModelExists.Should().BeTrue();
        svc.LlmModelValid.Should().BeTrue();
    }

    // ══════════════════════════════════════
    //  Embedding Model — ONNX Provider
    // ══════════════════════════════════════

    [Fact]
    public async Task VerifyOnStartup_EmbeddingMissing_Onnx_SetsInvalid()
    {
        CreateModelFile("qwen2.5-14b.Q5_K_M.gguf"); // LLM exists
        var svc = CreateService(new Dictionary<string, string?>
        {
            ["Embedding:Provider"] = "onnx"
        });
        await svc.VerifyOnStartupAsync();

        svc.EmbeddingModelExists.Should().BeFalse();
        svc.EmbeddingModelValid.Should().BeFalse();
        svc.EmbeddingError.Should().Contain("not found");
    }

    [Fact]
    public async Task VerifyOnStartup_EmbeddingExists_NoHash_Onnx_Valid()
    {
        CreateModelFile("qwen2.5-14b.Q5_K_M.gguf");
        CreateModelFile("arabert.onnx");
        var svc = CreateService(new Dictionary<string, string?>
        {
            ["Embedding:Provider"] = "onnx"
        });
        await svc.VerifyOnStartupAsync();

        svc.EmbeddingModelExists.Should().BeTrue();
        svc.EmbeddingModelValid.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyOnStartup_EmbeddingHashMatches_Onnx_Valid()
    {
        CreateModelFile("qwen2.5-14b.Q5_K_M.gguf");
        var path = CreateModelFile("arabert.onnx", "embedding model data");
        var hash = ComputeFileHash(path);

        var svc = CreateService(new Dictionary<string, string?>
        {
            ["Embedding:Provider"] = "onnx",
            ["ModelIntegrity:ExpectedEmbeddingHash"] = hash
        });
        await svc.VerifyOnStartupAsync();

        svc.EmbeddingModelValid.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyOnStartup_EmbeddingHashMismatch_Onnx_Invalid()
    {
        CreateModelFile("qwen2.5-14b.Q5_K_M.gguf");
        CreateModelFile("arabert.onnx", "legit data");

        var svc = CreateService(new Dictionary<string, string?>
        {
            ["Embedding:Provider"] = "onnx",
            ["ModelIntegrity:ExpectedEmbeddingHash"] = "badhash0000000000000000000000000000000000000000000000000000000000"
        });
        await svc.VerifyOnStartupAsync();

        svc.EmbeddingModelValid.Should().BeFalse();
        svc.EmbeddingError.Should().NotBeNullOrEmpty();
    }

    // ══════════════════════════════════════
    //  Embedding Model — Ollama Provider (no local file needed)
    // ══════════════════════════════════════

    [Fact]
    public async Task VerifyOnStartup_OllamaProvider_SkipsLocalFileCheck()
    {
        CreateModelFile("qwen2.5-14b.Q5_K_M.gguf");
        // No arabert.onnx created — should still be valid for Ollama
        var svc = CreateService(new Dictionary<string, string?>
        {
            ["Embedding:Provider"] = "ollama"
        });
        await svc.VerifyOnStartupAsync();

        svc.EmbeddingModelValid.Should().BeTrue();
        svc.EmbeddingModelExists.Should().BeTrue();
    }

    // ══════════════════════════════════════
    //  GPU Detection
    // ══════════════════════════════════════

    [Fact]
    public async Task VerifyOnStartup_DetectsGpuInfo()
    {
        CreateModelFile("qwen2.5-14b.Q5_K_M.gguf");
        var svc = CreateService();
        await svc.VerifyOnStartupAsync();

        svc.DetectedGpuInfo.Should().NotBeNullOrEmpty();
        // Either CUDA detected or CPU mode — depends on test environment
    }

    // ══════════════════════════════════════
    //  Embedding Custom Path
    // ══════════════════════════════════════

    [Fact]
    public async Task VerifyOnStartup_EmbeddingCustomPath_UsesConfiguredPath()
    {
        CreateModelFile("qwen2.5-14b.Q5_K_M.gguf");
        var customPath = Path.Combine(_tempDir, "custom_embed.onnx");
        File.WriteAllText(customPath, "custom embedding");

        var svc = CreateService(new Dictionary<string, string?>
        {
            ["Embedding:Provider"] = "onnx",
            ["Embedding:OnnxModelPath"] = customPath
        });
        await svc.VerifyOnStartupAsync();

        svc.EmbeddingModelExists.Should().BeTrue();
        svc.EmbeddingModelValid.Should().BeTrue();
    }

    // ══════════════════════════════════════
    //  Default Properties Before Verification
    // ══════════════════════════════════════

    [Fact]
    public void Properties_BeforeVerification_DefaultToFalse()
    {
        var svc = CreateService();

        svc.LlmModelValid.Should().BeFalse();
        svc.EmbeddingModelValid.Should().BeFalse();
        svc.LlmModelExists.Should().BeFalse();
        svc.EmbeddingModelExists.Should().BeFalse();
        svc.DetectedGpuInfo.Should().BeNull();
    }

    // ══════════════════════════════════════
    //  Both Models Missing
    // ══════════════════════════════════════

    [Fact]
    public async Task VerifyOnStartup_BothMissing_BothInvalid()
    {
        var svc = CreateService();
        await svc.VerifyOnStartupAsync();

        svc.LlmModelValid.Should().BeFalse();
        svc.LlmModelExists.Should().BeFalse();
        svc.EmbeddingModelValid.Should().BeFalse();
        svc.EmbeddingModelExists.Should().BeFalse();
    }

    // ══════════════════════════════════════
    //  Both Models Valid
    // ══════════════════════════════════════

    [Fact]
    public async Task VerifyOnStartup_BothExistNoHashes_BothValid()
    {
        CreateModelFile("qwen2.5-14b.Q5_K_M.gguf");
        CreateModelFile("arabert.onnx");
        var svc = CreateService(new Dictionary<string, string?>
        {
            ["Embedding:Provider"] = "onnx"
        });
        await svc.VerifyOnStartupAsync();

        svc.LlmModelValid.Should().BeTrue();
        svc.EmbeddingModelValid.Should().BeTrue();
        svc.DetectedGpuInfo.Should().NotBeNullOrEmpty();
    }
}
