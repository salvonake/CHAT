using System.IO;
using FluentAssertions;
using LegalAI.Desktop.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace LegalAI.UnitTests.Services;

/// <summary>
/// Tests for <see cref="ModelDownloadService"/>: local copy, hash verification,
/// Ollama connectivity, and helper utilities.
/// </summary>
public sealed class ModelDownloadServiceTests : IDisposable
{
    private readonly ModelDownloadService _sut;
    private readonly string _tempDir;

    public ModelDownloadServiceTests()
    {
        _sut = new ModelDownloadService(Mock.Of<ILogger<ModelDownloadService>>());
        _tempDir = Path.Combine(Path.GetTempPath(), $"LegalAI_Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    // ═══════════════════════════════════════
    //  FormatFileSize
    // ═══════════════════════════════════════

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1572864, "1.5 MB")]
    [InlineData(1073741824, "1.00 GB")]
    [InlineData(10737418240, "10.00 GB")]
    public void FormatFileSize_ReturnsExpected(long bytes, string expected)
    {
        ModelDownloadService.FormatFileSize(bytes).Should().Be(expected);
    }

    // ═══════════════════════════════════════
    //  CopyLocalFileAsync
    // ═══════════════════════════════════════

    [Fact]
    public async Task CopyLocalFile_CopiesCorrectly()
    {
        // Arrange
        var sourcePath = Path.Combine(_tempDir, "source.gguf");
        var destPath = Path.Combine(_tempDir, "Models", "dest.gguf");
        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(sourcePath, content);

        var reportedProgress = new List<DownloadProgress>();
        var progress = new Progress<DownloadProgress>(p => reportedProgress.Add(p));

        // Act
        await _sut.CopyLocalFileAsync(sourcePath, destPath, progress, CancellationToken.None);

        // Assert
        File.Exists(destPath).Should().BeTrue("destination file should exist");
        var destContent = await File.ReadAllBytesAsync(destPath);
        destContent.Should().Equal(content, "copied content should match source");
    }

    [Fact]
    public async Task CopyLocalFile_ReportsProgress()
    {
        // Arrange
        var sourcePath = Path.Combine(_tempDir, "source.gguf");
        var destPath = Path.Combine(_tempDir, "dest.gguf");
        var content = new byte[512 * 1024]; // 512 KB — will have multiple progress reports
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(sourcePath, content);

        var reportedProgress = new List<DownloadProgress>();
        var progress = new Progress<DownloadProgress>(p => reportedProgress.Add(p));

        // Act
        await _sut.CopyLocalFileAsync(sourcePath, destPath, progress, CancellationToken.None);

        // Allow async Progress<T> callbacks to fire (IProgress<T> marshals through SynchronizationContext)
        await Task.Delay(500);

        // Assert
        reportedProgress.Should().NotBeEmpty("progress should be reported");
        reportedProgress.Last().BytesCompleted.Should().Be(content.Length);
    }

    [Fact]
    public async Task CopyLocalFile_ThrowsIfSourceMissing()
    {
        var sourcePath = Path.Combine(_tempDir, "nonexistent.gguf");
        var destPath = Path.Combine(_tempDir, "dest.gguf");

        var act = () => _sut.CopyLocalFileAsync(sourcePath, destPath, null, CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task CopyLocalFile_SupportsCancellation()
    {
        // Arrange — large enough file that cancellation can happen
        var sourcePath = Path.Combine(_tempDir, "source.gguf");
        var destPath = Path.Combine(_tempDir, "dest.gguf");
        await File.WriteAllBytesAsync(sourcePath, new byte[1024 * 1024]);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act
        var act = () => _sut.CopyLocalFileAsync(sourcePath, destPath, null, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CopyLocalFile_CreatesDestinationDirectory()
    {
        // Arrange
        var sourcePath = Path.Combine(_tempDir, "source.gguf");
        var destPath = Path.Combine(_tempDir, "nested", "deep", "dest.gguf");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);

        // Act
        await _sut.CopyLocalFileAsync(sourcePath, destPath, null, CancellationToken.None);

        // Assert
        File.Exists(destPath).Should().BeTrue();
    }

    [Fact]
    public async Task CopyLocalFile_OverwritesExistingDestination()
    {
        // Arrange
        var sourcePath = Path.Combine(_tempDir, "source.gguf");
        var destPath = Path.Combine(_tempDir, "dest.gguf");
        await File.WriteAllBytesAsync(sourcePath, [10, 20, 30]);
        await File.WriteAllBytesAsync(destPath, [99, 99]); // existing file

        // Act
        await _sut.CopyLocalFileAsync(sourcePath, destPath, null, CancellationToken.None);

        // Assert
        var result = await File.ReadAllBytesAsync(destPath);
        result.Should().Equal([10, 20, 30], "old file should be replaced");
    }

    // ═══════════════════════════════════════
    //  VerifyHashAsync
    // ═══════════════════════════════════════

    [Fact]
    public async Task VerifyHash_SkipsWhenNoExpectedHash()
    {
        var filePath = Path.Combine(_tempDir, "model.gguf");
        await File.WriteAllBytesAsync(filePath, [1, 2, 3]);

        var result = await _sut.VerifyHashAsync(filePath, null, null, CancellationToken.None);

        result.Passed.Should().BeTrue();
        result.ActualHash.Should().BeNull();
    }

    [Fact]
    public async Task VerifyHash_SkipsWhenEmptyExpectedHash()
    {
        var filePath = Path.Combine(_tempDir, "model.gguf");
        await File.WriteAllBytesAsync(filePath, [1, 2, 3]);

        var result = await _sut.VerifyHashAsync(filePath, "", null, CancellationToken.None);

        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyHash_SkipsWhenWhitespaceExpectedHash()
    {
        var filePath = Path.Combine(_tempDir, "model.gguf");
        await File.WriteAllBytesAsync(filePath, [1, 2, 3]);

        var result = await _sut.VerifyHashAsync(filePath, "   ", null, CancellationToken.None);

        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyHash_FailsWhenFileMissing()
    {
        var result = await _sut.VerifyHashAsync(
            Path.Combine(_tempDir, "missing.gguf"),
            "abc123", null, CancellationToken.None);

        result.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyHash_PassesWithCorrectHash()
    {
        // Arrange — compute the actual SHA-256 of [1, 2, 3]
        var filePath = Path.Combine(_tempDir, "model.gguf");
        byte[] content = [1, 2, 3];
        await File.WriteAllBytesAsync(filePath, content);

        using var sha = System.Security.Cryptography.SHA256.Create();
        var expectedHash = Convert.ToHexString(sha.ComputeHash(content)).ToLowerInvariant();

        // Act
        var result = await _sut.VerifyHashAsync(filePath, expectedHash, null, CancellationToken.None);

        // Assert
        result.Passed.Should().BeTrue();
        result.ActualHash.Should().Be(expectedHash);
    }

    [Fact]
    public async Task VerifyHash_FailsWithWrongHash()
    {
        var filePath = Path.Combine(_tempDir, "model.gguf");
        await File.WriteAllBytesAsync(filePath, [1, 2, 3]);

        var result = await _sut.VerifyHashAsync(filePath, "0000000000000000", null, CancellationToken.None);

        result.Passed.Should().BeFalse();
        result.ActualHash.Should().NotBeNullOrWhiteSpace();
        result.ExpectedHash.Should().Be("0000000000000000");
    }

    [Fact]
    public async Task VerifyHash_IsCaseInsensitive()
    {
        var filePath = Path.Combine(_tempDir, "model.gguf");
        byte[] content = [0xAB, 0xCD, 0xEF];
        await File.WriteAllBytesAsync(filePath, content);

        using var sha = System.Security.Cryptography.SHA256.Create();
        var upper = Convert.ToHexString(sha.ComputeHash(content)); // Uppercase

        var result = await _sut.VerifyHashAsync(filePath, upper, null, CancellationToken.None);

        result.Passed.Should().BeTrue("hash comparison should be case-insensitive");
    }

    [Fact]
    public async Task VerifyHash_ReportsProgress()
    {
        var filePath = Path.Combine(_tempDir, "model.gguf");
        await File.WriteAllBytesAsync(filePath, new byte[1024]);

        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(new byte[1024])).ToLowerInvariant();

        var progressReports = new List<DownloadProgress>();
        var progress = new Progress<DownloadProgress>(p => progressReports.Add(p));

        await _sut.VerifyHashAsync(filePath, hash, progress, CancellationToken.None);

        await Task.Delay(100); // Allow Progress<T> callbacks
        progressReports.Should().NotBeEmpty("progress should be reported during hashing");
    }

    // ═══════════════════════════════════════
    //  TestOllamaConnectionAsync
    // ═══════════════════════════════════════

    [Fact]
    public async Task TestOllama_FailsWithBadUrl()
    {
        var result = await _sut.TestOllamaConnectionAsync(
            "http://127.0.0.1:19999",  // Non-routable port
            "test-model",
            CancellationToken.None);

        result.Connected.Should().BeFalse();
        result.ModelAvailable.Should().BeFalse();
    }

    // ═══════════════════════════════════════
    //  DownloadProgress DTO
    // ═══════════════════════════════════════

    [Fact]
    public void DownloadProgress_FractionCalculatesCorrectly()
    {
        var p = new DownloadProgress(500, 1000, "test");
        p.Fraction.Should().BeApproximately(0.5, 0.001);
        p.PercentText.Should().Be("50.0%");
    }

    [Fact]
    public void DownloadProgress_FractionNegativeWhenTotalUnknown()
    {
        var p = new DownloadProgress(500, -1, "test");
        p.Fraction.Should().Be(-1);
        p.PercentText.Should().Be("...");
    }

    [Fact]
    public void DownloadProgress_ZeroTotal()
    {
        var p = new DownloadProgress(0, 0, "test");
        p.Fraction.Should().Be(-1, "0 total should be treated as unknown");
    }

    // ═══════════════════════════════════════
    //  HashVerificationResult DTO
    // ═══════════════════════════════════════

    [Fact]
    public void HashVerificationResult_Properties()
    {
        var r = new HashVerificationResult(true, "abc", "abc", "OK");
        r.Passed.Should().BeTrue();
        r.ActualHash.Should().Be("abc");
        r.ExpectedHash.Should().Be("abc");
        r.Message.Should().Be("OK");
    }

    // ═══════════════════════════════════════
    //  OllamaCheckResult DTO
    // ═══════════════════════════════════════

    [Fact]
    public void OllamaCheckResult_Properties()
    {
        var r = new OllamaCheckResult(true, true, "OK");
        r.Connected.Should().BeTrue();
        r.ModelAvailable.Should().BeTrue();
        r.Message.Should().Be("OK");
    }

    [Fact]
    public void OllamaCheckResult_DisconnectedState()
    {
        var r = new OllamaCheckResult(false, false, "failed");
        r.Connected.Should().BeFalse();
        r.ModelAvailable.Should().BeFalse();
    }
}
