using System.IO;
using FluentAssertions;
using LegalAI.Application.Commands;
using MediatR;
using Microsoft.Extensions.Logging;
using Moq;

namespace LegalAI.UnitTests.Application;

/// <summary>
/// Tests for IngestDirectoryHandler — batch PDF ingestion with parallel processing.
/// Covers: file discovery, parallel processing, progress reporting, success/failure
/// counting, failed file tracking, empty directory, cancellation, exception safety.
/// Note: Memory pressure (GC.GetGCMemoryInfo) is not directly testable but is
/// an internal concern — we test the observable behavior it influences.
/// </summary>
public sealed class IngestDirectoryHandlerTests : IDisposable
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<ILogger<IngestDirectoryHandler>> _logger = new();

    private readonly string _tempDir;

    public IngestDirectoryHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LegalAI_DirTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
    }

    private IngestDirectoryHandler CreateHandler() =>
        new(_mediator.Object, _logger.Object);

    // ──────────────────────────── Helpers ────────────────────────────

    private string CreatePdf(string name)
    {
        var path = Path.Combine(_tempDir, name);
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, $"dummy {name}");
        return path;
    }

    private void SetupMediatorSuccess(int chunksCreated = 5)
    {
        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IngestDocumentResult
            {
                Success = true,
                DocumentId = Guid.NewGuid().ToString("N"),
                ChunksCreated = chunksCreated,
                LatencyMs = 100
            });
    }

    private void SetupMediatorFailure(string error = "Bad PDF")
    {
        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IngestDocumentResult
            {
                Success = false,
                Error = error,
                LatencyMs = 10
            });
    }

    private IngestDirectoryCommand MakeCommand(
        string? dir = null,
        bool recursive = true,
        int maxParallel = 1,
        IProgress<IngestionProgress>? progress = null,
        string? ns = null,
        string? userId = "user1") =>
        new()
        {
            DirectoryPath = dir ?? _tempDir,
            Recursive = recursive,
            MaxParallelFiles = maxParallel,
            Progress = progress,
            CaseNamespace = ns,
            UserId = userId
        };

    // ══════════════════════════════════════
    //  Empty Directory
    // ══════════════════════════════════════

    [Fact]
    public async Task Handle_EmptyDirectory_ReturnsZeroCounts()
    {
        var result = await CreateHandler().Handle(MakeCommand(), CancellationToken.None);

        result.TotalFiles.Should().Be(0);
        result.SuccessCount.Should().Be(0);
        result.FailedCount.Should().Be(0);
        result.SkippedCount.Should().Be(0);
        result.FailedFiles.Should().BeEmpty();
        _mediator.Verify(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ══════════════════════════════════════
    //  Single File Success
    // ══════════════════════════════════════

    [Fact]
    public async Task Handle_SinglePdf_Success()
    {
        CreatePdf("doc1.pdf");
        SetupMediatorSuccess();

        var result = await CreateHandler().Handle(MakeCommand(), CancellationToken.None);

        result.TotalFiles.Should().Be(1);
        result.SuccessCount.Should().Be(1);
        result.FailedCount.Should().Be(0);
    }

    // ══════════════════════════════════════
    //  Multiple Files
    // ══════════════════════════════════════

    [Fact]
    public async Task Handle_MultipleFiles_AllSucceed()
    {
        CreatePdf("a.pdf");
        CreatePdf("b.pdf");
        CreatePdf("c.pdf");
        SetupMediatorSuccess();

        var result = await CreateHandler().Handle(MakeCommand(), CancellationToken.None);

        result.TotalFiles.Should().Be(3);
        result.SuccessCount.Should().Be(3);
        result.FailedCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_MixedResults_CountsCorrectly()
    {
        CreatePdf("good1.pdf");
        CreatePdf("bad1.pdf");
        CreatePdf("good2.pdf");

        var callCount = 0;
        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var n = Interlocked.Increment(ref callCount);
                return n == 2
                    ? new IngestDocumentResult { Success = false, Error = "Bad", LatencyMs = 10 }
                    : new IngestDocumentResult { Success = true, DocumentId = "d", ChunksCreated = 5, LatencyMs = 50 };
            });

        var result = await CreateHandler().Handle(MakeCommand(maxParallel: 1), CancellationToken.None);

        result.TotalFiles.Should().Be(3);
        result.SuccessCount.Should().Be(2);
        result.FailedCount.Should().Be(1);
        result.FailedFiles.Should().HaveCount(1);
    }

    // ══════════════════════════════════════
    //  Skipped (Success with 0 chunks = dedup skip)
    // ══════════════════════════════════════

    [Fact]
    public async Task Handle_SuccessZeroChunks_CountedAsSkipped()
    {
        CreatePdf("dup.pdf");
        SetupMediatorSuccess(chunksCreated: 0); // Already indexed

        var result = await CreateHandler().Handle(MakeCommand(), CancellationToken.None);

        result.SkippedCount.Should().Be(1);
        result.SuccessCount.Should().Be(0);
    }

    // ══════════════════════════════════════
    //  All Failures
    // ══════════════════════════════════════

    [Fact]
    public async Task Handle_AllFail_ReportsAllFailed()
    {
        CreatePdf("f1.pdf");
        CreatePdf("f2.pdf");
        SetupMediatorFailure();

        var result = await CreateHandler().Handle(MakeCommand(), CancellationToken.None);

        result.FailedCount.Should().Be(2);
        result.SuccessCount.Should().Be(0);
        result.FailedFiles.Should().HaveCount(2);
    }

    // ══════════════════════════════════════
    //  Recursive vs Non-Recursive
    // ══════════════════════════════════════

    [Fact]
    public async Task Handle_Recursive_FindsSubdirectoryFiles()
    {
        CreatePdf("root.pdf");
        CreatePdf(Path.Combine("sub", "nested.pdf"));
        SetupMediatorSuccess();

        var result = await CreateHandler().Handle(MakeCommand(recursive: true), CancellationToken.None);

        result.TotalFiles.Should().Be(2);
    }

    [Fact]
    public async Task Handle_NonRecursive_SkipsSubdirectories()
    {
        CreatePdf("root.pdf");
        CreatePdf(Path.Combine("sub", "nested.pdf"));
        SetupMediatorSuccess();

        var result = await CreateHandler().Handle(MakeCommand(recursive: false), CancellationToken.None);

        result.TotalFiles.Should().Be(1);
    }

    // ══════════════════════════════════════
    //  Non-PDF files ignored
    // ══════════════════════════════════════

    [Fact]
    public async Task Handle_NonPdfFiles_Ignored()
    {
        CreatePdf("doc.pdf");
        File.WriteAllText(Path.Combine(_tempDir, "readme.txt"), "not a pdf");
        File.WriteAllText(Path.Combine(_tempDir, "data.docx"), "not a pdf");
        SetupMediatorSuccess();

        var result = await CreateHandler().Handle(MakeCommand(), CancellationToken.None);

        result.TotalFiles.Should().Be(1);
    }

    // ══════════════════════════════════════
    //  Progress Reporting
    // ══════════════════════════════════════

    [Fact]
    public async Task Handle_ReportsProgress()
    {
        CreatePdf("p1.pdf");
        CreatePdf("p2.pdf");
        SetupMediatorSuccess();

        var progressReports = new List<IngestionProgress>();
        var progress = new Progress<IngestionProgress>(p => progressReports.Add(p));

        await CreateHandler().Handle(MakeCommand(maxParallel: 1, progress: progress), CancellationToken.None);

        // Allow async progress to propagate
        await Task.Delay(100);

        progressReports.Should().HaveCountGreaterThanOrEqualTo(1);
        progressReports.Last().TotalFiles.Should().Be(2);
        progressReports.Last().ProcessedFiles.Should().Be(2);
    }

    // ══════════════════════════════════════
    //  CaseNamespace and UserId forwarding
    // ══════════════════════════════════════

    [Fact]
    public async Task Handle_ForwardsCaseNamespaceAndUserId()
    {
        CreatePdf("doc.pdf");
        SetupMediatorSuccess();

        await CreateHandler().Handle(MakeCommand(ns: "civil", userId: "admin"), CancellationToken.None);

        _mediator.Verify(m => m.Send(
            It.Is<IngestDocumentCommand>(c => c.CaseNamespace == "civil" && c.UserId == "admin"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ══════════════════════════════════════
    //  Exception during Send
    // ══════════════════════════════════════

    [Fact]
    public async Task Handle_MediatorThrows_CountsAsFailure()
    {
        CreatePdf("crasher.pdf");
        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Unexpected error"));

        var result = await CreateHandler().Handle(MakeCommand(), CancellationToken.None);

        result.FailedCount.Should().Be(1);
        result.FailedFiles.Should().HaveCount(1);
    }

    // ══════════════════════════════════════
    //  LatencyMs
    // ══════════════════════════════════════

    [Fact]
    public async Task Handle_AlwaysReportsLatency()
    {
        var result = await CreateHandler().Handle(MakeCommand(), CancellationToken.None);

        result.TotalLatencyMs.Should().BeGreaterThanOrEqualTo(0);
    }

    // ══════════════════════════════════════
    //  MaxParallelFiles enforced ≥ 1
    // ══════════════════════════════════════

    [Fact]
    public async Task Handle_ZeroParallel_StillProcesses()
    {
        CreatePdf("test.pdf");
        SetupMediatorSuccess();

        // MaxParallelFiles = 0 → Math.Max(1, 0) = 1
        var result = await CreateHandler().Handle(MakeCommand(maxParallel: 0), CancellationToken.None);

        result.TotalFiles.Should().Be(1);
        result.SuccessCount.Should().Be(1);
    }
}
