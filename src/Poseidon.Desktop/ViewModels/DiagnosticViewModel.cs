using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Poseidon.Desktop.Diagnostics;
using Poseidon.Desktop.Services;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace Poseidon.Desktop.ViewModels;

public partial class DiagnosticViewModel : ObservableObject
{
    private readonly SystemHealthService _healthService;
    private readonly RuntimeConfigurationService _configurationService;
    private readonly LiveLogBuffer _liveLogBuffer;
    private readonly DataPaths _paths;
    private readonly IDispatcherService _dispatcher;
    private readonly ILogger<DiagnosticViewModel> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _operationLock = new();
    private readonly Dictionary<string, long> _scopeOperations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastRetryByScope = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _busyScopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _logRefreshTimer;
    private long _nextOperationId;
    private long _latestStateOperationId;
    private long _queuedRefreshOperationId;
    private int _refreshQueued;
    private int _logRefreshPending;
    private const int RetryCoalesceMilliseconds = 750;
    private static readonly TimeSpan LogRefreshInterval = TimeSpan.FromMilliseconds(250);

    [ObservableProperty]
    private string _modeText = "Initialisation";

    [ObservableProperty]
    private bool _canAskQuestions;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private bool _showDebugFields;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _selectedLogLevel = "Information";

    [ObservableProperty]
    private string _logSearchText = "";

    public ObservableCollection<DiagnosticGroupViewModel> Groups { get; } = [];
    public ObservableCollection<LiveLogEntryViewModel> Logs { get; } = [];

    public DiagnosticViewModel(
        SystemHealthService healthService,
        RuntimeConfigurationService configurationService,
        LiveLogBuffer liveLogBuffer,
        DataPaths paths,
        IDispatcherService dispatcher,
        ILogger<DiagnosticViewModel> logger)
    {
        _healthService = healthService;
        _configurationService = configurationService;
        _liveLogBuffer = liveLogBuffer;
        _paths = paths;
        _dispatcher = dispatcher;
        _logger = logger;
        _logRefreshTimer = new Timer(OnLogRefreshTimer);

        _healthService.HealthChanged += OnHealthChanged;
        _configurationService.ConfigurationReloaded += OnConfigurationReloaded;
        _liveLogBuffer.EntryAdded += OnLogEntryAdded;

        if (_healthService.LastSnapshot is not null)
            ApplySnapshot(_healthService.LastSnapshot);

        RefreshLogs();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var operationId = StartGlobalOperation();
        await RunHealthRefreshAsync("Actualisation manuelle du diagnostic", operationId);
    }

    [RelayCommand]
    private async Task RetryAsync(DiagnosticIssueViewModel? issue)
    {
        var scope = ResolveScope(issue);
        var operationId = BeginScopedOperation(scope, "Nouvelle tentative en cours", issue);
        if (operationId == 0)
            return;

        if (IsRetryCoalesced(scope))
        {
            EndScopedOperation(scope, operationId);
            StatusMessage = $"Nouvelle tentative deja en cours pour {scope}.";
            _logger.LogInformation("Diagnostic retry coalesced for {Component}", scope);
            return;
        }

        StatusMessage = issue is null
            ? "Nouvelle tentative en cours..."
            : $"Nouvelle tentative: {issue.Component}";

        try
        {
            await _configurationService.ReloadAsync("Manual retry from diagnostics");
            await RunHealthRefreshAsync($"Nouvelle tentative: {scope}", operationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagnostic retry failed for {Component}", scope);
            if (issue is not null)
                issue.ValidationError = ex.Message;
            StatusMessage = $"Nouvelle tentative impossible: {ex.Message}";
        }
        finally
        {
            EndScopedOperation(scope, operationId);
        }
    }

    [RelayCommand]
    private async Task FixAsync(DiagnosticIssueViewModel? issue)
    {
        if (issue is null || issue.FixAction is null)
            return;

        var scope = ResolveScope(issue);
        var operationId = BeginScopedOperation(scope, "Correction en cours", issue);
        if (operationId == 0)
            return;

        try
        {
            switch (issue.FixAction)
            {
                case FixAction.EditLlmPath:
                    await FixLlmPathAsync(issue);
                    break;
                case FixAction.EditEmbeddingPath:
                    await FixEmbeddingPathAsync(issue);
                    break;
                case FixAction.EditOllamaEndpoint:
                    await FixOllamaEndpointAsync(issue);
                    break;
                case FixAction.EnableEncryption:
                    await FixEncryptionAsync(issue);
                    break;
                case FixAction.RepairConfig:
                    await _configurationService.RepairConfigAsync();
                    break;
                case FixAction.OpenLogs:
                    OpenLogsFolder();
                    break;
                case FixAction.OpenSettings:
                    StatusMessage = "Ouvrez l'onglet Parametres pour modifier ce point.";
                    break;
                case FixAction.RetryService:
                    await RetryAsync(issue);
                    break;
            }

            await RunHealthRefreshAsync($"Correction: {scope}", operationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagnostic fix failed for {Component}", issue.Component);
            issue.ValidationError = ex.Message;
            StatusMessage = $"Correction impossible: {ex.Message}";
        }
        finally
        {
            EndScopedOperation(scope, operationId);
        }
    }

    [RelayCommand]
    private void ViewLogs(DiagnosticIssueViewModel? issue)
    {
        if (issue is not null)
            LogSearchText = issue.Component;

        RefreshLogs();
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = _paths.LogsDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Impossible d'ouvrir les journaux: {ex.Message}";
            _logger.LogError(ex, "Failed to open logs folder");
        }
    }

    [RelayCommand]
    private void ClearLogView()
    {
        _liveLogBuffer.ClearView();
        RefreshLogs();
    }

    partial void OnSelectedLogLevelChanged(string value) => ScheduleLogRefresh();
    partial void OnLogSearchTextChanged(string value) => ScheduleLogRefresh();

    private async Task FixLlmPathAsync(DiagnosticIssueViewModel issue)
    {
        var path = issue.EditValue?.Trim() ?? "";
        if (!_configurationService.ValidateLlmPath(path, out var error))
        {
            issue.ValidationError = error;
            return;
        }

        await _configurationService.SaveSettingAsync("Llm", "ModelPath", path);
        issue.ValidationError = "";
        StatusMessage = "Chemin LLM applique sans redemarrage.";
    }

    private async Task FixEmbeddingPathAsync(DiagnosticIssueViewModel issue)
    {
        var path = issue.EditValue?.Trim() ?? "";
        if (!_configurationService.ValidateEmbeddingPath(path, out var error))
        {
            issue.ValidationError = error;
            return;
        }

        await _configurationService.SaveSettingAsync("Embedding", "OnnxModelPath", path);
        issue.ValidationError = "";
        StatusMessage = "Chemin d'embedding applique sans redemarrage.";
    }

    private async Task FixOllamaEndpointAsync(DiagnosticIssueViewModel issue)
    {
        var url = issue.EditValue?.Trim() ?? "";
        if (!_configurationService.ValidateOllamaEndpoint(url, out var error))
        {
            issue.ValidationError = error;
            return;
        }

        await _configurationService.SaveSettingAsync("Ollama", "Url", url);
        issue.ValidationError = "";
        StatusMessage = "Endpoint Ollama applique sans redemarrage.";
    }

    private async Task FixEncryptionAsync(DiagnosticIssueViewModel issue)
    {
        var secret = issue.SecretValue ?? "";
        if (string.IsNullOrWhiteSpace(secret))
        {
            issue.ValidationError = "Saisissez une phrase secrete de chiffrement.";
            return;
        }

        await _configurationService.SaveEncryptionSecretAsync(secret);
        issue.SecretValue = "";
        issue.ValidationError = "";
        StatusMessage = "Chiffrement active avec secret protege par DPAPI.";
    }

    private void OnHealthChanged(object? sender, SystemHealthSnapshot snapshot)
    {
        _ = _dispatcher.InvokeAsync(() => ApplySnapshot(snapshot));
    }

    private void OnConfigurationReloaded(object? sender, ConfigurationReloadedEventArgs e)
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            StatusMessage = e.Success
                ? e.Message
                : $"Echec du rechargement: {e.Exception?.Message ?? e.Message}";
        });

        if (e.Success)
        {
            var operationId = StartGlobalOperation();
            _ = RunHealthRefreshAsync("Rechargement configuration", operationId);
        }
    }

    private void OnLogEntryAdded(object? sender, LiveLogEntry entry)
    {
        ScheduleLogRefresh();
    }

    private void ApplySnapshot(SystemHealthSnapshot snapshot)
    {
        ModeText = snapshot.Mode switch
        {
            StartupMode.Full => "Mode complet",
            StartupMode.Degraded => "Mode degrade",
            StartupMode.Recovery => "Mode recuperation",
            _ => snapshot.Mode.ToString()
        };
        CanAskQuestions = snapshot.CanAskQuestions;

        Groups.Clear();
        foreach (var group in snapshot.Results.GroupBy(r => ResolveGroupName(r.Component)))
        {
            var vm = new DiagnosticGroupViewModel(group.Key);
            foreach (var result in group.OrderByDescending(r => r.Status == HealthStatus.Error).ThenByDescending(r => r.Status == HealthStatus.Warning))
            {
                var issue = DiagnosticIssueViewModel.FromResult(result);
                ApplyBusyState(issue);
                vm.Issues.Add(issue);
            }

            Groups.Add(vm);
        }
    }

    private async Task RunHealthRefreshAsync(string reason, long operationId)
    {
        if (!await _refreshLock.WaitAsync(0))
        {
            Interlocked.Exchange(ref _refreshQueued, 1);
            Interlocked.Exchange(ref _queuedRefreshOperationId, operationId);
            StatusMessage = "Diagnostic en cours; actualisation planifiee.";
            _logger.LogInformation("Diagnostic refresh queued: {Reason}", reason);
            return;
        }

        IsRefreshing = true;
        try
        {
            var activeOperationId = operationId;
            do
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                _logger.LogInformation("Diagnostic refresh started: {Reason}", reason);
                var snapshot = await _healthService.CheckAllAsync();
                if (IsLatestGlobalOperation(activeOperationId))
                {
                    ApplySnapshot(snapshot);
                    StatusMessage = snapshot.Mode == StartupMode.Recovery
                        ? "Diagnostic actualise: recuperation requise."
                        : "Diagnostic actualise.";
                }
                else
                {
                    _logger.LogInformation("Discarded stale diagnostic refresh result: {Reason}", reason);
                }

                activeOperationId = Interlocked.Read(ref _queuedRefreshOperationId);
            }
            while (Interlocked.CompareExchange(ref _refreshQueued, 0, 1) == 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagnostic refresh failed: {Reason}", reason);
            StatusMessage = $"Echec du diagnostic: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
            _refreshLock.Release();
        }
    }

    private long StartGlobalOperation()
    {
        var operationId = Interlocked.Increment(ref _nextOperationId);
        Interlocked.Exchange(ref _latestStateOperationId, operationId);
        return operationId;
    }

    private long BeginScopedOperation(string scope, string busyReason, DiagnosticIssueViewModel? issue)
    {
        lock (_operationLock)
        {
            if (_busyScopes.ContainsKey(scope))
            {
                var reason = _busyScopes[scope];
                if (issue is not null)
                    issue.ValidationError = $"Action deja en cours: {reason}.";

                StatusMessage = $"Action deja en cours pour {scope}.";
                _logger.LogWarning("Duplicate diagnostic command rejected for {Component}: {Reason}", scope, reason);
                return 0;
            }

            var operationId = StartGlobalOperation();
            _scopeOperations[scope] = operationId;
            _busyScopes[scope] = busyReason;
            SetScopeBusy(scope, true, busyReason);
            _logger.LogInformation("Diagnostic operation started. Component={Component}, OperationId={OperationId}", scope, operationId);
            return operationId;
        }
    }

    private void EndScopedOperation(string scope, long operationId)
    {
        lock (_operationLock)
        {
            if (_scopeOperations.TryGetValue(scope, out var current) && current == operationId)
            {
                _busyScopes.TryRemove(scope, out _);
                SetScopeBusy(scope, false, "");
                _logger.LogInformation("Diagnostic operation ended. Component={Component}, OperationId={OperationId}", scope, operationId);
            }
        }
    }

    private bool IsLatestGlobalOperation(long operationId)
    {
        return operationId == Interlocked.Read(ref _latestStateOperationId);
    }

    private bool IsRetryCoalesced(string scope)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_operationLock)
        {
            if (_lastRetryByScope.TryGetValue(scope, out var last) &&
                now - last < TimeSpan.FromMilliseconds(RetryCoalesceMilliseconds))
            {
                return true;
            }

            _lastRetryByScope[scope] = now;
            return false;
        }
    }

    private void SetScopeBusy(string scope, bool isBusy, string reason)
    {
        foreach (var issue in Groups.SelectMany(g => g.Issues).Where(i => string.Equals(i.Component, scope, StringComparison.OrdinalIgnoreCase)))
            issue.SetBusy(isBusy, reason);
    }

    private void ApplyBusyState(DiagnosticIssueViewModel issue)
    {
        if (_busyScopes.TryGetValue(issue.Component, out var reason))
            issue.SetBusy(true, reason);
    }

    private void ScheduleLogRefresh()
    {
        if (Interlocked.Exchange(ref _logRefreshPending, 1) == 0)
            _logRefreshTimer.Change(LogRefreshInterval, Timeout.InfiniteTimeSpan);
    }

    private void OnLogRefreshTimer(object? state)
    {
        Interlocked.Exchange(ref _logRefreshPending, 0);
        _ = _dispatcher.InvokeAsync(RefreshLogs);
    }

    private void RefreshLogs()
    {
        var level = SelectedLogLevel switch
        {
            "Error" => LogEventLevel.Error,
            "Warning" => LogEventLevel.Warning,
            "Debug" => LogEventLevel.Debug,
            _ => LogEventLevel.Information
        };

        Logs.Clear();
        foreach (var entry in _liveLogBuffer.Query(level, LogSearchText))
            Logs.Add(new LiveLogEntryViewModel(entry));
    }

    private static string ResolveGroupName(string component)
    {
        return component switch
        {
            "LLM" or "Embeddings" or "Vector DB" => "Services principaux",
            "Configuration" => "Configuration",
            "Encryption" => "Securite",
            _ => "Systeme"
        };
    }

    private static string ResolveScope(DiagnosticIssueViewModel? issue)
    {
        return string.IsNullOrWhiteSpace(issue?.Component)
            ? "Systeme"
            : issue.Component;
    }
}

public sealed class DiagnosticGroupViewModel
{
    public DiagnosticGroupViewModel(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public ObservableCollection<DiagnosticIssueViewModel> Issues { get; } = [];
}

public partial class DiagnosticIssueViewModel : ObservableObject
{
    private int _draftVersion;

    [ObservableProperty]
    private string _editValue = "";

    [ObservableProperty]
    private string _secretValue = "";

    [ObservableProperty]
    private string _validationError = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunFix))]
    [NotifyPropertyChangedFor(nameof(CanRunRetry))]
    [NotifyPropertyChangedFor(nameof(IsEditEnabled))]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyReason = "";

    private DiagnosticIssueViewModel() { }

    public required string Component { get; init; }
    public required HealthStatus Status { get; init; }
    public required string Message { get; init; }
    public FixAction? FixAction { get; init; }
    public IReadOnlyDictionary<string, string>? Details { get; init; }

    public bool IsError => Status == HealthStatus.Error;
    public bool CanFix => FixAction is not null;
    public bool CanRunFix => CanFix && !IsBusy;
    public bool CanRunRetry => !IsBusy;
    public bool IsEditable => FixAction is Diagnostics.FixAction.EditLlmPath or Diagnostics.FixAction.EditEmbeddingPath or Diagnostics.FixAction.EditOllamaEndpoint;
    public bool IsEditEnabled => IsEditable && !IsBusy;
    public bool RequiresSecret => FixAction == Diagnostics.FixAction.EnableEncryption;
    public string StatusText => Status.ToString().ToUpperInvariant();
    public string StatusBrush => Status switch
    {
        HealthStatus.OK => "#2E7D32",
        HealthStatus.Warning => "#F57F17",
        HealthStatus.Error => "#C62828",
        _ => "#888888"
    };

    public static DiagnosticIssueViewModel FromResult(HealthCheckResult result)
    {
        var editValue = "";
        if (result.Details is not null)
        {
            result.Details.TryGetValue("ModelPath", out editValue);
            if (string.IsNullOrWhiteSpace(editValue))
                result.Details.TryGetValue("OllamaUrl", out editValue);
        }

        return new DiagnosticIssueViewModel
        {
            Component = result.Component,
            Status = result.Status,
            Message = result.Message,
            FixAction = result.FixAction,
            Details = result.Details,
            EditValue = editValue ?? ""
        };
    }

    public void SetBusy(bool isBusy, string reason)
    {
        IsBusy = isBusy;
        BusyReason = reason;
    }

    partial void OnEditValueChanged(string value)
    {
        _draftVersion++;
        ValidationError = "";
    }

    partial void OnSecretValueChanged(string value)
    {
        _draftVersion++;
        ValidationError = "";
    }
}

public sealed class LiveLogEntryViewModel
{
    public LiveLogEntryViewModel(LiveLogEntry entry)
    {
        Timestamp = entry.Timestamp.ToString("HH:mm:ss");
        Level = entry.Level.ToString().ToUpperInvariant();
        Component = entry.Component;
        Message = entry.Message;
        if (entry.RepeatCount > 1)
            Message = $"{Message} (x{entry.RepeatCount})";
        Exception = entry.Exception ?? "";
        LevelBrush = entry.Level switch
        {
            LogEventLevel.Error or LogEventLevel.Fatal => "#C62828",
            LogEventLevel.Warning => "#F57F17",
            _ => "#A3B6D1"
        };
    }

    public string Timestamp { get; }
    public string Level { get; }
    public string Component { get; }
    public string Message { get; }
    public string Exception { get; }
    public string LevelBrush { get; }
}
