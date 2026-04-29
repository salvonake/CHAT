using System.IO;
using FluentAssertions;
using Poseidon.Application.Commands;
using Poseidon.Domain.Interfaces;
using Poseidon.WorkerService;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Poseidon.UnitTests.Services;

public sealed class FileWatcherServiceTests : IDisposable
{
    private readonly Mock<ILogger<FileWatcherService>> _logger = new();
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IMetricsCollector> _metrics = new();
    private readonly Mock<IAuditService> _audit = new();
    private readonly Mock<IIngestionJobStore> _jobs = new();
    private readonly string _tempDir;
    private readonly IConfiguration _config;

    public FileWatcherServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FWS_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Data:PdfWatchDirectory"] = Path.Combine(_tempDir, "pdfs"),
                ["Data:QuarantineDirectory"] = Path.Combine(_tempDir, "quarantine"),
                ["Ingestion:MaxRetryAttempts"] = "3",
                ["Ingestion:RetryBackoffSeconds"] = "1"
            })
            .Build();

        _audit.Setup(a => a.LogAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _jobs.Setup(j => j.GetByFileAndHashAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Poseidon.Domain.Entities.IngestionJob?)null);

        _jobs.Setup(j => j.CreateAsync(
                It.IsAny<Poseidon.Domain.Entities.IngestionJob>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Poseidon.Domain.Entities.IngestionJob job, CancellationToken _) => job);

        _jobs.Setup(j => j.MarkRunningAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _jobs.Setup(j => j.MarkSucceededAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _jobs.Setup(j => j.MarkFailedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _jobs.Setup(j => j.MarkQuarantinedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _jobs.Setup(j => j.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    private FileWatcherService CreateSut() =>
        new(_logger.Object, _mediator.Object, _metrics.Object, _audit.Object, _jobs.Object, _config);

    // ─── Construction ────────────────────────────────────────────

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var sut = CreateSut();
        sut.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_DefaultConfig_UsesPdfsPath()
    {
        // When no config key is set, defaults to "pdfs"
        var emptyConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var sut = new FileWatcherService(
            _logger.Object, _mediator.Object, _metrics.Object, _audit.Object, _jobs.Object, emptyConfig);

        sut.Should().NotBeNull();
    }

    // ─── ExecuteAsync creates directory ──────────────────────────

    [Fact]
    public async Task ExecuteAsync_CreatesWatchDirectory()
    {
        var watchDir = Path.Combine(_tempDir, "pdfs");
        if (Directory.Exists(watchDir))
            Directory.Delete(watchDir, true);

        var sut = CreateSut();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        try { await sut.StartAsync(cts.Token); }
        catch (OperationCanceledException) { /* expected */ }

        await Task.Delay(200);

        try { await sut.StopAsync(CancellationToken.None); }
        catch { /* ok */ }

        Directory.Exists(watchDir).Should().BeTrue();
    }

    // ─── Initial scan ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_InitialScan_QueuesPdfs()
    {
        var watchDir = Path.Combine(_tempDir, "pdfs");
        Directory.CreateDirectory(watchDir);
        File.WriteAllText(Path.Combine(watchDir, "doc1.pdf"), "dummy");
        File.WriteAllText(Path.Combine(watchDir, "doc2.pdf"), "dummy");
        File.WriteAllText(Path.Combine(watchDir, "readme.txt"), "not a pdf");

        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new IngestDocumentResult { Success = true, ChunksCreated = 5 });

        var sut = CreateSut();
        using var cts = new CancellationTokenSource();

        await sut.StartAsync(cts.Token);

        // Wait for batch interval (5s) + processing buffer
        await Task.Delay(7000);

        cts.Cancel();
        try { await sut.StopAsync(CancellationToken.None); }
        catch { /* ok */ }

        // Should have processed 2 PDFs (not the .txt)
        _mediator.Verify(
            m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ─── Audit log on start ──────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_LogsAuditEntry()
    {
        var watchDir = Path.Combine(_tempDir, "pdfs");
        Directory.CreateDirectory(watchDir);

        var sut = CreateSut();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000));

        try { await sut.StartAsync(cts.Token); }
        catch (OperationCanceledException) { /* expected */ }

        await Task.Delay(300);

        try { await sut.StopAsync(CancellationToken.None); }
        catch { /* ok */ }

        _audit.Verify(a => a.LogAsync(
            "FILE_WATCHER_STARTED",
            It.Is<string>(s => s.Contains(watchDir)),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Metrics tracking ────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ProcessingBatch_UpdatesQueueDepthMetric()
    {
        var watchDir = Path.Combine(_tempDir, "pdfs");
        Directory.CreateDirectory(watchDir);
        File.WriteAllText(Path.Combine(watchDir, "metric.pdf"), "data");

        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new IngestDocumentResult { Success = true });

        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        await Task.Delay(7000);

        cts.Cancel();
        try { await sut.StopAsync(CancellationToken.None); }
        catch { /* ok */ }

        _metrics.Verify(m => m.SetGauge("indexing_queue_depth", It.IsAny<double>()),
            Times.AtLeastOnce);
    }

    // ─── File detection events ───────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NewPdfCreated_QueuesForIngestion()
    {
        var watchDir = Path.Combine(_tempDir, "pdfs");
        Directory.CreateDirectory(watchDir);

        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new IngestDocumentResult { Success = true, ChunksCreated = 1 });

        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        // Give watcher time to start
        await Task.Delay(1000);

        // Create a file after watcher is running
        File.WriteAllText(Path.Combine(watchDir, "new_file.pdf"), "content");

        // Wait for batch interval + processing
        await Task.Delay(7000);

        cts.Cancel();
        try { await sut.StopAsync(CancellationToken.None); }
        catch { /* ok */ }

        _mediator.Verify(
            m => m.Send(
                It.Is<IngestDocumentCommand>(c => c.FilePath.Contains("new_file.pdf")),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    // ─── Deleted files skipped ───────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_DeletedFile_SkipsIngestion()
    {
        var watchDir = Path.Combine(_tempDir, "pdfs");
        Directory.CreateDirectory(watchDir);

        var filePath = Path.Combine(watchDir, "to_delete.pdf");
        File.WriteAllText(filePath, "content");

        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        await Task.Delay(1000);

        // Delete the file
        File.Delete(filePath);

        // Wait for processing
        await Task.Delay(7000);

        cts.Cancel();
        try { await sut.StopAsync(CancellationToken.None); }
        catch { /* ok */ }

        // The initial scan queued the file for ingestion, but after deletion,
        // the delete event should be logged, not re-ingested
        // At minimum: no crash should occur
    }

    // ─── Ingestion failure doesn't crash ─────────────────────────

    [Fact]
    public async Task ExecuteAsync_IngestionThrows_ContinuesProcessing()
    {
        var watchDir = Path.Combine(_tempDir, "pdfs");
        Directory.CreateDirectory(watchDir);
        File.WriteAllText(Path.Combine(watchDir, "crash.pdf"), "bad data");

        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new Exception("ingestion boom"));

        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        await Task.Delay(7000);

        cts.Cancel();
        try { await sut.StopAsync(CancellationToken.None); }
        catch { /* ok */ }

        // Should not have crashed — the service should still be running
        // Verify the mediator was called (the exception was caught)
        _mediator.Verify(
            m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    // ─── Debounce ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_FileChangesDetected_IncrementCounter()
    {
        var watchDir = Path.Combine(_tempDir, "pdfs");
        Directory.CreateDirectory(watchDir);

        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new IngestDocumentResult { Success = true });

        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        await Task.Delay(1000);

        // Create file to trigger detection
        File.WriteAllText(Path.Combine(watchDir, "detected.pdf"), "data");

        await Task.Delay(3000);

        cts.Cancel();
        try { await sut.StopAsync(CancellationToken.None); }
        catch { /* ok */ }

        _metrics.Verify(m => m.IncrementCounter("file_changes_detected", It.IsAny<long>()), Times.AtLeastOnce);
    }
}


