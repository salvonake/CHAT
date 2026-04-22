using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LegalAI.Desktop.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Forms = System.Windows.Forms;

namespace LegalAI.Desktop;

/// <summary>
/// Main window shell. DataContext is set to MainViewModel by App.xaml.cs.
/// </summary>
public partial class MainWindow : Window
{
    private readonly DataPaths _paths;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MainWindow> _logger;

    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ToolStripMenuItem? _statusMenuItem;
    private MainViewModel? _mainViewModel;

    private readonly bool _minimizeToTrayOnClose;
    private readonly bool _minimizeToTrayOnMinimize;
    private readonly bool _showBackgroundNotifications;

    private readonly string _backgroundTipFlagPath;
    private bool _backgroundTipShown;
    private bool _isExitRequested;

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool FlashWindow(nint hWnd, bool bInvert);

    public MainWindow(DataPaths paths, IConfiguration configuration, ILogger<MainWindow> logger)
    {
        _paths = paths;
        _configuration = configuration;
        _logger = logger;

        _minimizeToTrayOnClose = _configuration.GetValue("Ui:MinimizeToTrayOnClose", true);
        _minimizeToTrayOnMinimize = _configuration.GetValue("Ui:MinimizeToTrayOnMinimize", true);
        _showBackgroundNotifications = _configuration.GetValue("Ui:ShowBackgroundNotifications", true);

        _backgroundTipFlagPath = Path.Combine(_paths.DataDirectory, ".background-tip-shown");
        _backgroundTipShown = _configuration.GetValue("Ui:BackgroundTipShown", false) || File.Exists(_backgroundTipFlagPath);

        InitializeComponent();

        InitializeTrayIcon();

        StateChanged += OnMainWindowStateChanged;
        Closing += OnMainWindowClosing;
        Closed += OnMainWindowClosed;
        DataContextChanged += OnMainWindowDataContextChanged;
    }

    public bool RestoreAndActivateWindow()
    {
        try
        {
            if (_isExitRequested)
                return false;

            if (!IsVisible)
            {
                Show();
            }

            ShowInTaskbar = true;

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            EnsureWindowOnScreen();

            Activate();
            Focus();

            var handle = new WindowInteropHelper(this).Handle;
            if (handle == nint.Zero)
                return false;

            ShowWindowAsync(handle, SwRestore);

            var focused = SetForegroundWindow(handle);
            if (!focused)
            {
                // Fallback for foreground restrictions.
                Topmost = true;
                Topmost = false;
                Activate();
                focused = GetForegroundWindow() == handle;
            }

            if (!focused)
            {
                FlashWindow(handle, true);
                ShowActivationFallbackNotification();
            }

            UpdateStatusMenuText();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore and activate main window");
            ShowActivationFallbackNotification();
            return false;
        }
    }

    public void HandleExternalActivationRequest()
    {
        var wasHiddenOrMinimized = !IsVisible || WindowState == WindowState.Minimized || !ShowInTaskbar;
        RestoreAndActivateWindow();

        if (wasHiddenOrMinimized && _showBackgroundNotifications)
        {
            ShowTrayBalloon(
                "LegalAI is already running",
                "The existing window has been restored and focused.",
                Forms.ToolTipIcon.Info);
        }
    }

    private void OnMainWindowStateChanged(object? sender, EventArgs e)
    {
        if (_isExitRequested || !_minimizeToTrayOnMinimize)
            return;

        if (WindowState == WindowState.Minimized)
        {
            HideToTray();
        }
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isExitRequested || !_minimizeToTrayOnClose)
            return;

        e.Cancel = true;
        HideToTray();
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
            _mainViewModel = null;
        }

        DisposeTrayIcon();
    }

    private void OnMainWindowDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
        }

        _mainViewModel = e.NewValue as MainViewModel;
        if (_mainViewModel is not null)
        {
            _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
        }

        UpdateStatusMenuText();
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.StatusText) or nameof(MainViewModel.SystemStatusText))
        {
            Dispatcher.BeginInvoke(UpdateStatusMenuText);
        }
    }

    private void InitializeTrayIcon()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "LegalAI Desktop",
            Icon = LoadTrayIcon(),
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => RestoreAndActivateWindow();

        var contextMenu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("Open LegalAI", null, (_, _) => RestoreAndActivateWindow());
        _statusMenuItem = new Forms.ToolStripMenuItem("Status: Starting")
        {
            Enabled = false
        };
        var exitItem = new Forms.ToolStripMenuItem("Exit", null, (_, _) => ExitApplication());

        contextMenu.Items.Add(openItem);
        contextMenu.Items.Add(_statusMenuItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;
        UpdateStatusMenuText();
    }

    private Icon LoadTrayIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "LegalAI.ico");
            if (File.Exists(iconPath))
            {
                return new Icon(iconPath);
            }

            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load tray icon from application resources");
        }

        return SystemIcons.Application;
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;

        if (!_backgroundTipShown && _showBackgroundNotifications)
        {
            ShowTrayBalloon(
                "LegalAI is running in the background",
                "Use the tray icon near the clock to reopen or exit the application.",
                Forms.ToolTipIcon.Info);

            _backgroundTipShown = true;
            PersistBackgroundTipShown();
        }

        UpdateStatusMenuText();
    }

    private void PersistBackgroundTipShown()
    {
        try
        {
            File.WriteAllText(_backgroundTipFlagPath, "shown");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist tray background tip flag");
        }
    }

    private void UpdateStatusMenuText()
    {
        if (_statusMenuItem is null)
            return;

        var status = _mainViewModel?.StatusText;
        if (string.IsNullOrWhiteSpace(status))
        {
            status = "Running";
        }

        _statusMenuItem.Text = $"Status: {status}";
    }

    private void ExitApplication()
    {
        _isExitRequested = true;
        DisposeTrayIcon();
        System.Windows.Application.Current.Shutdown();
    }

    private void ShowActivationFallbackNotification()
    {
        if (!_showBackgroundNotifications)
            return;

        ShowTrayBalloon(
            "LegalAI is running",
            "We could not bring the window to the foreground. Open it from the tray icon.",
            Forms.ToolTipIcon.Warning);
    }

    private void ShowTrayBalloon(string title, string text, Forms.ToolTipIcon icon)
    {
        try
        {
            _notifyIcon?.ShowBalloonTip(3500, title, text, icon);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to show tray balloon notification");
        }
    }

    private void EnsureWindowOnScreen()
    {
        var width = Math.Max(400, RestoreBounds.Width > 0 ? RestoreBounds.Width : Width);
        var height = Math.Max(300, RestoreBounds.Height > 0 ? RestoreBounds.Height : Height);
        var left = double.IsNaN(Left) ? RestoreBounds.Left : Left;
        var top = double.IsNaN(Top) ? RestoreBounds.Top : Top;

        var intersectsAnyScreen = Forms.Screen.AllScreens.Any(screen =>
        {
            var area = screen.WorkingArea;
            return left < area.Right
                && left + width > area.Left
                && top < area.Bottom
                && top + height > area.Top;
        });

        if (intersectsAnyScreen)
            return;

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + Math.Max(0, (workArea.Width - width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - height) / 2);
    }

    private void DisposeTrayIcon()
    {
        if (_notifyIcon is null)
            return;

        try
        {
            if (_notifyIcon.ContextMenuStrip is not null)
            {
                _notifyIcon.ContextMenuStrip.Dispose();
            }

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to dispose tray icon cleanly");
        }
        finally
        {
            _notifyIcon = null;
            _statusMenuItem = null;
        }
    }
}