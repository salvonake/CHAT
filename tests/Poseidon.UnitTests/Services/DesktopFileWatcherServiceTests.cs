using System.IO;
using FluentAssertions;
using Poseidon.Application.Commands;
using Poseidon.Desktop;
using Poseidon.Desktop.Services;
using Poseidon.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;
using Moq;

namespace Poseidon.UnitTests.Services;

public sealed class DesktopFileWatcherServiceTests : IDisposable
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IDocumentStore> _docStore = new();
    private readonly Mock<ILogger<DesktopFileWatcherService>> _logger = new();
    private readonly string _tempDir;
    private readonly DataPaths _paths;

    public DesktopFileWatcherServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FW_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _paths = new DataPaths
        {
            DataDirectory = _tempDir,
            ModelsDirectory = Path.Combine(_tempDir, "models"),
            VectorDbPath = Path.Combine(_tempDir, "vectors.db"),
            HnswIndexPath = Path.Combine(_tempDir, "vectors.hnsw"),
            DocumentDbPath = Path.Combine(_tempDir, "docs.db"),
            AuditDbPath = Path.Combine(_tempDir, "audit.db"),
            WatchDirectory = Path.Combine(_tempDir, "watch")
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    private DesktopFileWatcherService CreateSut() =>
        new(_mediator.Object, _docStore.Object, _paths, _logger.Object);

    // ─── Construction ────────────────────────────────────────────

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var sut = CreateSut();
        sut.Should().NotBeNull();
    }

    // ─── ExecuteAsync creates watch directory ────────────────────

    [Fact]
    public async Task ExecuteAsync_CreatesWatchDirectory()
    {
        // The watch directory should not exist yet
        if (Directory.Exists(_paths.WatchDirectory))
            Directory.Delete(_paths.WatchDirectory, true);

        var sut = CreateSut();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        try { await sut.StartAsync(cts.Token); }
        catch (OperationCanceledException) { /* expected */ }

        await Task.Delay(200);

        try { await sut.StopAsync(CancellationToken.None); }
        catch { /* ok */ }

        Directory.Exists(_paths.WatchDirectory).Should().BeTrue();
    }

    // ─── Initial scan ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_InitialScan_QueuesPdfsInDirectory()
    {
        Directory.CreateDirectory(_paths.WatchDirectory);
        File.WriteAllText(Path.Combine(_paths.WatchDirectory, "doc1.pdf"), "dummy");
        File.WriteAllText(Path.Combine(_paths.WatchDirectory, "doc2.pdf"), "dummy");
        File.WriteAllText(Path.Combine(_paths.WatchDirectory, "readme.txt"), "not a pdf");

        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new IngestDocumentResult { Success = true, ChunksCreated = 5 });

        var sut = CreateSut();
        using var cts = new CancellationTokenSource();

        await sut.StartAsync(cts.Token);

        // Wait for debounce (2s) + batch interval (5s) + processing buffer
        await Task.Delay(8000);

        cts.Cancel();
        try { await sut.StopAsync(CancellationToken.None); }
        catch { /* ok */ }

        // Should have processed the 2 PDFs (not the .txt)
        _mediator.Verify(
            m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ─── FileEvent raised on detection ──────────────────────────

    [Fact]
    public async Task ExecuteAsync_NewPdfCreated_RaisesFileEvent()
    {
        Directory.CreateDirectory(_paths.WatchDirectory);

        var events = new List<(string path, FileWatcherEventType type)>();

        var sut = CreateSut();
        sut.FileEvent += (path, type) => events.Add((path, type));

        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new IngestDocumentResult { Success = true, ChunksCreated = 1 });

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        // Give watcher time to start
        await Task.Delay(500);

        // Create a PDF file to trigger the watcher
        File.WriteAllText(Path.Combine(_paths.WatchDirectory, "new.pdf"), "pdf content");

        // Wait for detection
        await Task.Delay(1000);

        cts.Cancel();
        try { await sut.StopAsync(CancellationToken.None); }
        catch { /* ok */ }

        events.Should().Contain(e => e.type == FileWatcherEventType.Created);
    }

    // ─── Ingestion success raises Indexed event ─────────────────

    [Fact]
    public async Task ExecuteAsync_SuccessfulIngestion_RaisesIndexedEvent()
    {
        Directory.CreateDirectory(_paths.WatchDirectory);
        File.WriteAllText(Path.Combine(_paths.WatchDirectory, "indexed.pdf"), "content");

        var events = new List<(string path, FileWatcherEventType type)>();

        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new IngestDocumentResult { Success = true, ChunksCreated = 3 });

        var sut = CreateSut();
        sut.FileEvent += (path, type) => events.Add((path, type));

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        // Wait for debounce + batch interval + processing
        await Task.Delay(8000);

        cts.Cancel();
        try { await sut.StopAsync(CancellationToken.None); }
        catch { /* ok */ }

        events.Should().Contain(e => e.type == FileWatcherEventType.Indexed);
    }

    // ─── Ingestion failure raises Failed event ──────────────────

    [Fact]
    public async Task ExecuteAsync_FailedIngestion_RaisesFailedEvent()
    {
        Directory.CreateDirectory(_paths.WatchDirectory);
        File.WriteAllText(Path.Combine(_paths.WatchDirectory, "bad.pdf"), "corrupted");

        var events = new List<(string path, FileWatcherEventType type)>();

        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new IngestDocumentResult { Success = false, Error = "corrupt" });

        var sut = CreateSut();
        sut.FileEvent += (path, type) => events.Add((path, type));

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        await Task.Delay(8000);

        cts.Cancel();
        try { await sut.StopAsync(CancellationToken.None); }
        catch { /* ok */ }

        events.Should().Contain(e => e.type == FileWatcherEventType.Failed);
    }

    // ─── Ingestion exception raises Failed event ────────────────

    [Fact]
    public async Task ExecuteAsync_IngestionThrows_RaisesFailedEvent()
    {
        Directory.CreateDirectory(_paths.WatchDirectory);
        File.WriteAllText(Path.Combine(_paths.WatchDirectory, "crash.pdf"), "crash data");

        var events = new List<(string path, FileWatcherEventType type)>();

        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new Exception("boom"));

        var sut = CreateSut();
        sut.FileEvent += (path, type) => events.Add((path, type));

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        await Task.Delay(8000);

        cts.Cancel();
        try { await sut.StopAsync(CancellationToken.None); }
        catch { /* ok */ }

        events.Should().Contain(e => e.type == FileWatcherEventType.Failed);
    }

    // ─── IngestionProgress event ────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_IngestionInProgress_RaisesProgressEvent()
    {
        Directory.CreateDirectory(_paths.WatchDirectory);
        File.WriteAllText(Path.Combine(_paths.WatchDirectory, "progress.pdf"), "data");

        var progressEvents = new List<(string file, int current, int total)>();

        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new IngestDocumentResult { Success = true, ChunksCreated = 1 });

        var sut = CreateSut();
        sut.IngestionProgress += (file, current, total) =>
            progressEvents.Add((file, current, total));

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        await Task.Delay(8000);

        cts.Cancel();
        try { await sut.StopAsync(CancellationToken.None); }
        catch { /* ok */ }

        progressEvents.Should().NotBeEmpty();
        progressEvents[0].current.Should().Be(1);
        progressEvents[0].total.Should().BeGreaterThanOrEqualTo(1);
    }

    // ─── Dispose ─────────────────────────────────────────────────

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var sut = CreateSut();
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

    // ─── Sends correct command fields ────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SendsFileWatcherAsUserId()
    {
        Directory.CreateDirectory(_paths.WatchDirectory);
        File.WriteAllText(Path.Combine(_paths.WatchDirectory, "user.pdf"), "data");

        IngestDocumentCommand? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
                 .Callback<IRequest<IngestDocumentResult>, CancellationToken>((c, _) =>
                     captured = (IngestDocumentCommand)c)
                 .ReturnsAsync(new IngestDocumentResult { Success = true });

        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        await Task.Delay(8000);

        cts.Cancel();
        try { await sut.StopAsync(CancellationToken.None); }
        catch { /* ok */ }

        captured.Should().NotBeNull();
        captured!.UserId.Should().Be("FileWatcher");
        captured.FilePath.Should().EndWith("user.pdf");
    }
}


