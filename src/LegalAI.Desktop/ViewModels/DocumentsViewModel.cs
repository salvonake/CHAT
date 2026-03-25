using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LegalAI.Application.Commands;
using LegalAI.Desktop.Services;
using LegalAI.Domain.Entities;
using LegalAI.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace LegalAI.Desktop.ViewModels;

/// <summary>
/// ViewModel for the document management view. Handles PDF drag-and-drop,
/// ingestion monitoring, document listing, and quarantine management.
/// </summary>
public partial class DocumentsViewModel : ObservableObject
{
    private readonly IMediator _mediator;
    private readonly IDocumentStore _docStore;
    private readonly IVectorStore _vectorStore;
    private readonly IDispatcherService _dispatcher;
    private readonly DataPaths _paths;
    private readonly ILogger<DocumentsViewModel> _logger;

    // ── Document List ──
    [ObservableProperty]
    private ObservableCollection<DocumentItem> _documents = [];

    [ObservableProperty]
    private ObservableCollection<DocumentItem> _quarantinedDocuments = [];

    // ── Stats ──
    [ObservableProperty]
    private int _totalDocuments;

    [ObservableProperty]
    private int _indexedCount;

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private long _vectorCount;

    // ── Ingestion ──
    [ObservableProperty]
    private bool _isIngesting;

    [ObservableProperty]
    private string _ingestionStatus = "";

    [ObservableProperty]
    private int _ingestionProgress;

    [ObservableProperty]
    private int _ingestionTotal;

    [ObservableProperty]
    private string _watchDirectory = "";

    // ── Case Namespace ──
    [ObservableProperty]
    private string? _selectedCaseNamespace;

    public DocumentsViewModel(
        IMediator mediator,
        IDocumentStore docStore,
        IVectorStore vectorStore,
        IDispatcherService dispatcher,
        DataPaths paths,
        ILogger<DocumentsViewModel> logger)
    {
        _mediator = mediator;
        _docStore = docStore;
        _vectorStore = vectorStore;
        _dispatcher = dispatcher;
        _paths = paths;
        _logger = logger;
        _watchDirectory = paths.WatchDirectory;

        _ = RefreshDocumentsAsync();
    }

    [RelayCommand]
    private async Task RefreshDocumentsAsync()
    {
        try
        {
            var docs = await _docStore.GetAllAsync();
            var quarantined = await _docStore.GetQuarantineRecordsAsync();

            await _dispatcher.InvokeAsync(() =>
            {
                Documents.Clear();
                foreach (var doc in docs)
                {
                    Documents.Add(new DocumentItem
                    {
                        Id = doc.Id,
                        FileName = Path.GetFileName(doc.FilePath),
                        FilePath = doc.FilePath,
                        Status = doc.Status.ToString(),
                        StatusArabic = GetStatusArabic(doc.Status),
                        CaseNamespace = doc.CaseNamespace ?? "—",
                        IndexedAt = doc.IndexedAt.DateTime,
                        FailureCount = doc.FailureCount
                    });
                }

                QuarantinedDocuments.Clear();
                foreach (var q in quarantined)
                {
                    QuarantinedDocuments.Add(new DocumentItem
                    {
                        Id = q.DocumentId,
                        FileName = Path.GetFileName(q.FilePath),
                        FilePath = q.FilePath,
                        Status = "Quarantined",
                        StatusArabic = "محجور",
                        FailureReason = q.Reason
                    });
                }

                TotalDocuments = docs.Count;
                IndexedCount = docs.Count(d => d.Status == DocumentStatus.Indexed);
                PendingCount = docs.Count(d => d.Status == DocumentStatus.Pending || d.Status == DocumentStatus.Indexing);
                FailedCount = docs.Count(d => d.Status == DocumentStatus.Failed);
            });

            VectorCount = await _vectorStore.GetVectorCountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh documents");
        }
    }

    [RelayCommand]
    private async Task IngestFilesAsync(IEnumerable<string>? filePaths)
    {
        if (filePaths == null) return;

        var files = filePaths.Where(f =>
            f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();

        if (files.Count == 0) return;

        IsIngesting = true;
        IngestionTotal = files.Count;
        IngestionProgress = 0;

        try
        {
            foreach (var filePath in files)
            {
                IngestionProgress++;
                IngestionStatus = $"جارٍ فهرسة: {Path.GetFileName(filePath)} ({IngestionProgress}/{IngestionTotal})";

                try
                {
                    var result = await _mediator.Send(new IngestDocumentCommand
                    {
                        FilePath = filePath,
                        CaseNamespace = SelectedCaseNamespace,
                        UserId = "Desktop"
                    });

                    if (result.Success)
                    {
                        _logger.LogInformation("Ingested {File}: {Chunks} chunks",
                            Path.GetFileName(filePath), result.ChunksCreated);
                    }
                    else
                    {
                        _logger.LogWarning("Ingestion failed for {File}: {Error}",
                            Path.GetFileName(filePath), result.Error);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error ingesting {File}", filePath);
                }
            }

            await RefreshDocumentsAsync();
        }
        finally
        {
            IsIngesting = false;
            IngestionStatus = $"اكتمل — {IngestionTotal} ملف";
        }
    }

    [RelayCommand]
    private async Task IngestDirectoryAsync()
    {
        // The folder browser is opened from the View code-behind, which calls this
        // via IngestFilesAsync with the resolved file paths.
        // This command handles inline directory ingestion via MediatR.
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "PDF Files (*.pdf)|*.pdf",
            Multiselect = true,
            Title = "اختر ملفات PDF للفهرسة"
        };

        // Note: This runs on UI thread — dialog is modal
        if (dialog.ShowDialog() == true)
        {
            await IngestFilesAsync(dialog.FileNames);
        }
    }

    [RelayCommand]
    private async Task IngestWatchDirectoryAsync()
    {
        if (string.IsNullOrWhiteSpace(WatchDirectory)) return;

        IsIngesting = true;
        IngestionStatus = "جارٍ فهرسة مجلد المراقبة...";

        try
        {
            var result = await _mediator.Send(new IngestDirectoryCommand
            {
                DirectoryPath = WatchDirectory,
                CaseNamespace = SelectedCaseNamespace,
                UserId = "Desktop"
            });

            IngestionStatus = $"اكتمل: {result.SuccessCount} نجح, {result.FailedCount} فشل, {result.SkippedCount} تم تخطيه";
            await RefreshDocumentsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Directory ingestion failed");
            IngestionStatus = $"خطأ: {ex.Message}";
        }
        finally
        {
            IsIngesting = false;
        }
    }

    [RelayCommand]
    private void OpenWatchDirectory()
    {
        try
        {
            Directory.CreateDirectory(WatchDirectory);
            System.Diagnostics.Process.Start("explorer.exe", WatchDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open watch directory");
        }
    }

    private static string GetStatusArabic(DocumentStatus status) => status switch
    {
        DocumentStatus.Pending => "بانتظار الفهرسة",
        DocumentStatus.Indexing => "جارٍ الفهرسة",
        DocumentStatus.Indexed => "مفهرس",
        DocumentStatus.Failed => "فشل",
        DocumentStatus.Quarantined => "محجور",
        _ => status.ToString()
    };
}

// ── Supporting types ──

public sealed class DocumentItem
{
    public string Id { get; init; } = "";
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string Status { get; init; } = "";
    public string StatusArabic { get; init; } = "";
    public string? CaseNamespace { get; init; }
    public DateTime? IndexedAt { get; init; }
    public int FailureCount { get; init; }
    public string? FailureReason { get; init; }
}
