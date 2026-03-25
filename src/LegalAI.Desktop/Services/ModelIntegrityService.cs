using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LegalAI.Desktop.Services;

/// <summary>
/// Verifies model file integrity on startup to prevent corrupted/tampered models
/// from producing incorrect legal answers. Life-critical safety measure.
/// </summary>
public class ModelIntegrityService
{
    private static readonly string[] KnownLlmModelFileNames =
    [
        "qwen2.5-14b.Q5_K_M.gguf",
        "Qwen_Qwen3.5-9B-Q5_K_M.gguf",
        "Qwen3.5-9B-Q5_K_M.gguf"
    ];

    private readonly IConfiguration _config;
    private readonly DataPaths _paths;
    private readonly ILogger<ModelIntegrityService> _logger;

    public virtual bool LlmModelValid { get; private set; }
    public virtual bool EmbeddingModelValid { get; private set; }
    public virtual string? LlmError { get; private set; }
    public virtual string? EmbeddingError { get; private set; }
    public virtual bool LlmModelExists { get; private set; }
    public virtual bool EmbeddingModelExists { get; private set; }
    public virtual string? DetectedGpuInfo { get; private set; }

    public ModelIntegrityService(
        IConfiguration config,
        DataPaths paths,
        ILogger<ModelIntegrityService> logger)
    {
        _config = config;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Runs on application startup. Verifies model files exist and optionally
    /// checks SHA-256 hashes against expected values.
    /// </summary>
    public async Task VerifyOnStartupAsync()
    {
        await Task.Run(() =>
        {
            VerifyLlmModel();
            VerifyEmbeddingModel();
            DetectGpu();
        });
    }

    private void VerifyLlmModel()
    {
        var modelPath = _config["Llm:ModelPath"];
        if (string.IsNullOrEmpty(modelPath))
        {
            modelPath = KnownLlmModelFileNames
                .Select(name => Path.Combine(_paths.ModelsDirectory, name))
                .FirstOrDefault(File.Exists)
                ?? Path.Combine(_paths.ModelsDirectory, KnownLlmModelFileNames[0]);
        }

        LlmModelExists = File.Exists(modelPath);

        if (!LlmModelExists)
        {
            LlmError = $"ملف النموذج غير موجود: {modelPath}\nModel file not found: {modelPath}";
            LlmModelValid = false;
            _logger.LogWarning("LLM model file not found at {Path}", modelPath);
            return;
        }

        var expectedHash = _config["ModelIntegrity:ExpectedLlmHash"];
        if (string.IsNullOrEmpty(expectedHash))
        {
            // No hash configured — accept the file as-is but warn
            LlmModelValid = true;
            _logger.LogInformation(
                "LLM model found at {Path} (no integrity hash configured — skipping verification)",
                modelPath);
            return;
        }

        try
        {
            _logger.LogInformation("Verifying LLM model integrity (SHA-256)...");
            var actualHash = ComputeFileHash(modelPath);

            if (string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                LlmModelValid = true;
                _logger.LogInformation("LLM model integrity verified ✓");
            }
            else
            {
                LlmModelValid = false;
                LlmError = $"تحقق سلامة النموذج فشل — التجزئة لا تتطابق\n" +
                           $"Model integrity check failed.\nExpected: {expectedHash}\nActual: {actualHash}";
                _logger.LogError(
                    "LLM MODEL INTEGRITY FAILURE. Expected: {Expected}, Got: {Actual}",
                    expectedHash, actualHash);
            }
        }
        catch (Exception ex)
        {
            LlmModelValid = false;
            LlmError = $"خطأ في التحقق من سلامة النموذج: {ex.Message}";
            _logger.LogError(ex, "Failed to compute LLM model hash");
        }
    }

    private void VerifyEmbeddingModel()
    {
        var provider = _config["Embedding:Provider"]?.ToLowerInvariant() ?? "onnx";
        if (provider != "onnx")
        {
            // Ollama embeddings — no local model file to verify
            EmbeddingModelValid = true;
            EmbeddingModelExists = true;
            return;
        }

        var modelPath = _config["Embedding:OnnxModelPath"];
        if (string.IsNullOrEmpty(modelPath))
            modelPath = Path.Combine(_paths.ModelsDirectory, "arabert.onnx");

        EmbeddingModelExists = File.Exists(modelPath);

        if (!EmbeddingModelExists)
        {
            EmbeddingError = $"ملف نموذج التضمين غير موجود: {modelPath}\n" +
                             $"Embedding model not found: {modelPath}";
            EmbeddingModelValid = false;
            _logger.LogWarning("Embedding ONNX model not found at {Path}", modelPath);
            return;
        }

        var expectedHash = _config["ModelIntegrity:ExpectedEmbeddingHash"];
        if (string.IsNullOrEmpty(expectedHash))
        {
            EmbeddingModelValid = true;
            _logger.LogInformation("Embedding model found at {Path} (no hash configured)", modelPath);
            return;
        }

        try
        {
            var actualHash = ComputeFileHash(modelPath);
            EmbeddingModelValid = string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);

            if (!EmbeddingModelValid)
            {
                EmbeddingError = $"تحقق سلامة نموذج التضمين فشل\nEmbedding model integrity check failed.";
                _logger.LogError("Embedding model integrity failure");
            }
        }
        catch (Exception ex)
        {
            EmbeddingModelValid = false;
            EmbeddingError = ex.Message;
            _logger.LogError(ex, "Failed to verify embedding model");
        }
    }

    private void DetectGpu()
    {
        try
        {
            // Simple GPU detection via environment
            var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
            if (!string.IsNullOrEmpty(cudaPath))
            {
                DetectedGpuInfo = $"CUDA detected: {cudaPath}";
                _logger.LogInformation("GPU: {Info}", DetectedGpuInfo);
            }
            else
            {
                DetectedGpuInfo = "لم يتم اكتشاف وحدة معالجة رسومات CUDA\nNo CUDA GPU detected — running on CPU";
                _logger.LogInformation("No CUDA GPU detected. LLM will use CPU mode.");
            }
        }
        catch (Exception ex)
        {
            DetectedGpuInfo = $"GPU detection error: {ex.Message}";
        }
    }

    private static string ComputeFileHash(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);

        // For large files (10GB+), read in chunks
        var buffer = new byte[8192 * 16]; // 128KB buffer
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
        }
        sha256.TransformFinalBlock([], 0, 0);

        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }
}
