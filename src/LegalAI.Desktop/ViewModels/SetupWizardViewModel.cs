using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LegalAI.Desktop.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LegalAI.Desktop.ViewModels;

/// <summary>
/// ViewModel for the first-run setup wizard. Guides users through model
/// acquisition for both LLM (GGUF) and embedding (ONNX) models.
///
/// Wizard Steps:
///   0 — Welcome & provider selection
///   1 — Domain selection
///   2 — LLM model configuration
///   3 — Embedding model configuration
///   4 — Download/copy/verify progress
///   5 — Complete
/// </summary>
public partial class SetupWizardViewModel : ObservableObject
{
    private static readonly string[] KnownLlmModelFileNames =
    [
        "qwen2.5-14b.Q5_K_M.gguf",
        "Qwen_Qwen3.5-9B-Q5_K_M.gguf",
        "Qwen3.5-9B-Q5_K_M.gguf"
    ];

    private readonly ModelDownloadService _downloadService;
    private readonly DataPaths _paths;
    private readonly IConfiguration _config;
    private readonly ILogger<SetupWizardViewModel> _logger;
    private CancellationTokenSource? _cts;

    // ═══════════════════════════════════════════
    //  Wizard Navigation
    // ═══════════════════════════════════════════

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextStepCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousStepCommand))]
    private int _currentStep;

    public const int StepWelcome = 0;
    public const int StepDomain = 1;
    public const int StepLlm = 2;
    public const int StepEmbedding = 3;
    public const int StepProgress = 4;
    public const int StepComplete = 5;

    [ObservableProperty]
    private string _stepTitle = "Welcome to LegalAI";

    // ═══════════════════════════════════════════
    //  Step 0: Provider Selection
    // ═══════════════════════════════════════════

    [ObservableProperty]
    private bool _useLlamaSharp = true;

    [ObservableProperty]
    private bool _useOllama;

    [ObservableProperty]
    private string _modelFolderStatusMessage = "";

    // ═══════════════════════════════════════════
    //  Step 1: Domain Selection
    // ═══════════════════════════════════════════

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextStepCommand))]
    private string _selectedDomainId = "legal";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextStepCommand))]
    private string _customDomainId = "";

    [ObservableProperty]
    private string _domainStatusMessage = "";

    public bool IsCustomDomain =>
        string.Equals(SelectedDomainId, "custom", StringComparison.OrdinalIgnoreCase);

    // ═══════════════════════════════════════════
    //  Step 1: LLM Model Configuration
    // ═══════════════════════════════════════════

    [ObservableProperty]
    private bool _llmBrowseLocal = true;

    [ObservableProperty]
    private bool _llmDownloadUrl;

    [ObservableProperty]
    private string _llmLocalPath = "";

    [ObservableProperty]
    private string _llmDownloadUrlText = "https://huggingface.co/Qwen/Qwen2.5-14B-Instruct-GGUF/resolve/main/qwen2.5-14b-instruct-q5_k_m.gguf";

    [ObservableProperty]
    private string _llmModelFileName = "qwen2.5-14b.Q5_K_M.gguf";

    [ObservableProperty]
    private string _llmStatusMessage = "";

    // ═══════════════════════════════════════════
    //  Step 1 (Ollama): Connection Settings
    // ═══════════════════════════════════════════

    [ObservableProperty]
    private string _ollamaUrl = "http://localhost:11434";

    [ObservableProperty]
    private string _ollamaLlmModel = "qwen2.5:14b";

    [ObservableProperty]
    private string _ollamaEmbeddingModel = "nomic-embed-text";

    [ObservableProperty]
    private string _ollamaStatusMessage = "";

    [ObservableProperty]
    private bool _ollamaConnected;

    [ObservableProperty]
    private bool _ollamaLlmAvailable;

    [ObservableProperty]
    private bool _ollamaEmbeddingAvailable;

    // ═══════════════════════════════════════════
    //  Step 2: Embedding Model Configuration
    // ═══════════════════════════════════════════

    [ObservableProperty]
    private bool _embBrowseLocal = true;

    [ObservableProperty]
    private bool _embDownloadUrl;

    [ObservableProperty]
    private string _embLocalPath = "";

    [ObservableProperty]
    private string _embDownloadUrlText = "";

    [ObservableProperty]
    private string _embModelFileName = "arabert.onnx";

    [ObservableProperty]
    private string _embStatusMessage = "";

    // ═══════════════════════════════════════════
    //  Step 3: Progress
    // ═══════════════════════════════════════════

    [ObservableProperty]
    private double _overallProgress;

    [ObservableProperty]
    private double _currentFileProgress;

    [ObservableProperty]
    private string _progressStatusText = "";

    [ObservableProperty]
    private string _progressDetailText = "";

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private bool _canCancel = true;

    // ═══════════════════════════════════════════
    //  Step 4: Complete
    // ═══════════════════════════════════════════

    [ObservableProperty]
    private bool _setupSucceeded;

    [ObservableProperty]
    private string _completionMessage = "";

    [ObservableProperty]
    private string _errorDetails = "";

    // ═══════════════════════════════════════════
    //  Result — consumed by App.xaml.cs
    // ═══════════════════════════════════════════

    /// <summary>True when wizard completed successfully and app should proceed.</summary>
    public bool Completed { get; private set; }

    /// <summary>Raised when the wizard wants to close its window.</summary>
    public event Action? RequestClose;

    // ═══════════════════════════════════════════
    //  Constructor
    // ═══════════════════════════════════════════

    public SetupWizardViewModel(
        ModelDownloadService downloadService,
        DataPaths paths,
        IConfiguration config,
        ILogger<SetupWizardViewModel> logger)
    {
        _downloadService = downloadService;
        _paths = paths;
        _config = config;
        _logger = logger;

        // Pre-fill from existing appsettings
        var existingLlmPath = config["Llm:ModelPath"];
        if (!string.IsNullOrWhiteSpace(existingLlmPath))
            LlmLocalPath = existingLlmPath;

        var existingOllamaUrl = config["Ollama:Url"];
        if (!string.IsNullOrWhiteSpace(existingOllamaUrl))
            OllamaUrl = existingOllamaUrl;

        var existingOllamaModel = config["Ollama:Model"];
        if (!string.IsNullOrWhiteSpace(existingOllamaModel))
            OllamaLlmModel = existingOllamaModel;

        var existingEmbModel = config["Embedding:Model"];
        if (!string.IsNullOrWhiteSpace(existingEmbModel))
            OllamaEmbeddingModel = existingEmbModel;

        // Check if models already exist
        CheckExistingModels();
    }

    private void CheckExistingModels()
    {
        var llmPath = KnownLlmModelFileNames
            .Select(name => Path.Combine(_paths.ModelsDirectory, name))
            .FirstOrDefault(File.Exists);

        if (!string.IsNullOrWhiteSpace(llmPath))
        {
            LlmModelFileName = Path.GetFileName(llmPath);
            LlmLocalPath = llmPath;
            LlmStatusMessage = $"✓ Found LLM model: {ModelDownloadService.FormatFileSize(new FileInfo(llmPath).Length)}";
        }

        var embPath = Path.Combine(_paths.ModelsDirectory, EmbModelFileName);
        if (File.Exists(embPath))
        {
            EmbLocalPath = embPath;
            EmbStatusMessage = $"✓ Found embedding model: {ModelDownloadService.FormatFileSize(new FileInfo(embPath).Length)}";
        }
    }

    partial void OnSelectedDomainIdChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomDomain));
        if (!string.Equals(value, "custom", StringComparison.OrdinalIgnoreCase))
        {
            DomainStatusMessage = string.Empty;
        }
    }

    // ═══════════════════════════════════════════
    //  Navigation Commands
    // ═══════════════════════════════════════════

    private bool CanGoNext() => CurrentStep < StepComplete && !IsProcessing;
    private bool CanGoBack() => CurrentStep > StepWelcome && CurrentStep < StepProgress && !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextStepAsync()
    {
        switch (CurrentStep)
        {
            case StepWelcome:
                CurrentStep = StepDomain;
                StepTitle = "Choose domain";
                break;

            case StepDomain:
                if (!ValidateDomainStep())
                    return;

                if (UseOllama)
                {
                    // Skip LLM/Embedding file steps, go directly to Ollama validation
                    CurrentStep = StepProgress;
                    StepTitle = "Checking connectivity...";
                    await ExecuteOllamaSetupAsync();
                }
                else
                {
                    CurrentStep = StepLlm;
                    StepTitle = "Configure language model (LLM)";
                }
                break;

            case StepLlm:
                if (!ValidateLlmStep())
                    return;
                CurrentStep = StepEmbedding;
                StepTitle = "Configure embedding model";
                break;

            case StepEmbedding:
                if (!ValidateEmbeddingStep())
                    return;
                CurrentStep = StepProgress;
                StepTitle = "Setting up models...";
                await ExecuteLocalSetupAsync();
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousStep()
    {
        switch (CurrentStep)
        {
            case StepDomain:
                CurrentStep = StepWelcome;
                StepTitle = "Welcome to LegalAI";
                break;

            case StepLlm:
                CurrentStep = StepDomain;
                StepTitle = "Choose domain";
                break;

            case StepEmbedding:
                CurrentStep = StepLlm;
                StepTitle = "Configure language model (LLM)";
                break;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        if (!IsProcessing)
        {
            Completed = false;
            RequestClose?.Invoke();
        }
    }

    [RelayCommand]
    private void Finish()
    {
        Completed = SetupSucceeded;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void OpenOllamaDownloadPage()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://ollama.com/download/windows",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            OllamaStatusMessage = "Opened Ollama download page in browser.";
        }
        catch (Exception ex)
        {
            OllamaStatusMessage = $"Could not open download page: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════
    //  File Browse Commands
    // ═══════════════════════════════════════════

    [RelayCommand]
    private void BrowseLlmModel()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select language model file (GGUF)",
            Filter = "GGUF Model Files (*.gguf)|*.gguf|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            LlmLocalPath = dialog.FileName;
            var size = new FileInfo(dialog.FileName).Length;
            LlmStatusMessage = $"Selected: {ModelDownloadService.FormatFileSize(size)}";
        }
    }

    [RelayCommand]
    private void BrowseEmbeddingModel()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select embedding model file (ONNX)",
            Filter = "ONNX Model Files (*.onnx)|*.onnx|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            EmbLocalPath = dialog.FileName;
            var size = new FileInfo(dialog.FileName).Length;
            EmbStatusMessage = $"Selected: {ModelDownloadService.FormatFileSize(size)}";
        }
    }

    [RelayCommand]
    private void PickModelsFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select models folder (GGUF + ONNX)"
        };

        if (dialog.ShowDialog() != true)
            return;

        var selectedFolder = dialog.FolderName;
        var llmPath = KnownLlmModelFileNames
            .Select(name => Path.Combine(selectedFolder, name))
            .FirstOrDefault(File.Exists);

        var embCandidates = new[]
        {
            Path.Combine(selectedFolder, "arabert.onnx"),
            Path.Combine(selectedFolder, "model.onnx")
        };
        var embPath = embCandidates.FirstOrDefault(File.Exists)
            ?? Directory.GetFiles(selectedFolder, "*.onnx", SearchOption.TopDirectoryOnly).FirstOrDefault();

        var messages = new List<string>();

        if (!string.IsNullOrWhiteSpace(llmPath))
        {
            LlmBrowseLocal = true;
            LlmDownloadUrl = false;
            LlmLocalPath = llmPath;
            LlmModelFileName = Path.GetFileName(llmPath);
            messages.Add("✓ Detected LLM model");
        }
        else
        {
            messages.Add("⚠ No known GGUF file found");
        }

        if (!string.IsNullOrWhiteSpace(embPath))
        {
            EmbBrowseLocal = true;
            EmbDownloadUrl = false;
            EmbLocalPath = embPath;
            EmbModelFileName = Path.GetFileName(embPath);
            messages.Add("✓ Detected ONNX embedding model");
        }
        else
        {
            messages.Add("⚠ No ONNX file found");
        }

        ModelFolderStatusMessage = string.Join("\n", messages);
    }

    [RelayCommand]
    private async Task TestOllamaAsync()
    {
        OllamaStatusMessage = "Testing connection...";
        try
        {
            var llmResult = await _downloadService.TestOllamaConnectionAsync(OllamaUrl, OllamaLlmModel, CancellationToken.None);
            OllamaConnected = llmResult.Connected;
            OllamaLlmAvailable = llmResult.ModelAvailable;

            if (llmResult.Connected)
            {
                var embResult = await _downloadService.TestOllamaConnectionAsync(OllamaUrl, OllamaEmbeddingModel, CancellationToken.None);
                OllamaEmbeddingAvailable = embResult.ModelAvailable;

                OllamaStatusMessage = llmResult.Message + "\n" + embResult.Message;
            }
            else
            {
                OllamaStatusMessage = llmResult.Message;
            }
        }
        catch (Exception ex)
        {
            OllamaStatusMessage = $"Error: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════
    //  Validation
    // ═══════════════════════════════════════════

    private bool ValidateDomainStep()
    {
        if (!IsCustomDomain)
        {
            DomainStatusMessage = string.Empty;
            return true;
        }

        var normalized = CustomDomainId.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            DomainStatusMessage = "⚠ Enter a custom domain ID";
            return false;
        }

        if (normalized.Length < 3 || normalized.Length > 40)
        {
            DomainStatusMessage = "⚠ Domain ID must be 3-40 characters";
            return false;
        }

        foreach (var ch in normalized)
        {
            if (!char.IsLetterOrDigit(ch) && ch is not '-' and not '_')
            {
                DomainStatusMessage = "⚠ Allowed: letters, numbers, '-' and '_' only";
                return false;
            }
        }

        DomainStatusMessage = string.Empty;
        return true;
    }

    private bool ValidateLlmStep()
    {
        if (LlmBrowseLocal)
        {
            if (string.IsNullOrWhiteSpace(LlmLocalPath))
            {
                LlmStatusMessage = "⚠ Select a model file path";
                return false;
            }
            if (!File.Exists(LlmLocalPath))
            {
                LlmStatusMessage = "⚠ Selected file does not exist";
                return false;
            }
        }
        else if (LlmDownloadUrl)
        {
            if (string.IsNullOrWhiteSpace(LlmDownloadUrlText) || !Uri.TryCreate(LlmDownloadUrlText, UriKind.Absolute, out _))
            {
                LlmStatusMessage = "⚠ Enter a valid download URL";
                return false;
            }
        }

        return true;
    }

    private bool ValidateEmbeddingStep()
    {
        if (EmbBrowseLocal)
        {
            if (string.IsNullOrWhiteSpace(EmbLocalPath))
            {
                EmbStatusMessage = "⚠ Select embedding model file path";
                return false;
            }
            if (!File.Exists(EmbLocalPath))
            {
                EmbStatusMessage = "⚠ Selected file does not exist";
                return false;
            }
        }
        else if (EmbDownloadUrl)
        {
            if (string.IsNullOrWhiteSpace(EmbDownloadUrlText) || !Uri.TryCreate(EmbDownloadUrlText, UriKind.Absolute, out _))
            {
                EmbStatusMessage = "⚠ Enter a valid download URL";
                return false;
            }
        }

        return true;
    }

    // ═══════════════════════════════════════════
    //  Setup Execution — Local Models
    // ═══════════════════════════════════════════

    private async Task ExecuteLocalSetupAsync()
    {
        IsProcessing = true;
        CanCancel = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var tasks = new List<(string Name, Func<Task> Action)>();
            var llmRuntimePath = "";
            var embRuntimePath = "";

            // ── LLM Model ──
            var llmDestPath = Path.Combine(_paths.ModelsDirectory, LlmModelFileName);
            if (LlmBrowseLocal)
            {
                llmRuntimePath = LlmLocalPath;
            }
            else if (LlmDownloadUrl)
            {
                llmRuntimePath = llmDestPath;
                tasks.Add(("Download language model", async () =>
                {
                    ProgressStatusText = "Downloading language model...";
                    var progress = new Progress<DownloadProgress>(p =>
                    {
                        CurrentFileProgress = p.Fraction >= 0 ? p.Fraction : 0;
                        ProgressDetailText = p.StatusText;
                    });
                    await _downloadService.DownloadFileAsync(LlmDownloadUrlText, llmDestPath, progress, ct);
                }));
            }

            // ── Embedding Model ──
            var embDestPath = Path.Combine(_paths.ModelsDirectory, EmbModelFileName);
            if (EmbBrowseLocal)
            {
                embRuntimePath = EmbLocalPath;
            }
            else if (EmbDownloadUrl)
            {
                embRuntimePath = embDestPath;
                tasks.Add(("Download embedding model", async () =>
                {
                    ProgressStatusText = "Downloading embedding model...";
                    var progress = new Progress<DownloadProgress>(p =>
                    {
                        CurrentFileProgress = p.Fraction >= 0 ? p.Fraction : 0;
                        ProgressDetailText = p.StatusText;
                    });
                    await _downloadService.DownloadFileAsync(EmbDownloadUrlText, embDestPath, progress, ct);
                }));
            }

            // ── Hash Verification ──
            var expectedLlmHash = _config["ModelIntegrity:ExpectedLlmHash"];
            if (!string.IsNullOrWhiteSpace(expectedLlmHash))
            {
                tasks.Add(("Verify language model integrity", async () =>
                {
                    ProgressStatusText = "Verifying language model integrity...";
                    var progress = new Progress<DownloadProgress>(p =>
                    {
                        CurrentFileProgress = p.Fraction >= 0 ? p.Fraction : 0;
                        ProgressDetailText = p.StatusText;
                    });
                    var result = await _downloadService.VerifyHashAsync(llmRuntimePath, expectedLlmHash, progress, ct);
                    if (!result.Passed)
                        throw new InvalidOperationException($"LLM hash verification failed: {result.Message}");
                }));
            }

            var expectedEmbHash = _config["ModelIntegrity:ExpectedEmbeddingHash"];
            if (!string.IsNullOrWhiteSpace(expectedEmbHash))
            {
                tasks.Add(("Verify embedding model integrity", async () =>
                {
                    ProgressStatusText = "Verifying embedding model integrity...";
                    var progress = new Progress<DownloadProgress>(p =>
                    {
                        CurrentFileProgress = p.Fraction >= 0 ? p.Fraction : 0;
                        ProgressDetailText = p.StatusText;
                    });
                    var result = await _downloadService.VerifyHashAsync(embRuntimePath, expectedEmbHash, progress, ct);
                    if (!result.Passed)
                        throw new InvalidOperationException($"Embedding hash verification failed: {result.Message}");
                }));
            }

            // ── Execute all tasks sequentially with overall progress ──
            if (tasks.Count == 0)
            {
                // Models already in place
                ProgressStatusText = "Models are already available";
                OverallProgress = 1.0;
            }
            else
            {
                for (int i = 0; i < tasks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    ProgressStatusText = tasks[i].Name;
                    CurrentFileProgress = 0;
                    OverallProgress = (double)i / tasks.Count;

                    await tasks[i].Action();
                }
                OverallProgress = 1.0;
            }

            SaveWizardSettings(
                domainId: ResolveSelectedDomainId(),
                customDomainId: IsCustomDomain ? CustomDomainId.Trim() : null,
                llmProvider: "llamasharp",
                llmModelPath: llmRuntimePath,
                embeddingProvider: "onnx",
                embeddingOnnxModelPath: embRuntimePath,
                ollamaUrl: OllamaUrl,
                ollamaModel: OllamaLlmModel,
                embeddingModel: OllamaEmbeddingModel);

            // ── Success ──
            CurrentStep = StepComplete;
            StepTitle = "Setup complete";
            SetupSucceeded = true;
            CompletionMessage = "✓ Models were configured successfully. The system is ready.";
            _logger.LogInformation("Setup wizard completed successfully (local models)");
        }
        catch (OperationCanceledException)
        {
            CurrentStep = StepComplete;
            StepTitle = "Cancelled";
            SetupSucceeded = false;
            CompletionMessage = "Setup was cancelled. You can retry later.";
            _logger.LogWarning("Setup wizard cancelled by user");
        }
        catch (Exception ex)
        {
            CurrentStep = StepComplete;
            StepTitle = "Setup error";
            SetupSucceeded = false;
            CompletionMessage = "Setup failed. Review details below.";
            ErrorDetails = $"{ex.GetType().Name}: {ex.Message}";
            _logger.LogError(ex, "Setup wizard failed");
        }
        finally
        {
            IsProcessing = false;
            CanCancel = false;
        }
    }

    // ═══════════════════════════════════════════
    //  Setup Execution — Ollama
    // ═══════════════════════════════════════════

    private async Task ExecuteOllamaSetupAsync()
    {
        IsProcessing = true;
        CanCancel = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            ProgressStatusText = "Checking Ollama connectivity...";
            CurrentFileProgress = 0;

            var llmCheck = await _downloadService.TestOllamaConnectionAsync(OllamaUrl, OllamaLlmModel, ct);
            CurrentFileProgress = 0.33;

            if (!llmCheck.Connected)
            {
                throw new InvalidOperationException(
                    $"Cannot connect to Ollama server at {OllamaUrl}\n" +
                    "Ensure Ollama is running and listening on the configured URL.\n\n" +
                    "Cannot connect to Ollama server. Ensure it is running.");
            }

            ProgressStatusText = "Checking model availability...";
            var embCheck = await _downloadService.TestOllamaConnectionAsync(OllamaUrl, OllamaEmbeddingModel, ct);
            CurrentFileProgress = 0.66;

            var warnings = new List<string>();
            if (!llmCheck.ModelAvailable)
                warnings.Add($"⚠ Model '{OllamaLlmModel}' is unavailable. Run: ollama pull {OllamaLlmModel}");
            if (!embCheck.ModelAvailable)
                warnings.Add($"⚠ Embedding model '{OllamaEmbeddingModel}' is unavailable. Run: ollama pull {OllamaEmbeddingModel}");

            CurrentFileProgress = 1.0;
            OverallProgress = 1.0;

            SaveWizardSettings(
                domainId: ResolveSelectedDomainId(),
                customDomainId: IsCustomDomain ? CustomDomainId.Trim() : null,
                llmProvider: "ollama",
                llmModelPath: "",
                embeddingProvider: "ollama",
                embeddingOnnxModelPath: "",
                ollamaUrl: OllamaUrl,
                ollamaModel: OllamaLlmModel,
                embeddingModel: OllamaEmbeddingModel);

            // ── Complete ──
            CurrentStep = StepComplete;
            StepTitle = "Setup complete";
            SetupSucceeded = llmCheck.Connected; // Succeed if connected, even if models need pulling

            if (warnings.Count == 0)
            {
                CompletionMessage = "✓ Ollama is connected and all models are available. System is ready.";
            }
            else
            {
                CompletionMessage = "✓ Ollama is connected. Some models still need download:";
                ErrorDetails = string.Join("\n", warnings);
            }

            _logger.LogInformation("Setup wizard completed (Ollama). Connected: {Connected}, LLM: {Llm}, Emb: {Emb}",
                llmCheck.Connected, llmCheck.ModelAvailable, embCheck.ModelAvailable);
        }
        catch (OperationCanceledException)
        {
            CurrentStep = StepComplete;
            StepTitle = "Cancelled";
            SetupSucceeded = false;
            CompletionMessage = "Setup was cancelled.";
        }
        catch (Exception ex)
        {
            CurrentStep = StepComplete;
            StepTitle = "Setup error";
            SetupSucceeded = false;
            CompletionMessage = "Ollama setup failed.";
            ErrorDetails = ex.Message;
            _logger.LogError(ex, "Ollama setup failed");
        }
        finally
        {
            IsProcessing = false;
            CanCancel = false;
        }
    }

    private void SaveWizardSettings(
        string domainId,
        string? customDomainId,
        string llmProvider,
        string llmModelPath,
        string embeddingProvider,
        string embeddingOnnxModelPath,
        string ollamaUrl,
        string ollamaModel,
        string embeddingModel)
    {
        var configPath = Path.Combine(_paths.DataDirectory, "appsettings.user.json");

        var config = new Dictionary<string, object>
        {
            ["Domain"] = new Dictionary<string, object>
            {
                ["ActiveModule"] = domainId,
                ["CustomModuleId"] = customDomainId ?? string.Empty
            },
            ["Llm"] = new Dictionary<string, object>
            {
                ["Provider"] = llmProvider,
                ["ModelPath"] = llmModelPath
            },
            ["Embedding"] = new Dictionary<string, object>
            {
                ["Provider"] = embeddingProvider,
                ["OnnxModelPath"] = embeddingOnnxModelPath,
                ["Model"] = embeddingModel
            },
            ["Ollama"] = new Dictionary<string, object>
            {
                ["Url"] = ollamaUrl,
                ["Model"] = ollamaModel
            }
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(configPath, json);
        _logger.LogInformation("Wizard settings saved to {Path}", configPath);
    }

    private string ResolveSelectedDomainId()
    {
        if (IsCustomDomain)
        {
            return CustomDomainId.Trim().ToLowerInvariant();
        }

        return SelectedDomainId.Trim().ToLowerInvariant();
    }
}


