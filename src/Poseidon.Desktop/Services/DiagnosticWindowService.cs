using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Poseidon.Desktop.Views;

namespace Poseidon.Desktop.Services;

public sealed class DiagnosticWindowService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DiagnosticWindowService> _logger;
    private StartupDiagnosticWindow? _window;

    public DiagnosticWindowService(
        IServiceProvider services,
        ILogger<DiagnosticWindowService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public void ShowOrActivate()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        dispatcher.Invoke(() =>
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app is null)
                    return;

                if (_window is null)
                {
                    _window = _services.GetRequiredService<StartupDiagnosticWindow>();
                    _window.Owner = app.MainWindow;
                    _window.Closed += (_, _) => _window = null;
                }

                if (!_window.IsVisible)
                    _window.Show();

                if (_window.WindowState == WindowState.Minimized)
                    _window.WindowState = WindowState.Normal;

                _window.Activate();
                _window.Focus();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open diagnostic window");
            }
        });
    }
}
