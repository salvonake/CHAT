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
using Poseidon.Desktop.Diagnostics;
using Poseidon.Desktop.Services;
using Poseidon.Application.Services;
using Poseidon.Desktop.ViewModels;
using Poseidon.Desktop.Views;
using Poseidon.Domain.DomainModules;
using Poseidon.Domain.Interfaces;
using Poseidon.Infrastructure.Audit;
using Poseidon.Infrastructure.Llm;
using Poseidon.Infrastructure.Storage;
using Poseidon.Infrastructure.Telemetry;
using Poseidon.Infrastructure.VectorStore;
using Poseidon.Ingestion.Chunking;
using Poseidon.Ingestion.Embedding;
using Poseidon.Ingestion.Extractors;
using Poseidon.Retrieval.Lexical;
using Poseidon.Retrieval.Pipeline;
using Poseidon.Retrieval.QueryAnalysis;
using Poseidon.Security.Configuration;
using Poseidon.Security.Encryption;
using Poseidon.Security.Injection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Poseidon.Desktop;

/// <summary>
/// Application entry point. Configures DI, enforces single-instance,
/// and wires the fail-closed architecture.
/// </summary>
public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstanceMutex;
    private const string SingleInstanceMutexName = "Local\\Poseidon_LCDSS_SingleInstance";
    private const string ActivationPipeName = "Poseidon_LCDSS_Activation";
    private IHost? _host;
    private bool _hostStarted;
    private CancellationTokenSource? _activationPipeCts;
    private Task? _activationPipeServerTask;
    private const int SwRestore = 9;

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
        WriteBootstrapLog("INF", "Poseidon desktop launch started.");
        var debugMode = e.Args.Any(arg => string.Equals(arg, "--debug", StringComparison.OrdinalIgnoreCase));

        // Global handlers are attached before any startup work that can fail.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // ── Single-Instance Guard ──
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            if (await TrySignalRunningInstanceAsync())
            {
                WriteBootstrapLog("WRN", "Secondary launch signaled the existing instance.");
                Shutdown(0);
                return;
            }

            if (TryActivateExistingInstanceWindow())
            {
                WriteBootstrapLog("WRN", "Secondary launch activated the existing instance window.");
                Shutdown(0);
                return;
            }

            WriteBootstrapLog("WRN", "Secondary launch could not activate the existing instance.");
            System.Windows.MessageBox.Show(
                "Poseidon est deja en cours d'execution en arriere-plan. Ouvrez-le depuis la zone de notification.",
                "Poseidon",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

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
            "Poseidon");
        Directory.CreateDirectory(dataDir);

        var modelsDir = Path.Combine(dataDir, "Models");
        Directory.CreateDirectory(modelsDir);

        var watchDir = bootstrapConfiguration["Ingestion:WatchDirectory"];
        if (string.IsNullOrWhiteSpace(watchDir))
            watchDir = Path.Combine(dataDir, "Watch");
        Directory.CreateDirectory(watchDir);

        var dataUserConfigPath = Path.Combine(dataDir, "appsettings.user.json");
        var userConfigValidationError = SecurityConfigurationValidator.ValidateUserConfigTrustBoundary(dataUserConfigPath);
        var configurationBuilder = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.user.json", optional: true, reloadOnChange: true);

        if (userConfigValidationError is null)
            configurationBuilder.AddJsonFile(dataUserConfigPath, optional: true, reloadOnChange: true);

        var configuration = configurationBuilder.Build();

        var uiCulture = configuration["Ui:Culture"];
        var resolvedUiCulture = ApplyUiCulture(uiCulture);
        ApplyLocalizedResourceDictionary(resolvedUiCulture);

        // ── Configure Serilog ──
        var logDir = Path.Combine(dataDir, "Logs");
        Directory.CreateDirectory(logDir);
        var liveLogs = new LiveLogBuffer();

        var paths = new DataPaths
        {
            DataDirectory = dataDir,
            ModelsDirectory = modelsDir,
            InstalledModelsDirectory = Path.Combine(basePath, "Models"),
            VectorDbPath = Path.Combine(dataDir, "vectors.db"),
            HnswIndexPath = Path.Combine(dataDir, "hnsw.index"),
            DocumentDbPath = Path.Combine(dataDir, "documents.db"),
            AuditDbPath = Path.Combine(dataDir, "audit.db"),
            WatchDirectory = watchDir,
            UserConfigPath = dataUserConfigPath,
            LogsDirectory = logDir,
            AppLogPath = Path.Combine(logDir, "app.log"),
            StartupLogPath = Path.Combine(logDir, "startup.log")
        };

        var loggerConfiguration = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            .Enrich.WithMachineName()
            .Enrich.WithProperty("Application", "Poseidon-LCDSS")
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .WriteTo.Sink(new LiveLogSink(liveLogs))
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} <{SourceContext}>{NewLine}{Exception}")
            .WriteTo.File(
                paths.AppLogPath,
                retainedFileCountLimit: 30,
                fileSizeLimitBytes: 50 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] {SourceContext}{NewLine}  {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                paths.StartupLogPath,
                retainedFileCountLimit: 10,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] {SourceContext}{NewLine}  {Message:lj}{NewLine}{Exception}");

        if (debugMode)
        {
            loggerConfiguration.MinimumLevel.Debug();
        }

        Log.Logger = loggerConfiguration.CreateLogger();

        Log.Information("═══ Poseidon LCDSS Starting ═══");
        WriteBootstrapLog("INF", $"Serilog initialized. AppLog={paths.AppLogPath}; StartupLog={paths.StartupLogPath}");
        if (debugMode)
            Log.Information("Debug mode enabled from --debug flag");
        if (userConfigValidationError is not null)
        {
            WriteBootstrapLog("ERR", $"User configuration is invalid: {userConfigValidationError}");
            Log.Error("User configuration is invalid and will be handled in Recovery: {Error}", userConfigValidationError);
        }

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
                services.AddSingleton<IConfiguration>(configuration);
                services.AddSingleton(paths);
                services.AddSingleton(liveLogs);
                services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));

                // ── Encryption ──
                services.AddSingleton<ReloadableEncryptionService>();
                services.AddSingleton<IEncryptionService>(sp =>
                    sp.GetRequiredService<ReloadableEncryptionService>());
                services.AddSingleton<IReloadableRuntimeService>(sp =>
                    sp.GetRequiredService<ReloadableEncryptionService>());

                // ── LLM Service (configurable provider) ──
                services.AddSingleton<ReloadableLlmService>();
                services.AddSingleton<ILlmService>(sp =>
                    sp.GetRequiredService<ReloadableLlmService>());
                services.AddSingleton<IReloadableRuntimeService>(sp =>
                    sp.GetRequiredService<ReloadableLlmService>());

                // ── Embedding Service (configurable provider) ──
                services.AddSingleton<ReloadableEmbeddingService>();
                services.AddSingleton<IEmbeddingService>(sp =>
                    sp.GetRequiredService<ReloadableEmbeddingService>());
                services.AddSingleton<IReloadableRuntimeService>(sp =>
                    sp.GetRequiredService<ReloadableEmbeddingService>());

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
                var mediatRLicenseKey = SecurityConfigurationValidator.ResolveMediatRLicenseKey(cfg);
                services.AddMediatR(mCfg =>
                {
                    mCfg.RegisterServicesFromAssemblyContaining<Poseidon.Application.AssemblyMarker>();
                    if (!string.IsNullOrWhiteSpace(mediatRLicenseKey))
                        mCfg.LicenseKey = mediatRLicenseKey;
                });

                // ── Desktop Services ──
                services.AddSingleton<IDispatcherService, WpfDispatcherService>();
                services.AddSingleton<ModelIntegrityService>();
                services.AddSingleton<ModelDownloadService>();
                services.AddSingleton<StartupModeController>();
                services.AddSingleton<SystemHealthService>();
                services.AddSingleton<RuntimeConfigurationService>();
                services.AddSingleton<FailClosedGuard>();
                services.AddSingleton<DiagnosticWindowService>();
                services.AddHostedService<DesktopFileWatcherService>();

                // ── ViewModels ──
                services.AddSingleton<DiagnosticViewModel>();
                services.AddTransient<SetupWizardViewModel>();
                services.AddTransient<AskViewModel>();
                services.AddTransient<ChatViewModel>();
                services.AddTransient<DocumentsViewModel>();
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<HealthViewModel>();
                services.AddSingleton<MainViewModel>();

                // ── Main Window ──
                services.AddSingleton<MainWindow>();
                services.AddTransient<StartupDiagnosticWindow>();
            })
            .Build();

        var runtimeConfig = _host.Services.GetRequiredService<RuntimeConfigurationService>();
        var provisioningRecoveryReason = userConfigValidationError;
        if (provisioningRecoveryReason is null && ShouldShowSetupWizard(paths, configuration))
        {
            Log.Warning("First-launch provisioning required before hosted services start");
            var setupCompleted = ShowSetupWizard();
            if (setupCompleted)
            {
                Log.Information("Setup wizard completed; reloading runtime configuration before service startup");
                try
                {
                    if (configuration is IConfigurationRoot root)
                        root.Reload();

                    if (!await runtimeConfig.ReloadAsync("First-launch provisioning completed"))
                    {
                        provisioningRecoveryReason = "Runtime configuration reload failed after first-launch provisioning.";
                        Log.Error("Runtime configuration reload failed after first-launch provisioning");
                    }

                    var postWizardDecision = FirstLaunchProvisioning.Evaluate(paths, configuration);
                    if (provisioningRecoveryReason is null &&
                        postWizardDecision.Action != FirstLaunchProvisioningAction.Proceed)
                    {
                        provisioningRecoveryReason = postWizardDecision.Reason;
                    }
                }
                catch (Exception ex)
                {
                    provisioningRecoveryReason = $"Failed to reload generated configuration: {ex.Message}";
                    Log.Error(ex, "Failed to reload generated configuration after setup wizard");
                }
            }
            else
            {
                provisioningRecoveryReason = "Setup wizard was cancelled or did not complete successfully.";
                Log.Warning("Setup wizard closed without successful provisioning");
            }
        }

        if (provisioningRecoveryReason is null)
        {
            try
            {
                SecurityConfigurationValidator.ValidateDesktop(configuration);
            }
            catch (Exception ex)
            {
                provisioningRecoveryReason = $"Configuration security validation failed: {ex.Message}";
                Log.Error(ex, "Configuration security validation failed");
            }
        }

        runtimeConfig.Start();

        var modelIntegrity = _host.Services.GetRequiredService<ModelIntegrityService>();
        await modelIntegrity.VerifyOnStartupAsync();

        var healthService = _host.Services.GetRequiredService<SystemHealthService>();
        var startupSnapshot = await healthService.CheckAllAsync();

        var guard = _host.Services.GetRequiredService<FailClosedGuard>();
        await guard.InitializeAsync();
        if (provisioningRecoveryReason is not null)
            guard.ForceRecoveryMode(provisioningRecoveryReason);

        var shouldStartHostedServices = provisioningRecoveryReason is null &&
                                        startupSnapshot.Mode is StartupMode.Full or StartupMode.Degraded;
        if (shouldStartHostedServices)
        {
            await _host.StartAsync();
            _hostStarted = true;
        }
        else
        {
            Log.Warning(
                "Hosted ingestion services were not started. Mode={Mode}, Reason={Reason}",
                startupSnapshot.Mode,
                provisioningRecoveryReason ?? "Recovery mode");
        }

        // ── Show Main Window ──
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _host.Services.GetRequiredService<MainViewModel>();
        mainWindow.FlowDirection = FlowDirection.LeftToRight;
        MainWindow = mainWindow;
        mainWindow.Show();
        mainWindow.RestoreAndActivateWindow();

        if (startupSnapshot.Mode == StartupMode.Recovery || provisioningRecoveryReason is not null || debugMode)
        {
            _host.Services.GetRequiredService<DiagnosticViewModel>().ShowDebugFields = debugMode;
            _host.Services.GetRequiredService<DiagnosticWindowService>().ShowOrActivate();
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("═══ Poseidon LCDSS Shutting Down ═══");

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
            if (_hostStarted)
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
        return FirstLaunchProvisioning.Evaluate(paths, config).Action == FirstLaunchProvisioningAction.ShowWizard;
    }

    private bool ShowSetupWizard()
    {
        if (_host is null)
            return false;

        var vm = _host.Services.GetRequiredService<SetupWizardViewModel>();
        var window = new SetupWizardWindow
        {
            FlowDirection = FlowDirection.LeftToRight
        };
        window.BindViewModel(vm);
        window.ShowDialog();
        return vm.Completed;
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
        WriteBootstrapLog("ERR", $"Fatal exception from {source}: {ex}");
        try
        {
            Log.Fatal(ex, "═══ Poseidon LCDSS FATAL ═══ Source: {Source}", source);
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

    private static CultureInfo ApplyUiCulture(string? configuredCulture)
    {
        var cultureName = string.IsNullOrWhiteSpace(configuredCulture)
            ? "fr-FR"
            : configuredCulture.Trim();

        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(cultureName);
        }
        catch
        {
            culture = CultureInfo.GetCultureInfo("fr-FR");
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

        return culture;
    }

    private static void ApplyLocalizedResourceDictionary(CultureInfo culture)
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
                source.Contains("Resources/Strings/Strings.en.xaml", StringComparison.OrdinalIgnoreCase) ||
                source.Contains("Resources/Strings/Strings.fr.xaml", StringComparison.OrdinalIgnoreCase))
            {
                dictionaries.RemoveAt(i);
            }
        }

        var language = culture.TwoLetterISOLanguageName;
        var resourcePath = language switch
        {
            "ar" => "Resources/Strings/Strings.ar.xaml",
            "en" => "Resources/Strings/Strings.en.xaml",
            _ => "Resources/Strings/Strings.fr.xaml"
        };

        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(resourcePath, UriKind.Relative)
        });
    }

    private static void WriteBootstrapLog(string level, string message)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Poseidon",
                "Logs");
            Directory.CreateDirectory(logDir);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [bootstrap] {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(logDir, "startup.log"), line, Encoding.UTF8);
            File.AppendAllText(Path.Combine(logDir, "app.log"), line, Encoding.UTF8);
        }
        catch
        {
            // Bootstrap logging must never block launch or mask the original failure.
        }
    }
}



