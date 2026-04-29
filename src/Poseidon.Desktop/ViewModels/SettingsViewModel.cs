using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Poseidon.Desktop.Diagnostics;
using Poseidon.Desktop.Services;
using Poseidon.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Poseidon.Desktop.ViewModels;

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
    private readonly RuntimeConfigurationService? _runtimeConfiguration;

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
    public string StrictModeLockReason => "Strict mode is permanently locked - evidence-constrained safety-critical system.";

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

    [ObservableProperty]
    private bool _minimizeToTrayOnClose = true;

    [ObservableProperty]
    private bool _minimizeToTrayOnMinimize = true;

    [ObservableProperty]
    private bool _showBackgroundNotifications = true;

    // ── State ──
    [ObservableProperty]
    private string _saveStatus = "";

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    public SettingsViewModel(
        IEncryptionService encryption,
        FailClosedGuard guard,
        DataPaths paths,
        ILogger<SettingsViewModel> logger,
        RuntimeConfigurationService? runtimeConfiguration = null)
    {
        _encryption = encryption;
        _guard = guard;
        _paths = paths;
        _logger = logger;
        _runtimeConfiguration = runtimeConfiguration;

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

            if (root.TryGetProperty("Ingestion", out var ing))
            {
                if (ing.TryGetProperty("WatchDirectory", out var wd)) WatchDirectory = wd.GetString() ?? "";
                if (ing.TryGetProperty("MaxParallelFiles", out var mpf)) MaxParallelFiles = mpf.GetInt32();
            }

            if (root.TryGetProperty("Ui", out var ui))
            {
                if (ui.TryGetProperty("Culture", out var culture)) UiCulture = culture.GetString() ?? "fr-FR";
                if (ui.TryGetProperty("MinimizeToTrayOnClose", out var closeToTray)) MinimizeToTrayOnClose = closeToTray.GetBoolean();
                if (ui.TryGetProperty("MinimizeToTrayOnMinimize", out var minimizeToTray)) MinimizeToTrayOnMinimize = minimizeToTray.GetBoolean();
                if (ui.TryGetProperty("ShowBackgroundNotifications", out var showBackgroundNotifications)) ShowBackgroundNotifications = showBackgroundNotifications.GetBoolean();
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
                ["Ingestion"] = new Dictionary<string, object>
                {
                    ["WatchDirectory"] = WatchDirectory,
                    ["MaxParallelFiles"] = MaxParallelFiles
                },
                ["Ui"] = new Dictionary<string, object>
                {
                    ["Culture"] = UiCulture,
                    ["MinimizeToTrayOnClose"] = MinimizeToTrayOnClose,
                    ["MinimizeToTrayOnMinimize"] = MinimizeToTrayOnMinimize,
                    ["ShowBackgroundNotifications"] = ShowBackgroundNotifications
                }
            };

            var configPath = Path.Combine(_paths.DataDirectory, "appsettings.user.json");
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(configPath, json);

            SaveStatus = "Settings saved successfully. Changes are applied live.";
            HasUnsavedChanges = false;

            _logger.LogInformation("Settings saved to {Path}", configPath);

            if (_runtimeConfiguration is not null)
            {
                await _runtimeConfiguration.ReloadAsync("Settings saved");
            }
            else
            {
                await _guard.ForceRecheckAsync();
            }
        }
        catch (Exception ex)
        {
            SaveStatus = $"Save failed: {ex.Message}";
            _logger.LogError(ex, "Failed to save settings");
        }
    }

    [RelayCommand]
    private void BrowseLlmModel()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "GGUF Models (*.gguf)|*.gguf|All Files (*.*)|*.*",
            Title = "Select LLM model file"
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
            Title = "Select embedding model file"
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
            Title = "Select PDF watch directory"
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


