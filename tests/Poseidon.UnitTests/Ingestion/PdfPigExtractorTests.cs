using System.IO;
using FluentAssertions;
using Poseidon.Ingestion.Extractors;
using Microsoft.Extensions.Logging;
using Moq;
using UglyToad.PdfPig.Writer;

namespace Poseidon.UnitTests.Ingestion;

public sealed class PdfPigExtractorTests : IDisposable
{
    private readonly Mock<ILogger<PdfPigExtractor>> _logger = new();
    private readonly PdfPigExtractor _sut;
    private readonly string _tempDir;

    public PdfPigExtractorTests()
    {
        _sut = new PdfPigExtractor(_logger.Object);
        _tempDir = Path.Combine(Path.GetTempPath(), $"PdfPigTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* cleanup best effort */ }
    }

    private string CreatePdf(string text, int pages = 1)
    {
        var filePath = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.pdf");
        var builder = new PdfDocumentBuilder();

        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);

        for (int i = 0; i < pages; i++)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            page.AddText(text, 12, new UglyToad.PdfPig.Core.PdfPoint(72, 700), font);
        }

        File.WriteAllBytes(filePath, builder.Build());
        return filePath;
    }

    private string CreateEmptyPdf(int pages = 1)
    {
        var filePath = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.pdf");
        var builder = new PdfDocumentBuilder();

        for (int i = 0; i < pages; i++)
        {
            builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        }

        File.WriteAllBytes(filePath, builder.Build());
        return filePath;
    }

    // ─── File not found ──────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_FileNotFound_ReturnsError()
    {
        var result = await _sut.ExtractAsync(@"C:\nonexistent\file.pdf");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("File not found");
        result.Pages.Should().BeEmpty();
        result.FullText.Should().BeEmpty();
    }

    // ─── Valid PDF with text ─────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_ValidPdf_ReturnsFullText()
    {
        var pdf = CreatePdf("Hello World Legal Document");

        var result = await _sut.ExtractAsync(pdf);

        result.Success.Should().BeTrue();
        result.FullText.Should().Contain("Hello");
        result.FullText.Should().Contain("World");
    }

    [Fact]
    public async Task ExtractAsync_ValidPdf_ReturnsPageCount()
    {
        var pdf = CreatePdf("Some text", pages: 3);

        var result = await _sut.ExtractAsync(pdf);

        result.Success.Should().BeTrue();
        result.PageCount.Should().Be(3);
    }

    [Fact]
    public async Task ExtractAsync_ValidPdf_ReturnsPages()
    {
        var pdf = CreatePdf("Page content here");

        var result = await _sut.ExtractAsync(pdf);

        result.Pages.Should().NotBeEmpty();
        result.Pages[0].PageNumber.Should().Be(1);
        result.Pages[0].Text.Should().Contain("Page content");
    }

    [Fact]
    public async Task ExtractAsync_MultiPage_EachPageHasContent()
    {
        var pdf = CreatePdf("Repeated text on each page", pages: 2);

        var result = await _sut.ExtractAsync(pdf);

        result.Pages.Should().HaveCount(2);
        result.Pages[0].PageNumber.Should().Be(1);
        result.Pages[1].PageNumber.Should().Be(2);
    }

    // ─── Empty / scanned PDFs ────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_EmptyPdf_DetectsAsScanned()
    {
        var pdf = CreateEmptyPdf();

        var result = await _sut.ExtractAsync(pdf);

        result.Success.Should().BeTrue();
        result.IsScanned.Should().BeTrue();
    }

    [Fact]
    public async Task ExtractAsync_TextPdf_NotScanned()
    {
        // Need at least MinCharsPerPageForText (30) characters
        var longText = "This is a text-based legal document with enough characters to pass the threshold for detection.";
        var pdf = CreatePdf(longText);

        var result = await _sut.ExtractAsync(pdf);

        result.Success.Should().BeTrue();
        result.IsScanned.Should().BeFalse();
    }

    // ─── Language detection ──────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_EnglishText_DetectedAsUnknown()
    {
        var pdf = CreatePdf("This is English legal text for testing purposes only.");

        var result = await _sut.ExtractAsync(pdf);

        result.DetectedLanguage.Should().Be("unknown");
    }

    // ─── Corrupt file ────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_CorruptFile_ReturnsError()
    {
        var filePath = Path.Combine(_tempDir, "corrupt.pdf");
        File.WriteAllText(filePath, "this is not a valid PDF file content");

        var result = await _sut.ExtractAsync(filePath);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("PDF extraction failed");
        result.Pages.Should().BeEmpty();
    }

    // ─── Cancellation ────────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_AlreadyCancelled_ThrowsOrReturnsError()
    {
        var pdf = CreatePdf("Some text", pages: 5);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // The method wraps in try/catch, so it either throws OperationCanceledException
        // or returns an error result
        try
        {
            var result = await _sut.ExtractAsync(pdf, cts.Token);
            // If it catches the cancellation, it returns an error
            result.Success.Should().BeFalse();
        }
        catch (OperationCanceledException)
        {
            // Also acceptable
        }
    }

    // ─── Zero-byte file ──────────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_ZeroByteFile_ReturnsError()
    {
        var filePath = Path.Combine(_tempDir, "empty.pdf");
        File.WriteAllBytes(filePath, []);

        var result = await _sut.ExtractAsync(filePath);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    // ─── Full text assembly ──────────────────────────────────────

    [Fact]
    public async Task ExtractAsync_EmptyPdf_FullTextIsEmpty()
    {
        var pdf = CreateEmptyPdf();

        var result = await _sut.ExtractAsync(pdf);

        result.FullText.Should().BeEmpty();
    }

    // ─── Error field on success ──────────────────────────────────

    [Fact]
    public async Task ExtractAsync_Success_ErrorIsNull()
    {
        var pdf = CreatePdf("Legal text");

        var result = await _sut.ExtractAsync(pdf);

        result.Error.Should().BeNull();
    }
}


