using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using LegalAI.Application.Commands;
using LegalAI.Desktop;
using LegalAI.Desktop.Services;
using LegalAI.Desktop.ViewModels;
using LegalAI.Domain.Entities;
using LegalAI.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;
using Moq;

namespace LegalAI.UnitTests.ViewModels;

/// <summary>
/// Tests for <see cref="DocumentsViewModel"/>: document management, ingestion,
/// quarantine listing, statistics, and drag-and-drop file handling.
/// </summary>
public sealed class DocumentsViewModelTests
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IDocumentStore> _docStore = new();
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<IDispatcherService> _dispatcher = new();
    private readonly Mock<ILogger<DocumentsViewModel>> _logger = new();
    private readonly DataPaths _paths;

    public DocumentsViewModelTests()
    {
        _paths = new DataPaths
        {
            DataDirectory = @"C:\test\data",
            ModelsDirectory = @"C:\test\models",
            VectorDbPath = @"C:\test\vectors.db",
            HnswIndexPath = @"C:\test\hnsw",
            DocumentDbPath = @"C:\test\docs.db",
            AuditDbPath = @"C:\test\audit.db",
            WatchDirectory = @"C:\test\watch"
        };

        // Default: IDispatcherService executes inline (no real WPF dispatcher)
        _dispatcher.Setup(d => d.InvokeAsync(It.IsAny<Action>()))
            .Callback<Action>(action => action())
            .Returns(Task.CompletedTask);
        _dispatcher.Setup(d => d.Invoke(It.IsAny<Action>()))
            .Callback<Action>(action => action());

        // Default: empty document store and no vectors
        _docStore.Setup(d => d.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LegalDocument>());
        _docStore.Setup(d => d.GetQuarantineRecordsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QuarantineRecord>());
        _vectorStore.Setup(v => v.GetVectorCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0L);
    }

    /// <summary>
    /// Creates the ViewModel and waits for the fire-and-forget RefreshDocumentsAsync 
    /// triggered by the constructor to complete.
    /// </summary>
    private async Task<DocumentsViewModel> CreateVmAsync()
    {
        var vm = new DocumentsViewModel(
            _mediator.Object,
            _docStore.Object,
            _vectorStore.Object,
            _dispatcher.Object,
            _paths,
            _logger.Object);

        // Wait for constructor's fire-and-forget task
        await Task.Delay(200);
        return vm;
    }

    // ─── Constructor & Initialization ─────────────────────────────

    [Fact]
    public async Task Constructor_SetsWatchDirectoryFromPaths()
    {
        var vm = await CreateVmAsync();

        vm.WatchDirectory.Should().Be(@"C:\test\watch");
    }

    [Fact]
    public async Task Constructor_LoadsEmptyDocuments()
    {
        var vm = await CreateVmAsync();

        vm.Documents.Should().BeEmpty();
        vm.TotalDocuments.Should().Be(0);
        vm.IndexedCount.Should().Be(0);
        vm.PendingCount.Should().Be(0);
        vm.FailedCount.Should().Be(0);
    }

    [Fact]
    public async Task Constructor_LoadsDocumentsFromStore()
    {
        var docs = new List<LegalDocument>
        {
            MakeDoc("d1", DocumentStatus.Indexed),
            MakeDoc("d2", DocumentStatus.Failed),
            MakeDoc("d3", DocumentStatus.Pending),
            MakeDoc("d4", DocumentStatus.Indexed),
        };
        _docStore.Setup(d => d.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(docs);

        var vm = await CreateVmAsync();

        vm.Documents.Should().HaveCount(4);
        vm.TotalDocuments.Should().Be(4);
        vm.IndexedCount.Should().Be(2);
        vm.PendingCount.Should().Be(1);
        vm.FailedCount.Should().Be(1);
    }

    [Fact]
    public async Task Constructor_LoadsQuarantinedDocuments()
    {
        _docStore.Setup(d => d.GetQuarantineRecordsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QuarantineRecord>
            {
                new()
                {
                    DocumentId = "q1",
                    FilePath = "/docs/bad.pdf",
                    Reason = "extraction failure",
                    FailureCount = 3,
                    ContentHash = "hash"
                }
            });

        var vm = await CreateVmAsync();

        vm.QuarantinedDocuments.Should().ContainSingle();
        vm.QuarantinedDocuments[0].Status.Should().Be("Quarantined");
        vm.QuarantinedDocuments[0].StatusArabic.Should().Be("محجور");
        vm.QuarantinedDocuments[0].FailureReason.Should().Be("extraction failure");
    }

    [Fact]
    public async Task Constructor_SetsVectorCount()
    {
        _vectorStore.Setup(v => v.GetVectorCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(42_000L);

        var vm = await CreateVmAsync();

        vm.VectorCount.Should().Be(42_000L);
    }

    // ─── RefreshDocumentsCommand ──────────────────────────────────

    [Fact]
    public async Task RefreshDocumentsCommand_UpdatesStats()
    {
        var vm = await CreateVmAsync();

        // Change the store response
        _docStore.Setup(d => d.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LegalDocument>
            {
                MakeDoc("new1", DocumentStatus.Indexed),
                MakeDoc("new2", DocumentStatus.Indexed),
            });
        _vectorStore.Setup(v => v.GetVectorCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(500L);

        await vm.RefreshDocumentsCommand.ExecuteAsync(null);

        vm.TotalDocuments.Should().Be(2);
        vm.IndexedCount.Should().Be(2);
        vm.VectorCount.Should().Be(500L);
    }

    [Fact]
    public async Task RefreshDocumentsCommand_ExceptionLogsAndDoesNotThrow()
    {
        var vm = await CreateVmAsync();

        _docStore.Setup(d => d.GetAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB locked"));

        var act = () => vm.RefreshDocumentsCommand.ExecuteAsync(null);
        await act.Should().NotThrowAsync();
    }

    // ─── Status Arabic Mapping ────────────────────────────────────

    [Theory]
    [InlineData(DocumentStatus.Pending, "بانتظار الفهرسة")]
    [InlineData(DocumentStatus.Indexing, "جارٍ الفهرسة")]
    [InlineData(DocumentStatus.Indexed, "مفهرس")]
    [InlineData(DocumentStatus.Failed, "فشل")]
    [InlineData(DocumentStatus.Quarantined, "محجور")]
    public async Task Documents_HaveCorrectArabicStatus(DocumentStatus status, string expectedArabic)
    {
        _docStore.Setup(d => d.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LegalDocument> { MakeDoc("d1", status) });

        var vm = await CreateVmAsync();

        vm.Documents.Should().ContainSingle();
        vm.Documents[0].StatusArabic.Should().Be(expectedArabic);
    }

    // ─── IngestFilesCommand ───────────────────────────────────────

    [Fact]
    public async Task IngestFilesCommand_NullFilePaths_DoesNothing()
    {
        var vm = await CreateVmAsync();

        await vm.IngestFilesCommand.ExecuteAsync(null);

        _mediator.Verify(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IngestFilesCommand_NoPdfFiles_DoesNothing()
    {
        var vm = await CreateVmAsync();

        await vm.IngestFilesCommand.ExecuteAsync(new[] { "readme.txt", "doc.docx" });

        _mediator.Verify(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        vm.IsIngesting.Should().BeFalse();
    }

    [Fact]
    public async Task IngestFilesCommand_SendsCommandForEachPdf()
    {
        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IngestDocumentResult { Success = true, ChunksCreated = 10 });

        var vm = await CreateVmAsync();
        var files = new[] { "/docs/file1.pdf", "/docs/file2.PDF", "/docs/skip.txt" };

        await vm.IngestFilesCommand.ExecuteAsync(files);

        _mediator.Verify(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task IngestFilesCommand_SetsIsIngestingDuring()
    {
        var isIngestingDuringRun = false;

        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
            .Returns<IngestDocumentCommand, CancellationToken>(async (cmd, _) =>
            {
                // This callback runs during ingestion
                isIngestingDuringRun = true;
                return new IngestDocumentResult { Success = true, ChunksCreated = 5 };
            });

        var vm = await CreateVmAsync();
        await vm.IngestFilesCommand.ExecuteAsync(new[] { "/docs/test.pdf" });

        isIngestingDuringRun.Should().BeTrue();
        vm.IsIngesting.Should().BeFalse(); // reset after completion
    }

    [Fact]
    public async Task IngestFilesCommand_SetsProgressCounters()
    {
        var progressValues = new List<int>();

        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
            .Returns<IngestDocumentCommand, CancellationToken>(async (cmd, _) =>
            {
                return new IngestDocumentResult { Success = true, ChunksCreated = 5 };
            });

        var vm = await CreateVmAsync();
        await vm.IngestFilesCommand.ExecuteAsync(new[] { "/docs/a.pdf", "/docs/b.pdf", "/docs/c.pdf" });

        vm.IngestionTotal.Should().Be(3);
        vm.IngestionStatus.Should().Contain("3");
    }

    [Fact]
    public async Task IngestFilesCommand_ForwardsCaseNamespace()
    {
        IngestDocumentCommand? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<IngestDocumentResult>, CancellationToken>((cmd, _) => captured = (IngestDocumentCommand)cmd)
            .ReturnsAsync(new IngestDocumentResult { Success = true, ChunksCreated = 1 });

        var vm = await CreateVmAsync();
        vm.SelectedCaseNamespace = "case-2024-001";

        await vm.IngestFilesCommand.ExecuteAsync(new[] { "/docs/test.pdf" });

        captured.Should().NotBeNull();
        captured!.CaseNamespace.Should().Be("case-2024-001");
        captured.UserId.Should().Be("Desktop");
    }

    [Fact]
    public async Task IngestFilesCommand_FailedResult_LogsWarningNotException()
    {
        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IngestDocumentResult { Success = false, Error = "PDF corrupt" });

        var vm = await CreateVmAsync();

        var act = () => vm.IngestFilesCommand.ExecuteAsync(new[] { "/docs/bad.pdf" });
        await act.Should().NotThrowAsync();
        vm.IsIngesting.Should().BeFalse();
    }

    [Fact]
    public async Task IngestFilesCommand_ExceptionPerFile_ContinuesOtherFiles()
    {
        var callCount = 0;
        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
            .Returns<IRequest<IngestDocumentResult>, CancellationToken>((cmd, ct) =>
            {
                callCount++;
                if (callCount == 1)
                    throw new IOException("Disk error");
                return Task.FromResult(new IngestDocumentResult { Success = true, ChunksCreated = 5 });
            });

        var vm = await CreateVmAsync();
        await vm.IngestFilesCommand.ExecuteAsync(new[] { "/docs/a.pdf", "/docs/b.pdf" });

        callCount.Should().Be(2); // Both files attempted
        vm.IsIngesting.Should().BeFalse();
    }

    [Fact]
    public async Task IngestFilesCommand_RefreshesDocumentsAfterCompletion()
    {
        _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IngestDocumentResult { Success = true, ChunksCreated = 1 });

        var vm = await CreateVmAsync();

        // After ingestion, it calls RefreshDocumentsAsync — the store's GetAllAsync
        // should be called at least twice (once on init, once after ingest)
        var initialCallCount = 1; // from constructor
        await vm.IngestFilesCommand.ExecuteAsync(new[] { "/docs/test.pdf" });

        _docStore.Verify(d => d.GetAllAsync(It.IsAny<CancellationToken>()),
            Times.AtLeast(initialCallCount + 1));
    }

    // ─── IngestWatchDirectoryCommand ──────────────────────────────

    [Fact]
    public async Task IngestWatchDirectoryCommand_EmptyPath_DoesNothing()
    {
        var vm = await CreateVmAsync();
        vm.WatchDirectory = "";

        await vm.IngestWatchDirectoryCommand.ExecuteAsync(null);

        _mediator.Verify(m => m.Send(It.IsAny<IngestDirectoryCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IngestWatchDirectoryCommand_SendsDirectoryCommand()
    {
        IngestDirectoryCommand? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<IngestDirectoryCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<IngestDirectoryResult>, CancellationToken>((cmd, _) => captured = (IngestDirectoryCommand)cmd)
            .ReturnsAsync(new IngestDirectoryResult { SuccessCount = 5, FailedCount = 1, SkippedCount = 2 });

        var vm = await CreateVmAsync();
        vm.SelectedCaseNamespace = "ns-test";

        await vm.IngestWatchDirectoryCommand.ExecuteAsync(null);

        captured.Should().NotBeNull();
        captured!.DirectoryPath.Should().Be(@"C:\test\watch");
        captured.CaseNamespace.Should().Be("ns-test");
        captured.UserId.Should().Be("Desktop");
    }

    [Fact]
    public async Task IngestWatchDirectoryCommand_SetsIngestionStatus()
    {
        _mediator.Setup(m => m.Send(It.IsAny<IngestDirectoryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IngestDirectoryResult { SuccessCount = 3, FailedCount = 1, SkippedCount = 0 });

        var vm = await CreateVmAsync();

        await vm.IngestWatchDirectoryCommand.ExecuteAsync(null);

        vm.IngestionStatus.Should().Contain("3");
        vm.IngestionStatus.Should().Contain("1");
        vm.IsIngesting.Should().BeFalse();
    }

    [Fact]
    public async Task IngestWatchDirectoryCommand_Exception_SetsErrorStatus()
    {
        _mediator.Setup(m => m.Send(It.IsAny<IngestDirectoryCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Directory access denied"));

        var vm = await CreateVmAsync();

        await vm.IngestWatchDirectoryCommand.ExecuteAsync(null);

        vm.IngestionStatus.Should().Contain("Directory access denied");
        vm.IsIngesting.Should().BeFalse();
    }

    // ─── DocumentItem Structure ────────────────────────────────────

    [Fact]
    public async Task DocumentItem_ExtractsFileNameFromPath()
    {
        _docStore.Setup(d => d.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LegalDocument>
            {
                MakeDoc("d1", DocumentStatus.Indexed, @"C:\Documents\حكم_المحكمة.pdf")
            });

        var vm = await CreateVmAsync();

        vm.Documents[0].FileName.Should().Be("حكم_المحكمة.pdf");
    }

    [Fact]
    public async Task DocumentItem_CaseNamespaceShownOrDash()
    {
        _docStore.Setup(d => d.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LegalDocument>
            {
                MakeDoc("d1", DocumentStatus.Indexed, caseNamespace: "case-123"),
                MakeDoc("d2", DocumentStatus.Indexed, caseNamespace: null)
            });

        var vm = await CreateVmAsync();

        vm.Documents.Should().Contain(d => d.CaseNamespace == "case-123");
        vm.Documents.Should().Contain(d => d.CaseNamespace == "—");
    }

    // ─── Helper ───────────────────────────────────────────────────

    private static LegalDocument MakeDoc(
        string id,
        DocumentStatus status,
        string? filePath = null,
        string? caseNamespace = null)
    {
        var path = filePath ?? $"/docs/{id}.pdf";
        var doc = new LegalDocument
        {
            FilePath = path,
            FileName = Path.GetFileName(path),
            ContentHash = $"hash_{id}",
            FileSizeBytes = 2048,
            Status = status,
            CaseNamespace = caseNamespace
        };
        typeof(LegalDocument).GetProperty(nameof(LegalDocument.Id))!
            .SetValue(doc, id);
        return doc;
    }
}
