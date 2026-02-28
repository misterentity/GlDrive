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
    private MountService? _mountService;
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
        if (!ConfigManager.ConfigExists)
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
        var certManager = new CertificateManager(config.Tls.CertificateFingerprintFile);
        _mountService = new MountService(config, certManager);

        // Init tray
        _trayViewModel = new TrayViewModel(_mountService, config);
        _taskbarIcon = new H.NotifyIcon.TaskbarIcon();
        _taskbarIcon.DataContext = _trayViewModel;
        TrayIconSetup.Configure(_taskbarIcon, _trayViewModel);
        _taskbarIcon.ForceCreate(false);

        // Auto-mount
        if (config.Mount.AutoMountOnStart)
        {
            try
            {
                await _mountService.Mount();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Auto-mount failed");
                _trayViewModel.ShowNotification("GlDrive", $"Mount failed: {ex.Message}");
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("GlDrive shutting down...");
        _mountService?.Dispose();
        _taskbarIcon?.Dispose();
        _guard?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
