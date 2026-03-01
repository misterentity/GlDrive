using System.Windows;
using GlDrive.Config;
using GlDrive.Logging;
using GlDrive.Services;
using GlDrive.Tls;
using GlDrive.UI;
using Serilog;

namespace GlDrive;

public partial class App : Application
{
    private SingleInstanceGuard? _guard;
    private ServerManager? _serverManager;
    private TrayViewModel? _trayViewModel;
    private H.NotifyIcon.TaskbarIcon? _taskbarIcon;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance check
        _guard = new SingleInstanceGuard();
        if (!_guard.TryAcquire())
        {
            MessageBox.Show("GlDrive is already running.", "GlDrive",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Load config
        var config = ConfigManager.Load();

        // Init logging
        SerilogSetup.Configure(config.Logging);
        Log.Information("GlDrive starting...");

        // Check first run
        if (!ConfigManager.ConfigExists || config.Servers.Count == 0)
        {
            Log.Information("First run detected, showing wizard");
            var wizard = new WizardWindow();
            var result = wizard.ShowDialog();
            if (result != true)
            {
                Log.Information("Wizard cancelled, shutting down");
                Shutdown();
                return;
            }
            config = ConfigManager.Load(); // Reload after wizard saves
        }

        // Init services
        var certManager = new CertificateManager();
        _serverManager = new ServerManager(config, certManager);

        // Init tray
        _trayViewModel = new TrayViewModel(_serverManager, config);
        _taskbarIcon = new H.NotifyIcon.TaskbarIcon();
        _taskbarIcon.DataContext = _trayViewModel;
        TrayIconSetup.Configure(_taskbarIcon, _trayViewModel);
        _taskbarIcon.ForceCreate(false);

        // Auto-mount all enabled servers
        try
        {
            await _serverManager.MountAll();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Auto-mount failed");
            _trayViewModel.ShowNotification("GlDrive", $"Mount failed: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("GlDrive shutting down...");
        _serverManager?.Dispose();
        _taskbarIcon?.Dispose();
        _guard?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
