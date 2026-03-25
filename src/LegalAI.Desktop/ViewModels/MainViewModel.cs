using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LegalAI.Desktop.Services;
using LegalAI.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace LegalAI.Desktop.ViewModels;

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
    private string _currentViewTitle = "استعلام قانوني";

    // ── Encryption Warning ──
    [ObservableProperty]
    private bool _showEncryptionWarning;

    [ObservableProperty]
    private string _encryptionWarningText = "";

    // ── Model Status ──
    [ObservableProperty]
    private bool _isModelLoaded;

    [ObservableProperty]
    private string _modelStatusText = "جارٍ التحميل...";

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
    private string _statusText = "جاهز";

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
            SystemStatusText = "وضع المكتبة فقط — الاستعلام معطّل";
            StatusText = "⚠ وضع المكتبة فقط";

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
                SystemStatusText = "النظام يعمل مع تحذيرات";
                StatusText = "⚠ تشغيل مع تحذيرات";
            }
            else
            {
                SystemStatusText = "";
                StatusText = "جاهز";
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
                "⚠ التشفير غير مفعّل. بيانات الوثائق غير محمية. يُنصح بتفعيل التشفير من الإعدادات.";
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
                ?? "ملف النموذج غير موجود. وضع المكتبة فقط — البحث متاح لكن الاستعلام معطل.";
            ModelStatusText = "النموذج غير متوفر";
        }
        else if (!_modelIntegrity.LlmModelValid)
        {
            ShowModelWarning = true;
            IsModelLoaded = false;
            ModelWarningText = _modelIntegrity.LlmError
                ?? "فشل التحقق من سلامة النموذج.";
            ModelStatusText = "خطأ في النموذج";
        }
        else
        {
            ShowModelWarning = false;
            IsModelLoaded = true;
            ModelStatusText = "النموذج جاهز";
        }

        GpuStatusText = _modelIntegrity.DetectedGpuInfo ?? "غير محدد";
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
        CurrentViewTitle = "استعلام قانوني";
    }

    [RelayCommand]
    private void NavigateToChat()
    {
        CurrentView = ChatVm;
        CurrentViewTitle = "المحادثة القانونية";
    }

    [RelayCommand]
    private void NavigateToDocuments()
    {
        CurrentView = DocumentsVm;
        CurrentViewTitle = "إدارة الوثائق";
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        CurrentView = SettingsVm;
        CurrentViewTitle = "الإعدادات";
    }

    [RelayCommand]
    private void NavigateToHealth()
    {
        CurrentView = HealthVm;
        CurrentViewTitle = "حالة النظام";
    }

    [RelayCommand]
    private void EnableEncryption()
    {
        CurrentView = SettingsVm;
        CurrentViewTitle = "الإعدادات";
    }

    /// <summary>Refresh vector count from status bar.</summary>
    public async Task RefreshVectorCountAsync(IVectorStore vectorStore)
    {
        await UpdateVectorCountAsync(vectorStore);
    }
}
