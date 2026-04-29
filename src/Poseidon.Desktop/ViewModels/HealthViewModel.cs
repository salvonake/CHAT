using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Poseidon.Desktop.Services;
using Poseidon.Domain.Interfaces;
using Poseidon.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Poseidon.Desktop.ViewModels;

/// <summary>
/// ViewModel for the system health dashboard. Displays model status,
/// vector store health, audit integrity, metrics, and GPU information.
/// </summary>
public partial class HealthViewModel : ObservableObject
{
    private readonly IVectorStore _vectorStore;
    private readonly ILlmService _llm;
    private readonly IAuditService _audit;
    private readonly IDocumentStore _docStore;
    private readonly IMetricsCollector _metrics;
    private readonly ModelIntegrityService _modelIntegrity;
    private readonly IDispatcherService _dispatcher;
    private readonly ILogger<HealthViewModel> _logger;

    public DiagnosticViewModel? Diagnostic { get; }

    // ── LLM Status ──
    [ObservableProperty]
    private bool _llmAvailable;

    [ObservableProperty]
    private string _llmStatusText = "Unknown";

    [ObservableProperty]
    private string _llmModelInfo = "";

    // ── GPU Status ──
    [ObservableProperty]
    private string _gpuStatus = "";

    // ── Vector Store ──
    [ObservableProperty]
    private bool _vectorStoreHealthy;

    [ObservableProperty]
    private string _vectorStoreStatus = "";

    [ObservableProperty]
    private long _vectorCount;

    [ObservableProperty]
    private long _indexedSegments;

    // ── Documents ──
    [ObservableProperty]
    private int _totalDocuments;

    [ObservableProperty]
    private int _indexedDocuments;

    [ObservableProperty]
    private int _pendingDocuments;

    [ObservableProperty]
    private int _quarantinedDocuments;

    // ── Audit ──
    [ObservableProperty]
    private string _auditChainStatus = "Unknown";

    [ObservableProperty]
    private bool _auditChainValid;

    [ObservableProperty]
    private int _auditEntryCount;

    // ── Encryption ──
    [ObservableProperty]
    private string _encryptionStatus = "";

    // ── Metrics ──
    [ObservableProperty]
    private ObservableCollection<MetricItem> _metricItems = [];

    // ── State ──
    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private string _lastRefreshTime = "";

    // ── Error Log ──
    [ObservableProperty]
    private ObservableCollection<string> _recentErrors = [];

    public HealthViewModel(
        IVectorStore vectorStore,
        ILlmService llm,
        IAuditService audit,
        IDocumentStore docStore,
        IMetricsCollector metrics,
        IEncryptionService encryption,
        ModelIntegrityService modelIntegrity,
        IDispatcherService dispatcher,
        ILogger<HealthViewModel> logger,
        DiagnosticViewModel? diagnostic = null)
    {
        _vectorStore = vectorStore;
        _llm = llm;
        _audit = audit;
        _docStore = docStore;
        _metrics = metrics;
        _modelIntegrity = modelIntegrity;
        _dispatcher = dispatcher;
        _logger = logger;
        Diagnostic = diagnostic;

        EncryptionStatus = encryption.IsEnabled
            ? "✓ Encryption enabled"
            : "✗ Encryption disabled";

        GpuStatus = modelIntegrity.DetectedGpuInfo ?? "Unknown";

        _ = RefreshAllAsync();
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;

        try
        {
            await Task.WhenAll(
                CheckLlmAsync(),
                CheckVectorStoreAsync(),
                CheckDocumentsAsync(),
                LoadMetricsAsync()
            );

            LastRefreshTime = DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health refresh failed");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task CheckLlmAsync()
    {
        try
        {
            LlmAvailable = await _llm.IsAvailableAsync();
            LlmStatusText = LlmAvailable ? "✓ Model available" : "✗ Model unavailable";

            if (!_modelIntegrity.LlmModelValid && _modelIntegrity.LlmModelExists)
            {
                LlmStatusText = "⚠ Model integrity verification failed";
            }
        }
        catch (Exception ex)
        {
            LlmAvailable = false;
            LlmStatusText = $"✗ Error: {ex.Message}";
        }
    }

    private async Task CheckVectorStoreAsync()
    {
        try
        {
            var health = await _vectorStore.GetHealthAsync();
            VectorStoreHealthy = health.IsHealthy;
            VectorStoreStatus = health.Status ?? "Unknown";
            VectorCount = health.VectorCount;
            IndexedSegments = health.IndexedSegments;
        }
        catch (Exception ex)
        {
            VectorStoreHealthy = false;
            VectorStoreStatus = $"Error: {ex.Message}";
        }
    }

    private async Task CheckDocumentsAsync()
    {
        try
        {
            var docs = await _docStore.GetAllAsync();
            TotalDocuments = docs.Count;
            IndexedDocuments = docs.Count(d => d.Status == Poseidon.Domain.Entities.DocumentStatus.Indexed);
            PendingDocuments = docs.Count(d =>
                d.Status == Poseidon.Domain.Entities.DocumentStatus.Pending ||
                d.Status == Poseidon.Domain.Entities.DocumentStatus.Indexing);
            QuarantinedDocuments = docs.Count(d => d.Status == Poseidon.Domain.Entities.DocumentStatus.Quarantined);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check documents");
        }
    }

    [RelayCommand]
    private async Task VerifyAuditChainAsync()
    {
        try
        {
            AuditChainStatus = "Verifying...";
            var valid = await _audit.VerifyChainIntegrityAsync();

            AuditChainValid = valid;
            AuditChainStatus = valid
                ? "✓ Audit chain is valid"
                : "✗ Audit chain mismatch";
        }
        catch (Exception ex)
        {
            AuditChainValid = false;
            AuditChainStatus = $"✗ Error: {ex.Message}";
        }
    }

    private async Task LoadMetricsAsync()
    {
        try
        {
            var snapshot = await Task.Run(() => _metrics.GetSnapshot());

            await _dispatcher.InvokeAsync(() =>
            {
                MetricItems.Clear();

                MetricItems.Add(new MetricItem
                {
                    NameAr = "Total queries",
                    NameEn = "Total Queries",
                    Value = snapshot.TotalQueries.ToString("N0")
                });
                MetricItems.Add(new MetricItem
                {
                    NameAr = "Abstentions",
                    NameEn = "Abstentions",
                    Value = snapshot.AbstentionCount.ToString("N0")
                });
                MetricItems.Add(new MetricItem
                {
                    NameAr = "Avg retrieval time",
                    NameEn = "Avg Retrieval Latency",
                    Value = $"{snapshot.RetrievalLatencyP50Ms:F0} ms"
                });
                MetricItems.Add(new MetricItem
                {
                    NameAr = "Avg generation time",
                    NameEn = "Avg LLM Latency",
                    Value = $"{snapshot.AverageGenerationLatencyMs:F0} ms"
                });
                MetricItems.Add(new MetricItem
                {
                    NameAr = "Indexed documents",
                    NameEn = "Documents Indexed",
                    Value = snapshot.TotalDocumentsIndexed.ToString("N0")
                });
                MetricItems.Add(new MetricItem
                {
                    NameAr = "Blocked injection attempts",
                    NameEn = "Injection Attempts Blocked",
                    Value = snapshot.InjectionDetections.ToString("N0")
                });
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load metrics");
        }
    }

    [RelayCommand]
    private async Task ExportAuditLogAsync()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON Files (*.json)|*.json",
                Title = "Export audit log",
                FileName = $"audit-log-{DateTime.Now:yyyy-MM-dd}.json"
            };

            if (dialog.ShowDialog() == true)
            {
                var entries = await _audit.GetEntriesAsync(0, int.MaxValue);
                var json = System.Text.Json.JsonSerializer.Serialize(entries,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync(dialog.FileName, json);

                _logger.LogInformation("Audit log exported to {Path}", dialog.FileName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export audit log");
        }
    }
}

public sealed class MetricItem
{
    public string NameAr { get; init; } = "";
    public string NameEn { get; init; } = "";
    public string Value { get; init; } = "";
}


