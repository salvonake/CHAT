using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LegalAI.Desktop.Services;
using LegalAI.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace LegalAI.Desktop.ViewModels;

/// <summary>
/// ViewModel for the settings view. Manages encryption passphrase,
/// model paths, retrieval parameters, and watch folder configuration.
/// Strict mode is permanently locked ON — fail-closed requirement.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IEncryptionService _encryption;
    private readonly FailClosedGuard _guard;
    private readonly DataPaths _paths;
    private readonly ILogger<SettingsViewModel> _logger;

    // ── Encryption ──
    [ObservableProperty]
    private bool _encryptionEnabled;

    [ObservableProperty]
    private string _encryptionPassphrase = "";

    [ObservableProperty]
    private string _encryptionStatus = "";

    // ── Model Paths ──
    [ObservableProperty]
    private string _llmModelPath = "";

    [ObservableProperty]
    private string _embeddingModelPath = "";

    [ObservableProperty]
    private string _llmProvider = "llamasharp";

    [ObservableProperty]
    private string _embeddingProvider = "onnx";

    // ── GPU ──
    [ObservableProperty]
    private int _gpuLayers = -1;

    [ObservableProperty]
    private int _contextSize = 8192;

    // ── Retrieval ──
    [ObservableProperty]
    private int _topK = 10;

    [ObservableProperty]
    private double _similarityThreshold = 0.45;

    [ObservableProperty]
    private double _abstentionThreshold = 0.50;

    // StrictMode is ALWAYS true — fail-closed safety requirement.
    // Life-critical system: evidence-constrained mode cannot be disabled.
    [ObservableProperty]
    private bool _strictMode = true;

    /// <summary>Whether strict mode can be modified (always false).</summary>
    public bool CanModifyStrictMode => false;

    /// <summary>Explanation shown to user about why strict mode is locked.</summary>
    public string StrictModeLockReason => "الوضع الصارم مقفل دائماً — نظام حيوي مقيّد بالأدلة.\n" +
        "Strict mode is permanently locked — life-critical evidence-constrained system.";

    [ObservableProperty]
    private bool _enableDualPass = true;

    // ── Ingestion ──
    [ObservableProperty]
    private string _watchDirectory = "";

    [ObservableProperty]
    private int _maxParallelFiles = 4;

    // ── Data ──
    [ObservableProperty]
    private string _dataDirectory = "";

    // ── UI ──
    [ObservableProperty]
    private string _uiCulture = "fr-FR";

    // ── State ──
    [ObservableProperty]
    private string _saveStatus = "";

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    public SettingsViewModel(
        IEncryptionService encryption,
        FailClosedGuard guard,
        DataPaths paths,
        ILogger<SettingsViewModel> logger)
    {
        _encryption = encryption;
        _guard = guard;
        _paths = paths;
        _logger = logger;

        // Load current values
        DataDirectory = paths.DataDirectory;
        WatchDirectory = paths.WatchDirectory;
        EncryptionEnabled = encryption.IsEnabled;

        LoadUserConfig();
    }

    private void LoadUserConfig()
    {
        try
        {
            var configPath = Path.Combine(_paths.DataDirectory, "appsettings.user.json");
            if (!File.Exists(configPath)) return;

            var json = File.ReadAllText(configPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("Llm", out var llm))
            {
                if (llm.TryGetProperty("Provider", out var p)) LlmProvider = p.GetString() ?? "llamasharp";
                if (llm.TryGetProperty("ModelPath", out var mp)) LlmModelPath = mp.GetString() ?? "";
                if (llm.TryGetProperty("GpuLayers", out var gl)) GpuLayers = gl.GetInt32();
                if (llm.TryGetProperty("ContextSize", out var cs)) ContextSize = cs.GetInt32();
            }

            if (root.TryGetProperty("Embedding", out var emb))
            {
                if (emb.TryGetProperty("Provider", out var ep)) EmbeddingProvider = ep.GetString() ?? "onnx";
                if (emb.TryGetProperty("OnnxModelPath", out var emp)) EmbeddingModelPath = emp.GetString() ?? "";
            }

            if (root.TryGetProperty("Retrieval", out var ret))
            {
                if (ret.TryGetProperty("TopK", out var tk)) TopK = tk.GetInt32();
                if (ret.TryGetProperty("SimilarityThreshold", out var st)) SimilarityThreshold = st.GetDouble();
                if (ret.TryGetProperty("AbstentionThreshold", out var at)) AbstentionThreshold = at.GetDouble();
                if (ret.TryGetProperty("StrictMode", out var sm)) StrictMode = sm.GetBoolean();
                if (ret.TryGetProperty("EnableDualPassValidation", out var dp)) EnableDualPass = dp.GetBoolean();
            }

            if (root.TryGetProperty("Ingestion", out var ing))
            {
                if (ing.TryGetProperty("WatchDirectory", out var wd)) WatchDirectory = wd.GetString() ?? "";
                if (ing.TryGetProperty("MaxParallelFiles", out var mpf)) MaxParallelFiles = mpf.GetInt32();
            }

            if (root.TryGetProperty("Ui", out var ui))
            {
                if (ui.TryGetProperty("Culture", out var culture)) UiCulture = culture.GetString() ?? "fr-FR";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load user config");
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            var config = new Dictionary<string, object>
            {
                ["Llm"] = new Dictionary<string, object>
                {
                    ["Provider"] = LlmProvider,
                    ["ModelPath"] = LlmModelPath,
                    ["GpuLayers"] = GpuLayers,
                    ["ContextSize"] = ContextSize
                },
                ["Embedding"] = new Dictionary<string, object>
                {
                    ["Provider"] = EmbeddingProvider,
                    ["OnnxModelPath"] = EmbeddingModelPath
                },
                ["Retrieval"] = new Dictionary<string, object>
                {
                    ["TopK"] = TopK,
                    ["SimilarityThreshold"] = SimilarityThreshold,
                    ["AbstentionThreshold"] = AbstentionThreshold,
                    ["StrictMode"] = true,  // ALWAYS true — fail-closed (cannot be disabled)
                    ["EnableDualPassValidation"] = EnableDualPass
                },
                ["Ingestion"] = new Dictionary<string, object>
                {
                    ["WatchDirectory"] = WatchDirectory,
                    ["MaxParallelFiles"] = MaxParallelFiles
                },
                ["Ui"] = new Dictionary<string, object>
                {
                    ["Culture"] = UiCulture
                },
                ["Security"] = new Dictionary<string, object>
                {
                    ["EncryptionEnabled"] = EncryptionEnabled,
                    ["EncryptionPassphrase"] = EncryptionPassphrase
                }
            };

            var configPath = Path.Combine(_paths.DataDirectory, "appsettings.user.json");
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(configPath, json);

            SaveStatus = "تم حفظ الإعدادات بنجاح. أعد تشغيل التطبيق لتطبيق التغييرات.";
            HasUnsavedChanges = false;

            _logger.LogInformation("Settings saved to {Path}", configPath);

            // Trigger fail-closed guard recheck after settings change
            _ = _guard.ForceRecheckAsync();
        }
        catch (Exception ex)
        {
            SaveStatus = $"فشل الحفظ: {ex.Message}";
            _logger.LogError(ex, "Failed to save settings");
        }
    }

    [RelayCommand]
    private void BrowseLlmModel()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "GGUF Models (*.gguf)|*.gguf|All Files (*.*)|*.*",
            Title = "اختر ملف نموذج LLM"
        };

        if (dialog.ShowDialog() == true)
        {
            LlmModelPath = dialog.FileName;
            HasUnsavedChanges = true;
        }
    }

    [RelayCommand]
    private void BrowseEmbeddingModel()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "ONNX Models (*.onnx)|*.onnx|All Files (*.*)|*.*",
            Title = "اختر ملف نموذج التضمين"
        };

        if (dialog.ShowDialog() == true)
        {
            EmbeddingModelPath = dialog.FileName;
            HasUnsavedChanges = true;
        }
    }

    [RelayCommand]
    private void BrowseWatchDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "اختر مجلد مراقبة ملفات PDF"
        };

        if (dialog.ShowDialog() == true)
        {
            WatchDirectory = dialog.FolderName;
            HasUnsavedChanges = true;
        }
    }

    [RelayCommand]
    private void OpenDataDirectory()
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", DataDirectory);
        }
        catch { }
    }
}
