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
///   1 — LLM model configuration
///   2 — Embedding model configuration
///   3 — Download/copy/verify progress
///   4 — Complete
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
    public const int StepLlm = 1;
    public const int StepEmbedding = 2;
    public const int StepProgress = 3;
    public const int StepComplete = 4;

    [ObservableProperty]
    private string _stepTitle = "مرحباً بكم في نظام الدعم القانوني";

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
            LlmStatusMessage = $"✓ تم العثور على نموذج LLM: {ModelDownloadService.FormatFileSize(new FileInfo(llmPath).Length)}";
        }

        var embPath = Path.Combine(_paths.ModelsDirectory, EmbModelFileName);
        if (File.Exists(embPath))
        {
            EmbLocalPath = embPath;
            EmbStatusMessage = $"✓ تم العثور على نموذج التضمين: {ModelDownloadService.FormatFileSize(new FileInfo(embPath).Length)}";
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
                if (UseOllama)
                {
                    // Skip LLM/Embedding file steps, go directly to Ollama validation
                    CurrentStep = StepProgress;
                    StepTitle = "جارٍ التحقق من الاتصال...";
                    await ExecuteOllamaSetupAsync();
                }
                else
                {
                    CurrentStep = StepLlm;
                    StepTitle = "إعداد النموذج اللغوي (LLM)";
                }
                break;

            case StepLlm:
                if (!ValidateLlmStep())
                    return;
                CurrentStep = StepEmbedding;
                StepTitle = "إعداد نموذج التضمين (Embedding)";
                break;

            case StepEmbedding:
                if (!ValidateEmbeddingStep())
                    return;
                CurrentStep = StepProgress;
                StepTitle = "جارٍ إعداد النماذج...";
                await ExecuteLocalSetupAsync();
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousStep()
    {
        switch (CurrentStep)
        {
            case StepLlm:
                CurrentStep = StepWelcome;
                StepTitle = "مرحباً بكم في نظام الدعم القانوني";
                break;

            case StepEmbedding:
                CurrentStep = StepLlm;
                StepTitle = "إعداد النموذج اللغوي (LLM)";
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
            OllamaStatusMessage = "تم فتح صفحة تنزيل Ollama في المتصفح.";
        }
        catch (Exception ex)
        {
            OllamaStatusMessage = $"تعذر فتح صفحة التنزيل: {ex.Message}";
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
            Title = "اختر ملف النموذج اللغوي (GGUF)",
            Filter = "GGUF Model Files (*.gguf)|*.gguf|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            LlmLocalPath = dialog.FileName;
            var size = new FileInfo(dialog.FileName).Length;
            LlmStatusMessage = $"تم الاختيار: {ModelDownloadService.FormatFileSize(size)}";
        }
    }

    [RelayCommand]
    private void BrowseEmbeddingModel()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "اختر ملف نموذج التضمين (ONNX)",
            Filter = "ONNX Model Files (*.onnx)|*.onnx|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            EmbLocalPath = dialog.FileName;
            var size = new FileInfo(dialog.FileName).Length;
            EmbStatusMessage = $"تم الاختيار: {ModelDownloadService.FormatFileSize(size)}";
        }
    }

    [RelayCommand]
    private void PickModelsFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "اختر مجلد النماذج (GGUF + ONNX)"
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
            messages.Add("✓ تم اكتشاف نموذج LLM");
        }
        else
        {
            messages.Add("⚠ لم يتم العثور على ملف GGUF معروف");
        }

        if (!string.IsNullOrWhiteSpace(embPath))
        {
            EmbBrowseLocal = true;
            EmbDownloadUrl = false;
            EmbLocalPath = embPath;
            EmbModelFileName = Path.GetFileName(embPath);
            messages.Add("✓ تم اكتشاف نموذج التضمين ONNX");
        }
        else
        {
            messages.Add("⚠ لم يتم العثور على ملف ONNX");
        }

        ModelFolderStatusMessage = string.Join("\n", messages);
    }

    [RelayCommand]
    private async Task TestOllamaAsync()
    {
        OllamaStatusMessage = "جارٍ الاختبار...";
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
            OllamaStatusMessage = $"خطأ: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════
    //  Validation
    // ═══════════════════════════════════════════

    private bool ValidateLlmStep()
    {
        if (LlmBrowseLocal)
        {
            if (string.IsNullOrWhiteSpace(LlmLocalPath))
            {
                LlmStatusMessage = "⚠ يرجى تحديد مسار ملف النموذج";
                return false;
            }
            if (!File.Exists(LlmLocalPath))
            {
                LlmStatusMessage = "⚠ الملف المحدد غير موجود";
                return false;
            }
        }
        else if (LlmDownloadUrl)
        {
            if (string.IsNullOrWhiteSpace(LlmDownloadUrlText) || !Uri.TryCreate(LlmDownloadUrlText, UriKind.Absolute, out _))
            {
                LlmStatusMessage = "⚠ يرجى إدخال رابط تحميل صالح";
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
                EmbStatusMessage = "⚠ يرجى تحديد مسار ملف نموذج التضمين";
                return false;
            }
            if (!File.Exists(EmbLocalPath))
            {
                EmbStatusMessage = "⚠ الملف المحدد غير موجود";
                return false;
            }
        }
        else if (EmbDownloadUrl)
        {
            if (string.IsNullOrWhiteSpace(EmbDownloadUrlText) || !Uri.TryCreate(EmbDownloadUrlText, UriKind.Absolute, out _))
            {
                EmbStatusMessage = "⚠ يرجى إدخال رابط تحميل صالح";
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
                tasks.Add(("تحميل النموذج اللغوي", async () =>
                {
                    ProgressStatusText = "جارٍ تحميل النموذج اللغوي...";
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
                tasks.Add(("تحميل نموذج التضمين", async () =>
                {
                    ProgressStatusText = "جارٍ تحميل نموذج التضمين...";
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
                tasks.Add(("التحقق من سلامة النموذج اللغوي", async () =>
                {
                    ProgressStatusText = "جارٍ التحقق من سلامة النموذج اللغوي...";
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
                tasks.Add(("التحقق من سلامة نموذج التضمين", async () =>
                {
                    ProgressStatusText = "جارٍ التحقق من سلامة نموذج التضمين...";
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
                ProgressStatusText = "النماذج جاهزة بالفعل";
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
                llmProvider: "llamasharp",
                llmModelPath: llmRuntimePath,
                embeddingProvider: "onnx",
                embeddingOnnxModelPath: embRuntimePath,
                ollamaUrl: OllamaUrl,
                ollamaModel: OllamaLlmModel,
                embeddingModel: OllamaEmbeddingModel);

            // ── Success ──
            CurrentStep = StepComplete;
            StepTitle = "اكتمل الإعداد";
            SetupSucceeded = true;
            CompletionMessage = "✓ تم إعداد النماذج بنجاح. النظام جاهز للاستخدام.";
            _logger.LogInformation("Setup wizard completed successfully (local models)");
        }
        catch (OperationCanceledException)
        {
            CurrentStep = StepComplete;
            StepTitle = "تم الإلغاء";
            SetupSucceeded = false;
            CompletionMessage = "تم إلغاء الإعداد. يمكنك إعادة المحاولة لاحقاً.";
            _logger.LogWarning("Setup wizard cancelled by user");
        }
        catch (Exception ex)
        {
            CurrentStep = StepComplete;
            StepTitle = "خطأ في الإعداد";
            SetupSucceeded = false;
            CompletionMessage = "فشل الإعداد. تحقق من التفاصيل أدناه.";
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
            ProgressStatusText = "جارٍ التحقق من اتصال Ollama...";
            CurrentFileProgress = 0;

            var llmCheck = await _downloadService.TestOllamaConnectionAsync(OllamaUrl, OllamaLlmModel, ct);
            CurrentFileProgress = 0.33;

            if (!llmCheck.Connected)
            {
                throw new InvalidOperationException(
                    $"تعذر الاتصال بخادم Ollama على {OllamaUrl}\n" +
                    "تأكد من تشغيل Ollama وأنه يستمع على العنوان المحدد.\n\n" +
                    "Cannot connect to Ollama server. Ensure it is running.");
            }

            ProgressStatusText = "جارٍ التحقق من النماذج...";
            var embCheck = await _downloadService.TestOllamaConnectionAsync(OllamaUrl, OllamaEmbeddingModel, ct);
            CurrentFileProgress = 0.66;

            var warnings = new List<string>();
            if (!llmCheck.ModelAvailable)
                warnings.Add($"⚠ النموذج '{OllamaLlmModel}' غير متوفر. قم بتنفيذ: ollama pull {OllamaLlmModel}");
            if (!embCheck.ModelAvailable)
                warnings.Add($"⚠ نموذج التضمين '{OllamaEmbeddingModel}' غير متوفر. قم بتنفيذ: ollama pull {OllamaEmbeddingModel}");

            CurrentFileProgress = 1.0;
            OverallProgress = 1.0;

            SaveWizardSettings(
                llmProvider: "ollama",
                llmModelPath: "",
                embeddingProvider: "ollama",
                embeddingOnnxModelPath: "",
                ollamaUrl: OllamaUrl,
                ollamaModel: OllamaLlmModel,
                embeddingModel: OllamaEmbeddingModel);

            // ── Complete ──
            CurrentStep = StepComplete;
            StepTitle = "اكتمل الإعداد";
            SetupSucceeded = llmCheck.Connected; // Succeed if connected, even if models need pulling

            if (warnings.Count == 0)
            {
                CompletionMessage = "✓ Ollama متصل وجميع النماذج متوفرة. النظام جاهز.";
            }
            else
            {
                CompletionMessage = "✓ Ollama متصل. بعض النماذج تحتاج تحميل:";
                ErrorDetails = string.Join("\n", warnings);
            }

            _logger.LogInformation("Setup wizard completed (Ollama). Connected: {Connected}, LLM: {Llm}, Emb: {Emb}",
                llmCheck.Connected, llmCheck.ModelAvailable, embCheck.ModelAvailable);
        }
        catch (OperationCanceledException)
        {
            CurrentStep = StepComplete;
            StepTitle = "تم الإلغاء";
            SetupSucceeded = false;
            CompletionMessage = "تم إلغاء الإعداد.";
        }
        catch (Exception ex)
        {
            CurrentStep = StepComplete;
            StepTitle = "خطأ في الإعداد";
            SetupSucceeded = false;
            CompletionMessage = "فشل إعداد Ollama.";
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
}
