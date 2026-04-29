using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Poseidon.Desktop.Services;
using Poseidon.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Poseidon.Desktop.ViewModels;

/// <summary>
/// Root ViewModel for the main window. Manages navigation between views,
/// the persistent encryption warning banner, and fail-closed system state.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IEncryptionService _encryption;
    private readonly ModelIntegrityService _modelIntegrity;
    private readonly FailClosedGuard _guard;
    private readonly ILogger<MainViewModel> _logger;

    // ── Child ViewModels ──
    public AskViewModel AskVm { get; }
    public ChatViewModel ChatVm { get; }
    public DocumentsViewModel DocumentsVm { get; }
    public SettingsViewModel SettingsVm { get; }
    public HealthViewModel HealthVm { get; }

    [ObservableProperty]
    private ObservableObject _currentView;

    [ObservableProperty]
    private string _currentViewTitle = "Legal Query";

    // ── Encryption Warning ──
    [ObservableProperty]
    private bool _showEncryptionWarning;

    [ObservableProperty]
    private string _encryptionWarningText = "";

    // ── Model Status ──
    [ObservableProperty]
    private bool _isModelLoaded;

    [ObservableProperty]
    private string _modelStatusText = "Loading...";

    [ObservableProperty]
    private bool _showModelWarning;

    [ObservableProperty]
    private string _modelWarningText = "";

    // ── GPU Status ──
    [ObservableProperty]
    private string _gpuStatusText = "";

    // ── Status Bar ──
    [ObservableProperty]
    private long _vectorCount;

    [ObservableProperty]
    private string _statusText = "Ready";

    // ── Fail-Closed State ──
    [ObservableProperty]
    private bool _isLibraryOnlyMode;

    [ObservableProperty]
    private string _systemStatusText = "";

    [ObservableProperty]
    private bool _showSystemBlockBanner;

    [ObservableProperty]
    private string _systemBlockReason = "";

    public MainViewModel(
        AskViewModel askVm,
        ChatViewModel chatVm,
        DocumentsViewModel documentsVm,
        SettingsViewModel settingsVm,
        HealthViewModel healthVm,
        IEncryptionService encryption,
        ModelIntegrityService modelIntegrity,
        FailClosedGuard guard,
        IVectorStore vectorStore,
        ILogger<MainViewModel> logger)
    {
        AskVm = askVm;
        ChatVm = chatVm;
        DocumentsVm = documentsVm;
        SettingsVm = settingsVm;
        HealthVm = healthVm;
        _encryption = encryption;
        _modelIntegrity = modelIntegrity;
        _guard = guard;
        _logger = logger;

        _currentView = askVm;

        // Check encryption status
        UpdateEncryptionWarning();

        // Check model status
        UpdateModelStatus();

        // Apply fail-closed state
        UpdateFailClosedState();

        // Subscribe to guard status changes
        _guard.StatusChanged += OnGuardStatusChanged;

        // Get vector count
        _ = UpdateVectorCountAsync(vectorStore);
    }

    private void OnGuardStatusChanged(object? sender, SystemOperationalStatus status)
    {
        // This may be called from a background thread (timer)
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            UpdateFailClosedState();
            UpdateModelStatus();
        });
    }

    private void UpdateFailClosedState()
    {
        IsLibraryOnlyMode = !_guard.CanAskQuestions;

        if (IsLibraryOnlyMode)
        {
            var reasons = _guard.BlockReasons;
            ShowSystemBlockBanner = true;
            SystemBlockReason = string.Join("\n", reasons);
            SystemStatusText = "Recovery mode - querying disabled";
            StatusText = "Recovery mode";

            // Propagate to AskViewModel and ChatViewModel
            AskVm.SetLibraryOnlyMode(true, reasons);
            ChatVm.SetLibraryOnlyMode(true, reasons);
        }
        else
        {
            ShowSystemBlockBanner = false;
            SystemBlockReason = "";

            var warnings = _guard.Warnings;
            if (warnings.Count > 0)
            {
                SystemStatusText = "System running with warnings";
                StatusText = "Running with warnings";
            }
            else
            {
                SystemStatusText = "";
                StatusText = "Ready";
            }

            AskVm.SetLibraryOnlyMode(false, []);
            ChatVm.SetLibraryOnlyMode(false, []);
        }
    }

    private void UpdateEncryptionWarning()
    {
        // Check if encryption is enabled
        if (!_encryption.IsEnabled)
        {
            ShowEncryptionWarning = true;
            EncryptionWarningText =
                "WARNING: Encryption is disabled. Document data is not protected. Enable encryption from Settings.";
        }
        else
        {
            ShowEncryptionWarning = false;
        }
    }

    private void UpdateModelStatus()
    {
        if (!_modelIntegrity.LlmModelExists)
        {
            ShowModelWarning = true;
            IsModelLoaded = false;
            ModelWarningText = _modelIntegrity.LlmError
                ?? "Model file is missing. Library-only mode: retrieval is available but querying is disabled.";
            ModelStatusText = "Model unavailable";
        }
        else if (!_modelIntegrity.LlmModelValid)
        {
            ShowModelWarning = true;
            IsModelLoaded = false;
            ModelWarningText = _modelIntegrity.LlmError
                ?? "Model integrity verification failed.";
            ModelStatusText = "Model error";
        }
        else
        {
            ShowModelWarning = false;
            IsModelLoaded = true;
            ModelStatusText = "Model ready";
        }

        GpuStatusText = _modelIntegrity.DetectedGpuInfo ?? "Unknown";
    }

    private async Task UpdateVectorCountAsync(IVectorStore vectorStore)
    {
        try
        {
            VectorCount = await vectorStore.GetVectorCountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get vector count");
        }
    }

    // ── Navigation Commands ──

    [RelayCommand]
    private void NavigateToAsk()
    {
        CurrentView = AskVm;
        CurrentViewTitle = "Legal Query";
    }

    [RelayCommand]
    private void NavigateToChat()
    {
        CurrentView = ChatVm;
        CurrentViewTitle = "Legal Chat";
    }

    [RelayCommand]
    private void NavigateToDocuments()
    {
        CurrentView = DocumentsVm;
        CurrentViewTitle = "Document Management";
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        CurrentView = SettingsVm;
        CurrentViewTitle = "Settings";
    }

    [RelayCommand]
    private void NavigateToHealth()
    {
        CurrentView = HealthVm;
        CurrentViewTitle = "System Health";
    }

    [RelayCommand]
    private void EnableEncryption()
    {
        CurrentView = SettingsVm;
        CurrentViewTitle = "Settings";
    }

    /// <summary>Refresh vector count from status bar.</summary>
    public async Task RefreshVectorCountAsync(IVectorStore vectorStore)
    {
        await UpdateVectorCountAsync(vectorStore);
    }
}


