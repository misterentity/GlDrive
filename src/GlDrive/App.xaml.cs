using System.Windows;
using GlDrive.Config;
using GlDrive.Downloads;
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

        // Update applicator mode — runs elevated, replaces files, relaunches, then exits
        var applyIdx = Array.IndexOf(e.Args, "--apply-update");
        if (applyIdx >= 0 && e.Args.Length >= applyIdx + 4)
        {
            if (int.TryParse(e.Args[applyIdx + 1], out var pid))
            {
                UpdateChecker.ApplyUpdate(pid, e.Args[applyIdx + 2], e.Args[applyIdx + 3]);
                // ApplyUpdate calls Environment.Exit, but just in case:
                Shutdown();
                return;
            }
        }

        // Clean up .old files from a previous update
        UpdateChecker.CleanupOldUpdateFiles();

        // Screenshot mode — capture all UI windows to PNGs and exit
        if (e.Args.Contains("--screenshots", StringComparer.OrdinalIgnoreCase))
        {
            ScreenshotCapture.CaptureAll(ConfigManager.Load());
            Shutdown();
            return;
        }

        // Global exception handlers to prevent silent crashes
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled UI exception");
            Log.CloseAndFlush();
            args.Handled = true; // Prevent crash for non-fatal UI exceptions
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Fatal(ex, "Unhandled domain exception (terminating={Terminating})", args.IsTerminating);
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved(); // Prevent process termination
        };

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

        // Apply theme
        ThemeManager.ApplyTheme(config.Downloads.Theme);

        // Init services
        var certManager = new CertificateManager();
        certManager.CertificatePrompt += (hostInfo, fingerprint) =>
        {
            var tcs = new TaskCompletionSource<bool>();
            Dispatcher.Invoke(() =>
            {
                var msg = fingerprint.Contains('\n')
                    ? $"Certificate has changed for {hostInfo}:\n\n{fingerprint}\n\nAccept the new certificate?"
                    : $"New TLS certificate for {hostInfo}:\n\nFingerprint: {fingerprint}\n\nTrust this certificate?";
                var result = System.Windows.MessageBox.Show(msg, "GlDrive — Certificate Verification",
                    System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                tcs.SetResult(result == System.Windows.MessageBoxResult.Yes);
            });
            return tcs.Task;
        };
        var notificationStore = new NotificationStore();
        notificationStore.Load();
        _serverManager = new ServerManager(config, certManager, notificationStore);

        // Init tray
        _trayViewModel = new TrayViewModel(_serverManager, config, notificationStore);
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
