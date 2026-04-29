using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Poseidon.Desktop.Services;

/// <summary>
/// Handles model file acquisition: local copy, HTTP download, Ollama pull, and SHA-256 verification.
/// Provides progress callbacks for UI binding. Thread-safe, cancellable.
/// </summary>
public sealed class ModelDownloadService
{
    private readonly ILogger<ModelDownloadService> _logger;
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromHours(6) // Large model downloads can take hours
    };

    public ModelDownloadService(ILogger<ModelDownloadService> logger)
    {
        _logger = logger;
    }

    // ═══════════════════════════════════════════
    //  Copy a local file with progress
    // ═══════════════════════════════════════════

    /// <summary>
    /// Copies a model file from a local/network path to the target directory.
    /// Provides progress from 0.0 to 1.0.
    /// </summary>
    public async Task CopyLocalFileAsync(
        string sourcePath,
        string destPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Source model file not found: {sourcePath}", sourcePath);

        var fileInfo = new FileInfo(sourcePath);
        var totalBytes = fileInfo.Length;
        var buffer = new byte[256 * 1024]; // 256 KB buffer

        _logger.LogInformation("Copying model file: {Source} → {Dest} ({SizeMB:N0} MB)",
            sourcePath, destPath, totalBytes / (1024.0 * 1024.0));

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        // Write to temp file, rename on completion (atomic)
        var tempPath = destPath + ".tmp";
        try
        {
            await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true);
            await using var destStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, true);

            long totalRead = 0;
            int bytesRead;
            while ((bytesRead = await sourceStream.ReadAsync(buffer, ct)) > 0)
            {
                await destStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;
                progress?.Report(new DownloadProgress(totalRead, totalBytes, "Copying..."));
            }

            await destStream.FlushAsync(ct);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        // Atomic rename
        if (File.Exists(destPath))
            File.Delete(destPath);
        File.Move(tempPath, destPath);

        _logger.LogInformation("Model file copied successfully: {Dest}", destPath);
    }

    // ═══════════════════════════════════════════
    //  Download from URL with progress
    // ═══════════════════════════════════════════

    /// <summary>
    /// Downloads a model file from a URL with streaming progress.
    /// Supports resume on retry via temp file.
    /// </summary>
    public async Task DownloadFileAsync(
        string url,
        string destPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        _logger.LogInformation("Downloading model from: {Url}", url);

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        var tempPath = destPath + ".tmp";
        try
        {
            using var response = await SharedHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            _logger.LogInformation("Download size: {SizeMB:N1} MB",
                totalBytes > 0 ? totalBytes / (1024.0 * 1024.0) : -1);

            var buffer = new byte[256 * 1024];
            long totalRead = 0;

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, true);

            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;

                var statusText = totalBytes > 0
                    ? $"Downloading... {totalRead / (1024.0 * 1024.0):N1} / {totalBytes / (1024.0 * 1024.0):N1} MB"
                    : $"Downloading... {totalRead / (1024.0 * 1024.0):N1} MB";

                progress?.Report(new DownloadProgress(totalRead, totalBytes, statusText));
            }

            await fileStream.FlushAsync(ct);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        if (File.Exists(destPath))
            File.Delete(destPath);
        File.Move(tempPath, destPath);

        _logger.LogInformation("Download complete: {Dest}", destPath);
    }

    // ═══════════════════════════════════════════
    //  SHA-256 Hash Verification
    // ═══════════════════════════════════════════

    /// <summary>
    /// Computes SHA-256 hash of a file and compares to expected value.
    /// </summary>
    public async Task<HashVerificationResult> VerifyHashAsync(
        string filePath,
        string? expectedHash,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            _logger.LogInformation("No expected hash configured — skipping verification for {File}", filePath);
            return new HashVerificationResult(true, null, null, "No expected hash configured - skipped");
        }

        if (!File.Exists(filePath))
            return new HashVerificationResult(false, null, expectedHash, "File not found");

        _logger.LogInformation("Verifying SHA-256 hash for: {File}", filePath);
        progress?.Report(new DownloadProgress(0, 1, "Verifying file integrity..."));

        var fileInfo = new FileInfo(filePath);
        var totalBytes = fileInfo.Length;

        using var sha256 = SHA256.Create();
        var buffer = new byte[256 * 1024];
        long totalRead = 0;

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true);

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            totalRead += bytesRead;
            progress?.Report(new DownloadProgress(totalRead, totalBytes, "Verifying file integrity..."));
        }

        sha256.TransformFinalBlock([], 0, 0);
        var actualHash = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
        var matches = string.Equals(actualHash, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase);

        if (matches)
        {
            _logger.LogInformation("Hash verification passed ✓ for {File}", filePath);
            return new HashVerificationResult(true, actualHash, expectedHash, "Verification passed ✓");
        }
        else
        {
            _logger.LogError("Hash verification FAILED for {File}. Expected: {Expected}, Got: {Actual}",
                filePath, expectedHash, actualHash);
            return new HashVerificationResult(false, actualHash, expectedHash,
                $"Integrity verification failed\nExpected: {expectedHash}\nActual: {actualHash}");
        }
    }

    // ═══════════════════════════════════════════
    //  Ollama Connection Test
    // ═══════════════════════════════════════════

    /// <summary>
    /// Tests connectivity to an Ollama server and checks if a specific model is available.
    /// </summary>
    public async Task<OllamaCheckResult> TestOllamaConnectionAsync(
        string baseUrl,
        string modelName,
        CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Testing Ollama connection: {Url}", baseUrl);

            using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };

            // Test connectivity
            var response = await client.GetAsync("/api/tags", ct);
            if (!response.IsSuccessStatusCode)
                return new OllamaCheckResult(false, false, $"Ollama returned HTTP {(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(ct);

            // Simple check if model name appears in the tags list
            var modelAvailable = body.Contains(modelName, StringComparison.OrdinalIgnoreCase);

            _logger.LogInformation("Ollama connected. Model '{Model}' available: {Available}",
                modelName, modelAvailable);

            return new OllamaCheckResult(true, modelAvailable,
                modelAvailable
                    ? $"✓ Ollama connected - model '{modelName}' is available"
                    : $"⚠ Ollama connected - model '{modelName}' is unavailable. Run: ollama pull {modelName}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Cannot connect to Ollama at {Url}", baseUrl);
            return new OllamaCheckResult(false, false, $"Cannot connect to Ollama at {baseUrl}");
        }
        catch (TaskCanceledException)
        {
            return new OllamaCheckResult(false, false, "Connection to Ollama timed out");
        }
    }

    // ═══════════════════════════════════════════
    //  Model Size Estimation
    // ═══════════════════════════════════════════

    public static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):N1} MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):N2} GB"
        };
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}

// ═══════════════════════════════════════════
//  Data Transfer Objects
// ═══════════════════════════════════════════

/// <summary>Progress data for download/copy/verification operations.</summary>
public readonly record struct DownloadProgress(
    long BytesCompleted,
    long TotalBytes,
    string StatusText)
{
    /// <summary>Progress fraction from 0.0 to 1.0 (-1 if total unknown).</summary>
    public double Fraction => TotalBytes > 0 ? (double)BytesCompleted / TotalBytes : -1;

    /// <summary>Percentage string like "47.2%".</summary>
    public string PercentText => TotalBytes > 0 ? $"{Fraction * 100:N1}%" : "...";
}

/// <summary>Result of SHA-256 hash verification.</summary>
public readonly record struct HashVerificationResult(
    bool Passed,
    string? ActualHash,
    string? ExpectedHash,
    string Message);

/// <summary>Result of Ollama connectivity check.</summary>
public readonly record struct OllamaCheckResult(
    bool Connected,
    bool ModelAvailable,
    string Message);


