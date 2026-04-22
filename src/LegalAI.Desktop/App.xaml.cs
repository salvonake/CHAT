using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Serilog;
using Serilog.Events;
using LegalAI.Desktop.Services;
using LegalAI.Application.Services;
using LegalAI.Desktop.ViewModels;
using LegalAI.Desktop.Views;
using LegalAI.Domain.DomainModules;
using LegalAI.Domain.Interfaces;
using LegalAI.Infrastructure.Audit;
using LegalAI.Infrastructure.Llm;
using LegalAI.Infrastructure.Storage;
using LegalAI.Infrastructure.Telemetry;
using LegalAI.Infrastructure.VectorStore;
using LegalAI.Ingestion.Chunking;
using LegalAI.Ingestion.Embedding;
using LegalAI.Ingestion.Extractors;
using LegalAI.Retrieval.Lexical;
using LegalAI.Retrieval.Pipeline;
using LegalAI.Retrieval.QueryAnalysis;
using LegalAI.Security.Encryption;
using LegalAI.Security.Injection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LegalAI.Desktop;

/// <summary>
/// Application entry point. Configures DI, enforces single-instance,
/// and wires the fail-closed architecture.
/// </summary>
public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstanceMutex;
    private const string SingleInstanceMutexName = "Local\\LegalAI_LCDSS_SingleInstance";
    private const string ActivationPipeName = "LegalAI_LCDSS_Activation";
    private IHost? _host;
    private CancellationTokenSource? _activationPipeCts;
    private Task? _activationPipeServerTask;
    private const int SwRestore = 9;
    private bool _isArabicUi;

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    protected override async void OnStartup(StartupEventArgs e)
    {
        // ── Single-Instance Guard ──
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            if (await TrySignalRunningInstanceAsync())
            {
                Shutdown(0);
                return;
            }

            if (TryActivateExistingInstanceWindow())
            {
                Shutdown(0);
                return;
            }

            System.Windows.MessageBox.Show(
                "LegalAI is already running in the background. Open it from the system tray.",
                "LegalAI",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        // ── Global Exception Handlers ──
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        _activationPipeCts = new CancellationTokenSource();
        _activationPipeServerTask = RunActivationPipeServerAsync(_activationPipeCts.Token);

        // ── Build Configuration ──
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        var bootstrapConfiguration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.user.json", optional: true, reloadOnChange: true)
            .Build();

        // ── Resolve DataPaths ──
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LegalAI");
        Directory.CreateDirectory(dataDir);

        var modelsDir = Path.Combine(dataDir, "Models");
        Directory.CreateDirectory(modelsDir);

        var watchDir = bootstrapConfiguration["Ingestion:WatchDirectory"];
        if (string.IsNullOrWhiteSpace(watchDir))
            watchDir = Path.Combine(dataDir, "Watch");
        Directory.CreateDirectory(watchDir);

        var dataUserConfigPath = Path.Combine(dataDir, "appsettings.user.json");
        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.user.json", optional: true, reloadOnChange: true)
            .AddJsonFile(dataUserConfigPath, optional: true, reloadOnChange: true)
            .Build();

        var uiCulture = configuration["Ui:Culture"];
        _isArabicUi = ApplyUiCulture(uiCulture);
        ApplyLocalizedResourceDictionary(_isArabicUi);

        var paths = new DataPaths
        {
            DataDirectory = dataDir,
            ModelsDirectory = modelsDir,
            VectorDbPath = Path.Combine(dataDir, "vectors.db"),
            HnswIndexPath = Path.Combine(dataDir, "hnsw.index"),
            DocumentDbPath = Path.Combine(dataDir, "documents.db"),
            AuditDbPath = Path.Combine(dataDir, "audit.db"),
            WatchDirectory = watchDir
        };

        // ── Configure Serilog ──
        var logDir = Path.Combine(dataDir, "Logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            .Enrich.WithMachineName()
            .Enrich.WithProperty("Application", "LegalAI-LCDSS")
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} <{SourceContext}>{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(logDir, "legalai-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                fileSizeLimitBytes: 50 * 1024 * 1024,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] {SourceContext}{NewLine}  {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(logDir, "legalai-errors-.log"),
                restrictedToMinimumLevel: LogEventLevel.Error,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 90,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] {SourceContext}{NewLine}  {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("═══ LegalAI LCDSS Starting ═══");

        // ── Build Host ──
        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureAppConfiguration(cb =>
            {
                cb.Sources.Clear();
                cb.AddConfiguration(configuration);
            })
            .ConfigureServices((ctx, services) =>
            {
                var cfg = ctx.Configuration;

                // ── Core Singletons ──
                services.AddSingleton(paths);
                services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));

                // ── Encryption ──
                var encPassphrase = cfg["Security:EncryptionPassphrase"];
                var encEnabled = cfg.GetValue("Security:EncryptionEnabled", false);
                services.AddSingleton<IEncryptionService>(sp =>
                    new AesGcmEncryptionService(
                        encPassphrase,
                        sp.GetRequiredService<ILogger<AesGcmEncryptionService>>(),
                        encEnabled));

                // ── LLM Service (configurable provider) ──
                var llmProvider = (cfg["Llm:Provider"] ?? "llamasharp").ToLowerInvariant();
                if (llmProvider == "ollama")
                {
                    services.AddSingleton<ILlmService>(sp =>
                    {
                        var http = new HttpClient { BaseAddress = new Uri(cfg["Ollama:Url"] ?? "http://localhost:11434") };
                        return new OllamaLlmService(
                            http,
                            cfg["Ollama:Model"] ?? "qwen2.5:14b",
                            sp.GetRequiredService<ILogger<OllamaLlmService>>());
                    });
                }
                else
                {
                    services.AddSingleton<ILlmService>(sp =>
                    {
                        var modelPath = cfg["Llm:ModelPath"];
                        if (string.IsNullOrWhiteSpace(modelPath))
                            modelPath = Path.Combine(paths.ModelsDirectory, "model.gguf");
                        return new LLamaSharpLlmService(
                            modelPath,
                            cfg.GetValue("Llm:GpuLayers", -1),
                            cfg.GetValue("Llm:ContextSize", 8192),
                            sp.GetRequiredService<ILogger<LLamaSharpLlmService>>());
                    });
                }

                // ── Embedding Service (configurable provider) ──
                var embProvider = (cfg["Embedding:Provider"] ?? "onnx").ToLowerInvariant();
                var embDimension = cfg.GetValue("Embedding:Dimension", 768);
                if (embProvider == "ollama")
                {
                    services.AddSingleton<IEmbeddingService>(sp =>
                    {
                        var http = new HttpClient { BaseAddress = new Uri(cfg["Ollama:Url"] ?? "http://localhost:11434") };
                        return new OllamaEmbeddingService(
                            http,
                            cfg["Embedding:Model"] ?? "nomic-embed-text",
                            sp.GetRequiredService<ILogger<OllamaEmbeddingService>>(),
                            embDimension);
                    });
                }
                else
                {
                    services.AddSingleton<IEmbeddingService>(sp =>
                    {
                        var onnxPath = cfg["Embedding:OnnxModelPath"];
                        if (string.IsNullOrWhiteSpace(onnxPath))
                            onnxPath = Path.Combine(paths.ModelsDirectory, "embedding_model");

                        // vocab.txt auto-discovered in same directory as model
                        var vocabPath = cfg["Embedding:VocabPath"];
                        if (string.IsNullOrWhiteSpace(vocabPath))
                            vocabPath = null; // will auto-resolve from model directory

                        return new OnnxArabicEmbeddingService(
                            onnxPath,
                            sp.GetRequiredService<ILogger<OnnxArabicEmbeddingService>>(),
                            vocabPath,
                            embDimension);
                    });
                }

                // ── Vector Store ──
                services.AddSingleton<IVectorStore>(sp =>
                    new EmbeddedVectorStore(
                        paths.VectorDbPath,
                        paths.HnswIndexPath,
                        sp.GetRequiredService<ILogger<EmbeddedVectorStore>>()));

                // ── Document Store ──
                services.AddSingleton<IDocumentStore>(sp =>
                    new SqliteDocumentStore(
                        paths.DocumentDbPath,
                        sp.GetRequiredService<ILogger<SqliteDocumentStore>>()));

                // ── Audit ──
                services.AddSingleton<IAuditService>(sp =>
                    new SqliteAuditService(
                        paths.AuditDbPath,
                        sp.GetRequiredService<IEncryptionService>(),
                        sp.GetRequiredService<ILogger<SqliteAuditService>>()));

                // ── Metrics ──
                services.AddSingleton<IMetricsCollector, InMemoryMetricsCollector>();

                // ── Domain Modules ──
                foreach (var module in BuiltInDomainModules.CreateDefaultSet())
                {
                    services.AddSingleton<IDomainModule>(module);
                }

                services.AddSingleton<IDomainModuleRegistry>(sp =>
                {
                    var activeDomain = cfg["Domain:ActiveModule"] ?? BuiltInDomainModules.Legal;
                    return new InMemoryDomainModuleRegistry(sp.GetServices<IDomainModule>(), activeDomain);
                });

                services.AddSingleton<IPromptTemplateEngine, PromptTemplateEngine>();

                // ── Ingestion ──
                services.AddSingleton<IPdfExtractor, PdfPigExtractor>();
                services.AddSingleton<IDomainChunker, LegalDocumentChunker>();
                services.AddSingleton<IDocumentChunker>(sp => sp.GetRequiredService<IDomainChunker>());

                // ── Security ──
                services.AddSingleton<IInjectionDetector, PromptInjectionDetector>();

                // ── Retrieval ──
                services.AddSingleton<BM25Index>();
                services.AddSingleton<IDomainQueryAnalyzer, LegalQueryAnalyzer>();
                services.AddSingleton<IRetrievalPipeline, LegalRetrievalPipeline>();

                // ── MediatR ──
                services.AddMediatR(mCfg =>
                    mCfg.RegisterServicesFromAssemblyContaining<LegalAI.Application.AssemblyMarker>());

                // ── Desktop Services ──
                services.AddSingleton<IDispatcherService, WpfDispatcherService>();
                services.AddSingleton<ModelIntegrityService>();
                services.AddSingleton<ModelDownloadService>();
                services.AddSingleton<FailClosedGuard>();
                services.AddHostedService<DesktopFileWatcherService>();

                // ── ViewModels ──
                services.AddTransient<SetupWizardViewModel>();
                services.AddTransient<AskViewModel>();
                services.AddTransient<ChatViewModel>();
                services.AddTransient<DocumentsViewModel>();
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<HealthViewModel>();
                services.AddSingleton<MainViewModel>();

                // ── Main Window ──
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        // ── First-Run Setup Wizard ──
        if (ShouldShowSetupWizard(paths, configuration))
        {
            Log.Information("Models not found — launching setup wizard");
            var wizardVm = _host.Services.GetRequiredService<SetupWizardViewModel>();
            var wizardWindow = new SetupWizardWindow();
            wizardWindow.BindViewModel(wizardVm);
            wizardWindow.FlowDirection = _isArabicUi ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            wizardWindow.Loaded += (_, _) =>
            {
                if (wizardWindow.WindowState == WindowState.Minimized)
                    wizardWindow.WindowState = WindowState.Normal;
                wizardWindow.Activate();
                wizardWindow.Topmost = true;
                wizardWindow.Topmost = false;
                wizardWindow.Focus();
            };
            wizardWindow.ShowDialog();

            if (!wizardVm.Completed)
            {
                Log.Warning("Setup wizard cancelled or failed — continuing in library-only mode");
            }
        }

        // ── Show Main Window ──
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _host.Services.GetRequiredService<MainViewModel>();
        mainWindow.FlowDirection = _isArabicUi ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        MainWindow = mainWindow;
        mainWindow.Show();
        mainWindow.RestoreAndActivateWindow();

        // ── Initialize Fail-Closed Guard ──
        var guard = _host.Services.GetRequiredService<FailClosedGuard>();
        _ = Task.Run(() => guard.InitializeAsync());
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("═══ LegalAI LCDSS Shutting Down ═══");

        if (_activationPipeCts is not null)
        {
            _activationPipeCts.Cancel();

            if (_activationPipeServerTask is not null)
            {
                try
                {
                    await _activationPipeServerTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch
                {
                }
            }

            _activationPipeCts.Dispose();
            _activationPipeCts = null;
            _activationPipeServerTask = null;
        }

        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }

    private async Task<bool> TrySignalRunningInstanceAsync()
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var client = new NamedPipeClientStream(
                serverName: ".",
                pipeName: ActivationPipeName,
                direction: PipeDirection.Out,
                options: PipeOptions.Asynchronous);

            await client.ConnectAsync(timeoutCts.Token);

            using var writer = new StreamWriter(client, Encoding.UTF8, 1024, leaveOpen: true)
            {
                AutoFlush = true
            };
            await writer.WriteLineAsync("ACTIVATE");

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task RunActivationPipeServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    ActivationPipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken);

                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                var command = await reader.ReadLineAsync(cancellationToken);

                if (string.Equals(command, "ACTIVATE", StringComparison.OrdinalIgnoreCase))
                {
                    await Dispatcher.InvokeAsync(HandleSecondaryActivationRequest);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Activation pipe listener failed unexpectedly");
            }
        }
    }

    private void HandleSecondaryActivationRequest()
    {
        if (MainWindow is MainWindow shellWindow)
        {
            shellWindow.HandleExternalActivationRequest();
            return;
        }

        if (_host is null)
            return;

        try
        {
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            if (mainWindow.DataContext is null)
            {
                mainWindow.DataContext = _host.Services.GetRequiredService<MainViewModel>();
            }

            MainWindow = mainWindow;
            if (!mainWindow.IsVisible)
            {
                mainWindow.Show();
            }

            mainWindow.RestoreAndActivateWindow();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restore main window after activation request");
        }
    }

    // ═════════════════════════════════════════
    //  First-Run Detection
    // ═════════════════════════════════════════

    /// <summary>
    /// Returns true if the setup wizard should be shown (models not yet present).
    /// For Ollama-configured users, always returns false since no local files are needed.
    /// </summary>
    private static bool ShouldShowSetupWizard(DataPaths paths, IConfiguration config)
    {
        var llmProvider = (config["Llm:Provider"] ?? "llamasharp").ToLowerInvariant();
        if (llmProvider == "ollama")
            return false;

        var configuredLlmPath = config["Llm:ModelPath"];
        var configuredOnnxPath = config["Embedding:OnnxModelPath"];
        if ((!string.IsNullOrWhiteSpace(configuredLlmPath) && File.Exists(configuredLlmPath)) ||
            (!string.IsNullOrWhiteSpace(configuredOnnxPath) && File.Exists(configuredOnnxPath)))
        {
            return false;
        }

        var modelsDir = paths.ModelsDirectory;
        if (!Directory.Exists(modelsDir))
            return true;

        // Check for any GGUF or ONNX files
        var hasGguf = Directory.GetFiles(modelsDir, "*.gguf", SearchOption.TopDirectoryOnly).Length > 0;
        var hasOnnx = Directory.GetFiles(modelsDir, "*.onnx", SearchOption.TopDirectoryOnly).Length > 0;

        // Show wizard if neither model type is present
        return !hasGguf && !hasOnnx;
    }

    // ═════════════════════════════════════════
    //  Global Exception Handlers
    // ═════════════════════════════════════════

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogFatalException("UI Thread", e.Exception);
        e.Handled = true; // Prevent crash — fail-closed means stay alive in library-only mode

        try
        {
            var guard = _host?.Services.GetService<FailClosedGuard>();
            guard?.ForceLibraryOnlyMode($"Unexpected error: {e.Exception.Message}");
        }
        catch { /* swallow — we're already in error state */ }
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogFatalException("AppDomain", ex);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogFatalException("UnobservedTask", e.Exception);
        e.SetObserved(); // Prevent process termination
    }

    private static void LogFatalException(string source, Exception ex)
    {
        try
        {
            Log.Fatal(ex, "═══ LegalAI LCDSS FATAL ═══ Source: {Source}", source);
        }
        catch { /* last resort — nothing we can do */ }
    }

    private static bool TryActivateExistingInstanceWindow()
    {
        try
        {
            var current = System.Diagnostics.Process.GetCurrentProcess();
            var existingProcesses = System.Diagnostics.Process
                .GetProcessesByName(current.ProcessName)
                .Where(p => p.Id != current.Id)
                .ToList();

            foreach (var process in existingProcesses)
            {
                var hwnd = process.MainWindowHandle;
                if (hwnd == nint.Zero)
                {
                    hwnd = FindVisibleTopLevelWindowForProcess(process.Id);
                }

                if (hwnd == nint.Zero)
                    continue;

                ShowWindowAsync(hwnd, SwRestore);
                SetForegroundWindow(hwnd);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static nint FindVisibleTopLevelWindowForProcess(int processId)
    {
        nint found = nint.Zero;
        EnumWindows((hWnd, lParam) =>
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == (uint)processId && IsWindowVisible(hWnd))
            {
                found = hWnd;
                return false;
            }

            return true;
        }, nint.Zero);

        return found;
    }

    private static bool ApplyUiCulture(string? configuredCulture)
    {
        var cultureName = string.IsNullOrWhiteSpace(configuredCulture)
            ? "en-US"
            : configuredCulture.Trim();

        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(cultureName);
        }
        catch
        {
            culture = CultureInfo.GetCultureInfo("en-US");
        }

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        try
        {
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
        }
        catch
        {
        }

        return culture.TwoLetterISOLanguageName.Equals("ar", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyLocalizedResourceDictionary(bool isArabicUi)
    {
        if (Current?.Resources is null)
            return;

        var dictionaries = Current.Resources.MergedDictionaries;
        for (int i = dictionaries.Count - 1; i >= 0; i--)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (string.IsNullOrWhiteSpace(source))
                continue;

            if (source.Contains("Resources/Strings/Strings.ar.xaml", StringComparison.OrdinalIgnoreCase) ||
                source.Contains("Resources/Strings/Strings.en.xaml", StringComparison.OrdinalIgnoreCase))
            {
                dictionaries.RemoveAt(i);
            }
        }

        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                isArabicUi ? "Resources/Strings/Strings.ar.xaml" : "Resources/Strings/Strings.en.xaml",
                UriKind.Relative)
        });
    }
}

