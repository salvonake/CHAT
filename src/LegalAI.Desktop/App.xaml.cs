using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using Serilog.Events;
using LegalAI.Desktop.Services;
using LegalAI.Desktop.ViewModels;
using LegalAI.Desktop.Views;
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
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // ── Single-Instance Guard ──
        _singleInstanceMutex = new Mutex(true, "Global\\LegalAI_LCDSS_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "نظام الدعم القانوني يعمل بالفعل.",
                "تحذير",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        // ── Global Exception Handlers ──
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        // ── Build Configuration ──
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        var bootstrapConfiguration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
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
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.user.json", optional: true, reloadOnChange: true)
            .AddJsonFile(dataUserConfigPath, optional: true, reloadOnChange: true)
            .Build();

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

                // ── Ingestion ──
                services.AddSingleton<IPdfExtractor, PdfPigExtractor>();
                services.AddSingleton<IDocumentChunker, LegalDocumentChunker>();

                // ── Security ──
                services.AddSingleton<IInjectionDetector, PromptInjectionDetector>();

                // ── Retrieval ──
                services.AddSingleton<BM25Index>();
                services.AddSingleton<LegalQueryAnalyzer>();
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
            wizardWindow.ShowDialog();

            if (!wizardVm.Completed)
            {
                Log.Warning("Setup wizard cancelled or failed — continuing in library-only mode");
            }
        }

        // ── Show Main Window ──
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _host.Services.GetRequiredService<MainViewModel>();
        MainWindow = mainWindow;
        mainWindow.Show();

        // ── Initialize Fail-Closed Guard ──
        var guard = _host.Services.GetRequiredService<FailClosedGuard>();
        _ = Task.Run(() => guard.InitializeAsync());
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("═══ LegalAI LCDSS Shutting Down ═══");
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
            guard?.ForceLibraryOnlyMode($"خطأ غير متوقع: {e.Exception.Message}");
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
}
