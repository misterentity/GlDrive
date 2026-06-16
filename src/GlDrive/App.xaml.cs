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
    private GlDrive.Services.HeartbeatMonitor? _heartbeat;

    public static GlDrive.AiAgent.TelemetryRecorder? TelemetryRecorder { get; private set; }
    public static GlDrive.AiAgent.HealthRollup? HealthRollup { get; private set; }
    public static GlDrive.AiAgent.SectionActivityRollup? SectionActivityRollup { get; private set; }
    public static GlDrive.AiAgent.TelemetryRetention? TelemetryRetention { get; private set; }
    public static GlDrive.AiAgent.NukePoller? NukePoller { get; private set; }
    public static GlDrive.AiAgent.FreezeStore? FreezeStore { get; private set; }
    public static GlDrive.AiAgent.AuditTrail? AuditTrail { get; private set; }
    public static GlDrive.AiAgent.SnapshotStore? SnapshotStore { get; private set; }
    public static GlDrive.AiAgent.AgentMemo? AgentMemo { get; private set; }
    public static GlDrive.AiAgent.ChangeApplier? ChangeApplier { get; private set; }
    public static GlDrive.AiAgent.LogDigester? LogDigester { get; private set; }
    public static GlDrive.AiAgent.AgentRunner? AgentRunner { get; private set; }

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

        // Global exception handlers to prevent silent crashes.
        //
        // IMPORTANT: do NOT call Log.CloseAndFlush() here when args.Handled=true. The
        // process keeps running, but a disposed Serilog pipeline silently drops every
        // subsequent log entry — observed 2026-05-14 where an H.NotifyIcon glitch at
        // 09:05 killed logging while the app continued running until ~12:58 with zero
        // diagnostics. Use Log.Logger.ForContext(...) flushes if needed instead.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled UI exception");
            WriteCrashDump(args.Exception, "dispatcher");
            args.Handled = true; // Prevent crash for non-fatal UI exceptions
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            if (ex != null)
                Log.Fatal(ex, "Unhandled domain exception (terminating={Terminating})", args.IsTerminating);
            WriteCrashDump(ex, "appdomain");
            // CloseAndFlush is safe here — IsTerminating means the runtime is about to
            // tear the process down regardless.
            if (args.IsTerminating) Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // Background FTP exceptions we observe but don't action:
            // 1. FluentFTP's NoopDaemon races with client disposal and throws NRE from a
            //    finalizer-rethrown task.
            // 2. FluentFTP.GnuTLS surfaces GnuTlsException (e.g. -50 INVALID_REQUEST,
            //    -9 TLS_PACKET_DECODING_ERROR) from background reads after a BNC drops us.
            // Both are harmless (we SetObserved) but spam [ERR] logs. Suppress to Debug.
            // Type-check by name to avoid taking a using-reference on FluentFTP.GnuTLS.
            var inner = args.Exception?.Flatten().InnerExceptions
                ?? (IEnumerable<Exception>)Array.Empty<Exception>();
            bool suppress = false;
            foreach (var ex in inner)
            {
                // 3. Background socket reads (NOOP keepalive, SITE STATS / NewRelease
                //    polls) get their CancellationToken fired on timeout or teardown.
                //    The resulting OperationCanceledException is never observed because
                //    we deliberately fire-and-forget those probes — it's an expected
                //    cancellation, not an error. (~75/day of ERR noise pre-fix.)
                if (ex is OperationCanceledException)
                {
                    suppress = true;
                    break;
                }
                if (ex is NullReferenceException &&
                    ((ex.StackTrace?.Contains("BaseFtpClient.NoopDaemon", StringComparison.Ordinal) ?? false)
                     // Background GnuTLS read raced a connection teardown (socket
                     // close / m_customStream null in NeutralizeGnuTls) and
                     // dereferenced freed state. The NoopDaemon that produced
                     // these is now disabled (FtpClientFactory), but keep the
                     // suppression for any other path that reads a torn-down
                     // stream — it's a harmless use-after-free we've neutralized.
                     || (ex.StackTrace?.Contains("GnuTlsInternalStream.Read", StringComparison.Ordinal) ?? false)))
                {
                    suppress = true;
                    break;
                }
                if (ex.GetType().Name == "GnuTlsException")
                {
                    suppress = true;
                    break;
                }
            }
            if (suppress)
                Log.Debug(args.Exception, "Unobserved background-FTP exception (suppressed)");
            else
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
        var asmVersion = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString() ?? "unknown";
        Log.Information("GlDrive starting... version={Version}", asmVersion);

        // Verify the private-member reflection that prevents native GnuTLS crashes
        // still resolves against the loaded FluentFTP/GnuTLS assemblies. If a package
        // bump renamed internals, IsGnuTlsHealthy/NeutralizeGnuTls silently become
        // no-ops and the native crashes return — so fail LOUD here. Per decision the
        // app keeps running (degraded) rather than refusing to start.
        GlDrive.Ftp.GnuTlsReflectionGuard.VerifyOrFail(msg =>
        {
            try { MessageBox.Show(msg, "GlDrive — TLS crash protection degraded",
                MessageBoxButton.OK, MessageBoxImage.Warning); }
            catch { /* headless/early — log already has it */ }
        });

        // Heartbeat diagnostic: inspect the previous instance's last heartbeat BEFORE
        // starting a new one (the new monitor overwrites the file on first tick).
        // A recent heartbeat (<90s) at startup means the previous process was alive
        // until it died — instant native crash. A stale heartbeat means it hung first.
        var heartbeatCheck = GlDrive.Services.HeartbeatMonitor.CheckStaleHeartbeat();
        // TODO(v1.85.x): if stale, surface to user via tray balloon after tray init.
        TimeSpan? staleHeartbeatAge = null;
        if (heartbeatCheck.HadHeartbeat)
        {
            var age = heartbeatCheck.AgeAtStartup ?? TimeSpan.Zero;
            if (age > TimeSpan.FromSeconds(90))
            {
                Log.Warning("Previous instance heartbeat stale at startup — age={AgeSec}s, snapshot={Snapshot}",
                    (int)age.TotalSeconds, heartbeatCheck.RawJson);
                staleHeartbeatAge = age;
            }
            else
                Log.Information("Previous instance shut down cleanly — last heartbeat age={AgeSec}s",
                    (int)age.TotalSeconds);
        }
        else
        {
            Log.Information("No previous heartbeat file found (first run or clean shutdown)");
        }
        _heartbeat = new GlDrive.Services.HeartbeatMonitor();

        // Initialize AI agent telemetry recorder
        TelemetryRecorder = new GlDrive.AiAgent.TelemetryRecorder(ConfigManager.AppDataPath, config.Agent.TelemetryMaxFileMB);
        SerilogSetup.AgentSink.Recorder = TelemetryRecorder;
        FreezeStore = new GlDrive.AiAgent.FreezeStore(
            System.IO.Path.Combine(ConfigManager.AppDataPath, "ai-data"));
        AuditTrail = new GlDrive.AiAgent.AuditTrail(
            System.IO.Path.Combine(ConfigManager.AppDataPath, "ai-data"));
        SnapshotStore = new GlDrive.AiAgent.SnapshotStore(
            System.IO.Path.Combine(ConfigManager.AppDataPath, "ai-data"),
            config.Agent.SnapshotRetentionCount);
        AgentMemo = new GlDrive.AiAgent.AgentMemo(
            System.IO.Path.Combine(ConfigManager.AppDataPath, "ai-data"));

        var validators = new List<GlDrive.AiAgent.IChangeValidator>
        {
            new GlDrive.AiAgent.SkiplistValidator(),
            new GlDrive.AiAgent.PriorityValidator(),
            new GlDrive.AiAgent.SectionMappingValidator(),
            new GlDrive.AiAgent.AnnounceRuleValidator(),
            new GlDrive.AiAgent.ExcludedCategoriesValidator(),
            new GlDrive.AiAgent.WishlistPruneValidator(),
            new GlDrive.AiAgent.PoolSizingValidator(),
            new GlDrive.AiAgent.BlacklistValidator(),
            new GlDrive.AiAgent.AffilsValidator(),
            new GlDrive.AiAgent.ErrorReportValidator(
                System.IO.Path.Combine(ConfigManager.AppDataPath, "ai-data")),
            new GlDrive.AiAgent.DownloadOnlyValidator(),
            new GlDrive.AiAgent.RequestFillerValidator()
        };
        ChangeApplier = new GlDrive.AiAgent.ChangeApplier(validators, FreezeStore!, AuditTrail!);
        LogDigester = new GlDrive.AiAgent.LogDigester(
            System.IO.Path.Combine(ConfigManager.AppDataPath, "ai-data"));
        AgentRunner = new GlDrive.AiAgent.AgentRunner(
            LogDigester,
            AgentMemo,
            FreezeStore!,
            ChangeApplier,
            AuditTrail!,
            SnapshotStore!,
            ConfigManager.ConfigPath,
            cfg => ConfigManager.Save(cfg),
            () => ConfigManager.Load(),
            System.IO.Path.Combine(ConfigManager.AppDataPath, "ai-data"));
        AgentRunner.Start();

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

        // Re-add the cyberpunk ProgressBar stripe-scroll animation that v1.85.1
        // pulled from the template. The in-template EventTrigger crashed with a
        // namescope error ('StripeRect' not in scope) because the template
        // namescope isn't fully built when the EventTrigger fires. Wiring this
        // via EventManager.RegisterClassHandler runs the handler AFTER the
        // template applies, so FindName resolves cleanly. Themes without a
        // StripeRect element (Dark/Light) just no-op.
        try { WireProgressBarStripeAnimation(); }
        catch (Exception ex) { Log.Debug(ex, "WireProgressBarStripeAnimation failed"); }

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
        HealthRollup = new GlDrive.AiAgent.HealthRollup(TelemetryRecorder, _serverManager);
        SectionActivityRollup = new GlDrive.AiAgent.SectionActivityRollup(
            TelemetryRecorder,
            Path.Combine(ConfigManager.AppDataPath, "ai-data"));
        TelemetryRetention = new GlDrive.AiAgent.TelemetryRetention(
            Path.Combine(ConfigManager.AppDataPath, "ai-data"),
            config.Agent.GzipAfterDays,
            config.Agent.DeleteAfterDays);
        var nukeCursors = new GlDrive.AiAgent.NukeCursorStore(
            Path.Combine(ConfigManager.AppDataPath, "ai-data"));
        NukePoller = new GlDrive.AiAgent.NukePoller(
            TelemetryRecorder, _serverManager, nukeCursors,
            Path.Combine(ConfigManager.AppDataPath, "ai-data"),
            config.Agent.NukePollIntervalHours);

        // Init tray
        _trayViewModel = new TrayViewModel(_serverManager, config, notificationStore);
        _taskbarIcon = new H.NotifyIcon.TaskbarIcon();
        _taskbarIcon.DataContext = _trayViewModel;
        TrayIconSetup.Configure(_taskbarIcon, _trayViewModel);
        _taskbarIcon.ForceCreate(false);

        // One-shot tray balloon so the user knows last session likely crashed.
        // Best-effort: tray init may not have fully wired into the shell yet on
        // some boots; defer to Background dispatcher tick so the icon is live.
        if (staleHeartbeatAge is { } staleAge)
        {
            var ageSec = (int)staleAge.TotalSeconds;
            var icon = _taskbarIcon;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    icon?.ShowNotification(
                        "GlDrive recovered",
                        $"Previous session ended unexpectedly ({ageSec}s heartbeat gap). Logs in %AppData%\\GlDrive\\logs.",
                        H.NotifyIcon.Core.NotificationIcon.Warning);
                }
                catch (Exception ex) { Log.Debug(ex, "Heartbeat toast failed"); }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

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

    /// <summary>
    /// Re-adds the cyberpunk ProgressBar stripe-scroll animation that v1.85.1
    /// removed from the template. The in-template EventTrigger crashed with a
    /// 'StripeRect' namescope error because the template namescope isn't
    /// fully built when an inline FrameworkElement.LoadedEvent EventTrigger
    /// fires. RegisterClassHandler runs the handler AFTER the template
    /// applies, so FindName resolves cleanly. Themes whose template lacks
    /// StripeRect (Dark/Light) just no-op safely.
    /// </summary>
    private static void WireProgressBarStripeAnimation()
    {
        EventManager.RegisterClassHandler(
            typeof(System.Windows.Controls.ProgressBar),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                try
                {
                    if (sender is not System.Windows.Controls.ProgressBar pb) return;
                    pb.ApplyTemplate();
                    var stripe = pb.Template?.FindName("StripeRect", pb)
                                 as System.Windows.Shapes.Rectangle;
                    if (stripe?.Fill is not System.Windows.Media.DrawingBrush brush) return;
                    if (brush.Transform is not System.Windows.Media.TranslateTransform transform) return;
                    var anim = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = 0,
                        To = 16,
                        Duration = TimeSpan.FromSeconds(0.8),
                        RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                    };
                    transform.BeginAnimation(
                        System.Windows.Media.TranslateTransform.XProperty,
                        anim);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "ProgressBar stripe animation hook failed");
                }
            }),
            handledEventsToo: true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);

    /// <summary>
    /// Best-effort crash dump writer — captures the exception chain plus the last 100
    /// lines of today's log into a timestamped file under %AppData%\GlDrive\logs.
    /// Never throws — all I/O is swallowed.
    /// </summary>
    private static void WriteCrashDump(Exception? ex, string source)
    {
        try
        {
            var logsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GlDrive", "logs");
            try { Directory.CreateDirectory(logsDir); } catch { }

            var now = DateTime.Now;
            var dumpPath = Path.Combine(logsDir, $"crashdump-{now:yyyyMMdd-HHmmss}.log");
            var pid = Process.GetCurrentProcess().Id;

            using var sw = new StreamWriter(dumpPath, append: false);
            sw.WriteLine($"CRASH source={source} pid={pid} time={DateTime.UtcNow:o}");
            sw.WriteLine(ex?.ToString() ?? "<no exception object>");
            sw.WriteLine("--- recent log tail ---");

            var todayLogPath = Path.Combine(logsDir, $"gldrive-{now:yyyyMMdd}.log");
            try
            {
                if (File.Exists(todayLogPath))
                {
                    var lines = File.ReadAllLines(todayLogPath);
                    var tail = lines.Length > 100 ? lines[(lines.Length - 100)..] : lines;
                    foreach (var line in tail)
                        sw.WriteLine(line);
                }
                else
                {
                    sw.WriteLine($"<today's log not found at {todayLogPath}>");
                }
            }
            catch (Exception logEx)
            {
                sw.WriteLine($"<failed to read today's log: {logEx.Message}>");
            }
        }
        catch
        {
            // Best-effort — never let the crash dumper itself crash.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("GlDrive shutting down...");
        _heartbeat?.Dispose();
        _heartbeat = null;
        try { GlDrive.Logging.SerilogSetup.AgentSink.Flush(); }
        catch (Exception ex) { Log.Debug(ex, "AgentSink final flush failed"); }
        AgentRunner?.Dispose();
        AgentRunner = null;
        NukePoller?.Dispose();
        NukePoller = null;
        HealthRollup?.Dispose();
        HealthRollup = null;
        SectionActivityRollup?.Dispose();
        SectionActivityRollup = null;
        TelemetryRetention?.Dispose();
        TelemetryRetention = null;
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
