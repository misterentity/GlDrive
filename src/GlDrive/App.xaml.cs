using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Logging;
using GlDrive.Services;
using GlDrive.Tls;
using GlDrive.UI;
using Serilog;

namespace GlDrive;

public partial class App
{
    private SingleInstanceGuard? _guard;
    private ServerManager? _serverManager;
    private TrayViewModel? _trayViewModel;
    private H.NotifyIcon.TaskbarIcon? _taskbarIcon;

    public static GlDrive.AiAgent.TelemetryRecorder? TelemetryRecorder { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Update applicator mode — runs elevated, replaces files, relaunches, then exits.
        // Must force-kill the process after ApplyUpdate to prevent GnuTLS native DLL
        // teardown crash (DllNotFoundException in __scrt_uninitialize_type_info) when
        // running from the temp update directory.
        var applyIdx = Array.IndexOf(e.Args, "--apply-update");
        if (applyIdx >= 0 && e.Args.Length >= applyIdx + 4)
        {
            if (int.TryParse(e.Args[applyIdx + 1], out var pid))
            {
                UpdateChecker.ApplyUpdate(pid, e.Args[applyIdx + 2], e.Args[applyIdx + 3]);
                // Force-kill to skip native module teardown that crashes
                Process.GetCurrentProcess().Kill();
                return;
            }
        }

        // Register with Windows Application Restart Manager — if the process crashes
        // (including native GnuTLS crashes), Windows will automatically restart it.
        // This is the only reliable way to survive native crashes that bypass all
        // managed exception handlers.
        RegisterApplicationRestart(null, 0);

        // Crash recovery: if we detect an unclean shutdown (crash marker exists),
        // log the restart. The watchdog writes "CRASH:<timestamp>" when it restarts us.
        var crashMarker = Path.Combine(ConfigManager.AppDataPath, ".running");
        if (File.Exists(crashMarker))
        {
            try
            {
                var markerContent = File.ReadAllText(crashMarker).Trim();
                if (markerContent.StartsWith("CRASH:"))
                    Log.Warning("GlDrive: restarted by watchdog after crash at {CrashTime}", markerContent[6..]);
                else
                    Log.Warning("GlDrive: detected unclean shutdown (previous session did not exit cleanly)");
            }
            catch { Log.Warning("GlDrive: detected unclean shutdown"); }
        }
        try { File.WriteAllText(crashMarker, DateTime.UtcNow.ToString("O")); } catch { }

        // Clean up .old files and stale update marker from a previous update
        UpdateChecker.CleanupOldUpdateFiles();
        Irc.IrcLogStore.PruneOld();
        try { File.Delete(Path.Combine(ConfigManager.AppDataPath, ".updating")); } catch { }

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

        // Initialize AI agent telemetry recorder
        TelemetryRecorder = new GlDrive.AiAgent.TelemetryRecorder(ConfigManager.AppDataPath, config.Agent.TelemetryMaxFileMB);
        SerilogSetup.AgentSink.Recorder = TelemetryRecorder;

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

        // Init services — certificates auto-trusted on first connect (TOFU model)
        // Users can clear per-server certs in Settings > Servers > Edit > Clear Certificate
        var certManager = new CertificateManager();
        certManager.CertificatePrompt += (key, message) =>
        {
            var tcs = new TaskCompletionSource<bool>();
            Dispatcher.Invoke(() =>
            {
                var result = System.Windows.MessageBox.Show(
                    message,
                    "GlDrive — Certificate Changed",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning);
                tcs.SetResult(result == System.Windows.MessageBoxResult.Yes);
            });
            return tcs.Task;
        };
        var notificationStore = new NotificationStore();
        try { notificationStore.Load(); }
        catch (Exception ex) { Log.Warning(ex, "Failed to load notification store"); }
        _serverManager = new ServerManager(config, certManager, notificationStore);

        // Init tray
        _trayViewModel = new TrayViewModel(_serverManager, config, notificationStore);
        _taskbarIcon = new H.NotifyIcon.TaskbarIcon();
        _taskbarIcon.DataContext = _trayViewModel;
        TrayIconSetup.Configure(_taskbarIcon, _trayViewModel);
        _taskbarIcon.ForceCreate(false);

        // Auto-start extractor watch folders if enabled (hidden window)
        AutoStartExtractorWatch();

        // Auto-mount all enabled servers in background — don't block the UI
        _ = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await _serverManager.MountAll();
                Log.Information("All servers mounted in {Elapsed}ms", sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Auto-mount failed after {Elapsed}ms", sw.ElapsedMilliseconds);
                Dispatcher.BeginInvoke(() =>
                    _trayViewModel!.ShowNotification("GlDrive", $"Mount failed: {ex.Message}"));
            }
        });
    }

    /// <summary>
    /// If extractor watch folders are enabled in settings, open the ExtractorWindow
    /// hidden so the FileSystemWatchers start monitoring immediately on app launch.
    /// </summary>
    private void AutoStartExtractorWatch()
    {
        try
        {
            var settingsPath = Path.Combine(ConfigManager.AppDataPath, "extractor-settings.json");
            if (!File.Exists(settingsPath))
            {
                Log.Debug("AutoStartExtractorWatch: no settings file at {Path}", settingsPath);
                return;
            }

            var json = File.ReadAllText(settingsPath);
            ExtractorSettings? settings;
            try
            {
                settings = System.Text.Json.JsonSerializer.Deserialize<ExtractorSettings>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception jex)
            {
                Log.Warning(jex, "AutoStartExtractorWatch: failed to parse {Path}", settingsPath);
                return;
            }

            if (settings == null)
            {
                Log.Warning("AutoStartExtractorWatch: settings deserialized to null");
                return;
            }

            if (!settings.WatchEnabled || settings.WatchFolders == null || settings.WatchFolders.Count == 0)
            {
                Log.Debug(
                    "AutoStartExtractorWatch: disabled or empty (watchEnabled={We}, folders={Nf})",
                    settings.WatchEnabled, settings.WatchFolders?.Count ?? 0);
                return;
            }

            Log.Information(
                "Auto-starting extractor watch folders ({Count} folders)",
                settings.WatchFolders.Count);

            // Create the ExtractorWindow hidden. Its constructor calls LoadSettings(),
            // which starts the FileSystemWatchers when WatchEnabled is true. The window
            // stays alive (referenced by Application.Current.Windows) even after Hide()
            // because WPF tracks visible and hidden windows the same way.
            var win = new ExtractorWindow
            {
                ShowInTaskbar = false,
                WindowState = WindowState.Minimized
            };
            win.Show();
            win.Hide();
            Log.Information("AutoStartExtractorWatch: hidden ExtractorWindow created and watchers started");
        }
        catch (Exception ex)
        {
            // Warning (not Debug) so failures are visible at default log level
            Log.Warning(ex, "AutoStartExtractorWatch: unexpected failure");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("GlDrive shutting down...");
        TelemetryRecorder?.Dispose();
        TelemetryRecorder = null;
        _serverManager?.Dispose();
        _taskbarIcon?.Dispose();
        _guard?.Dispose();

        // Remove crash marker — clean exit
        try { File.Delete(Path.Combine(ConfigManager.AppDataPath, ".running")); } catch { }

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
