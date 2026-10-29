using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using GlDrive.Logging;
using GlDrive.Services;
using GlDrive.Services.Recovery;

namespace GlDrive;

public static class Program
{
    internal static CrashRecoveryStartup? RecoveryStartup { get; private set; }
    internal static PreviousSessionDiagnostics? PreviousSession { get; private set; }
    internal static bool OwnsCrashMarker { get; private set; }
    internal static string SessionId => RecoveryStartup?.SessionId ?? FallbackSessionId;
    private static readonly string FallbackSessionId = Guid.NewGuid().ToString("N");
    internal static string CrashRestartArguments(bool recovery) => RecoveryStartup?.NextCrash.Arguments(recovery) ??
        new CrashRestartOrigin(FallbackSessionId, FallbackSessionId, false).Arguments(recovery);

    [STAThread]
    public static int Main(string[] args)
    {
        // A requested restart waits for the current app to release its single-instance
        // mutex before constructing WPF or starting a watchdog. Never terminate the old app.
        if (args.Length >= 2 && args[0] == "--restart-after" && int.TryParse(args[1], out var restartPid))
        {
            try
            {
                using var previous = Process.GetProcessById(restartPid);
                if (!previous.WaitForExit(60000)) return 1;
            }
            catch (ArgumentException) { /* Previous process already exited. */ }
        }
        // Updater modes must run before constructing the WPF Application. The scheduled task
        // runs as SYSTEM in a non-interactive session, where WPF startup can terminate before
        // App.OnStartup is reached. Dispatching here keeps the updater headless and makes the
        // fixed-action SYSTEM task usable without a logged-in desktop.
        var cleanupIdx = Array.FindIndex(args,
            arg => arg.Equals("--cleanup-old-updates", StringComparison.OrdinalIgnoreCase));
        if (cleanupIdx >= 0)
            return UpdateChecker.CleanupOldUpdateFilesFromTask();

        var rollbackIdx = Array.IndexOf(args, "--rollback-update");
        if (rollbackIdx >= 0)
            return args.Length == rollbackIdx + 3 && int.TryParse(args[rollbackIdx + 1], out var rollbackPid)
                ? UpdateChecker.RollbackFromLocalRequest(rollbackPid, args[rollbackIdx + 2]) : 1;

        var taskIdx = Array.FindIndex(args,
            arg => arg.Equals("--apply-update-task", StringComparison.OrdinalIgnoreCase));
        if (taskIdx >= 0)
        {
            // Rejected handoffs have not replaced any files and can return normally.
            // A dispatched install terminates itself with its actual result.
            return UpdateChecker.ApplyUpdateFromTask();
        }

        var applyIdx = Array.FindIndex(args,
            arg => arg.Equals("--apply-update", StringComparison.OrdinalIgnoreCase));
        if (applyIdx >= 0 && args.Length >= applyIdx + 4 &&
            int.TryParse(args[applyIdx + 1], out var updatePid))
        {
            UpdateChecker.ApplyUpdate(updatePid, args[applyIdx + 2], args[applyIdx + 3]);
            return 1; // ApplyUpdate does not return after success or rollback.
        }

        // Watchdog mode — lightweight process monitor, no WPF.
        // Launched by the main app as: GlDrive.exe --watchdog <pid>
        var wdIdx = Array.IndexOf(args, "--watchdog");
        if (wdIdx >= 0 && wdIdx + 1 < args.Length && int.TryParse(args[wdIdx + 1], out var targetPid))
        {
            return RunWatchdog(targetPid, args.Contains("--recovery"), args);
        }

        // Normal mode — hold the single-instance mutex BEFORE spawning the watchdog or
        // constructing the WPF app. A second launch used to get all the way into
        // App.OnStartup, where it wrote and claimed the shared .running crash marker (and
        // deleted .updating) before the guard rejected it; its OnExit then deleted the
        // primary's marker, so the primary's watchdog treated the primary's next death as a
        // clean exit and never restarted it. It had also spawned a watchdog of its own.
        // Rejecting here means a blocked launch never touches production state at all.
        // Screenshot mode is an isolated renderer and deliberately bypasses the guard.
        var isScreenshots = Array.IndexOf(args, "--screenshots") >= 0;
        SingleInstanceGuard? guard = null;
        if (!isScreenshots)
        {
            guard = new SingleInstanceGuard();
            if (!guard.TryAcquire())
            {
                guard.Dispose();
                // Serilog is not initialised this early — append directly, like the watchdog.
                AppendDirectLogLine("WRN",
                    $"Second GlDrive instance (PID {Environment.ProcessId}) blocked — an instance is " +
                    "already running; exiting without touching its markers or spawning a watchdog.");
                if (CrashRestartOrigin.Parse(args) == null)
                    System.Windows.MessageBox.Show("GlDrive is already running.", "GlDrive",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return 0;
            }
            // Only the primary counts crash evidence. An ordinary/manual/update launch
            // gets a new chain so stale history can never make its first crash a loop.
            RecoveryStartup = CrashRecoveryStartup.Prepare(args, GlDrive.Util.AppDataPaths.Root,
                DateTimeOffset.UtcNow, warning => AppendDirectLogLine("WRN", warning));
            // Claim before WPF construction: native initialization can fail before OnStartup.
            // Preserve the old evidence for App's diagnostics while the primary owns the mutex.
            var crashMarker = Path.Combine(GlDrive.Util.AppDataPaths.Root, ".running");
            PreviousSession = PreviousSessionDiagnostics.Capture(crashMarker);
            try
            {
                Directory.CreateDirectory(GlDrive.Util.AppDataPaths.Root);
                File.WriteAllText(crashMarker, ExitMarker.Running(DateTime.UtcNow, SessionId));
                OwnsCrashMarker = true;
            }
            catch { }
            SpawnWatchdog(args.Contains("--recovery"));
        }

        App.PreAcquiredGuard = guard;
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static void AppendDirectLogLine(string level, string message)
    {
        try
        {
            var logDir = Path.Combine(
                GlDrive.Util.AppDataPaths.Root, "logs");
            if (!Directory.Exists(logDir)) return;
            ActiveLogFile.AppendLine(logDir, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}");
        }
        catch { }
    }

    /// <summary>Set by SpawnWatchdog before logging exists; App logs it once Serilog is up.</summary>
    internal static JobBreakaway.LogNote? WatchdogJobNote { get; private set; }

    /// <summary>
    /// Spawns a background copy of ourselves in watchdog mode to monitor our PID.
    /// The watchdog is a hidden process that restarts GlDrive if it crashes.
    /// </summary>
    private static void SpawnWatchdog(bool recovery)
    {
        try
        {
            if (RecoveryStartup?.Decision.RestartDelaySeconds == null && RecoveryStartup != null) return;
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var watchdogArguments = $"--watchdog {Environment.ProcessId} {CrashRestartArguments(recovery)} --watchdog-delay {RecoveryStartup?.Decision.RestartDelaySeconds ?? 3}";

            // The watchdog must not share a job object with us: terminating the launcher's
            // job would kill both at once and nothing would restart the app (see JobBreakaway).
            var jobState = JobBreakaway.CurrentState();
            string? breakawayError = null;
            if (JobBreakaway.ShouldAttemptBreakaway(jobState))
            {
                try
                {
                    JobBreakaway.StartBrokenAway(exe, watchdogArguments);
                    WatchdogJobNote = JobBreakaway.DescribeForLog(jobState, brokeAway: true, null);
                    return;
                }
                catch (Exception ex)
                {
                    breakawayError = ex.Message;
                }
            }
            WatchdogJobNote = JobBreakaway.DescribeForLog(jobState, brokeAway: false, breakawayError);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = watchdogArguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }
        catch
        {
            // Non-critical — app works fine without watchdog
        }
    }

    /// <summary>
    /// Watchdog loop: wait for the target process to exit, then restart if it was a crash.
    /// A clean exit deletes the .running marker file; if it still exists, it was a crash.
    /// </summary>
    private static int RunWatchdog(int targetPid, bool recovery, string[] args)
    {
        // Hide the console window if one was allocated
        var hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero)
            ShowWindow(hwnd, 0); // SW_HIDE

        try
        {
            using var proc = Process.GetProcessById(targetPid);
            proc.WaitForExit();
        }
        catch (ArgumentException)
        {
            // Process already exited before we could attach
        }
        catch
        {
            return 0;
        }

        // Give the OS a moment to release the mutex and flush file handles
        Thread.Sleep(3000);

        // Compute AppData path directly — do NOT use ConfigManager here.
        // The watchdog may run from a temp update directory that doesn't have
        // System.Text.Json.dll, so touching ConfigManager (which deserializes
        // JSON in its static constructor) would crash with FileNotFoundException.
        var appData = GlDrive.Util.AppDataPaths.Root;
        var crashMarker = Path.Combine(appData, ".running");
        var updateMarker = Path.Combine(appData, ".updating");

        // If a valid HMAC-authenticated update marker exists, the updater handles restarting.
        // Plain markers (no HMAC, expired, or tampered) are treated as missing — not honored.
        //
        // GUARD: UpdateMarkerHmac.IsValid lazily loads System.Security.Cryptography (HMACSHA256 /
        // CryptographicOperations). If THIS watchdog ever runs from a partial directory missing
        // that DLL, the load fails with FileNotFoundException at JIT/resolution time — at the call
        // site here, NOT inside IsValid (so IsValid's own try/catch can't help), and an unguarded
        // call crashes the watchdog (the WER crash-loops seen during 3.x update installs). A failure
        // to even validate means we're in a partial/update context → bow out, let the updater drive.
        bool updateInProgress;
        try { updateInProgress = UpdateMarkerHmac.IsValid(updateMarker); }
        catch { updateInProgress = true; }
        if (updateInProgress)
        {
            // The elevated updater may run as SYSTEM in session 0, which cannot reliably launch
            // the desktop app back into this user's session. Wait for its explicit completion
            // signal, then relaunch from this original interactive-session watchdog.
            var completionPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "GlDrive", $"update-complete-{targetPid}.signal");
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
            while (!File.Exists(completionPath) && DateTime.UtcNow < deadline)
                Thread.Sleep(500);

            var completed = File.Exists(completionPath);
            try { File.Delete(completionPath); } catch { }
            try { File.Delete(updateMarker); } catch { }

            try
            {
                var exe = Environment.ProcessPath;
                var started = string.IsNullOrWhiteSpace(exe) ? null : Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false
                });
                AppendWatchdogEvent(appData, completed ? "INF" : "WRN",
                    completed
                        ? $"Update completed; relaunched GlDrive as PID {started?.Id}."
                        : $"Update completion timed out; attempted recovery relaunch as PID {started?.Id}.");
            }
            catch (Exception ex)
            {
                AppendWatchdogEvent(appData, "ERR", $"Post-update relaunch failed: {ex.Message}");
            }
            return 0;
        }
        // Stale/invalid/plain marker — delete it and fall through to crash-restart logic
        if (File.Exists(updateMarker))
        {
            try { File.Delete(updateMarker); } catch { }
        }

        string? markerContent = null;
        var markerExists = File.Exists(crashMarker);
        if (markerExists)
        {
            try { markerContent = File.ReadAllText(crashMarker); } catch { }
        }
        var origin = CrashRestartOrigin.Parse(args);
        // WER may have won the restart race during the established three-second wait.
        // A positive different-session marker belongs to the replacement primary.
        if (ExitMarker.BelongsToDifferentSession(markerContent, origin?.SessionId) && AnotherPrimaryIsRunning()) return 0;
        var verdict = ExitMarker.ClassifyForWatchdog(markerExists, markerContent);
        if (verdict == WatchdogExitVerdict.Clean)
        {
            // Clean exit — marker was deleted by OnExit. Nothing to do.
            return 0;
        }
        if (verdict == WatchdogExitVerdict.IntendedExit)
        {
            // The app had begun an exit it chose (tray Exit, Windows restart/logoff) and died
            // before teardown finished. Restarting would undo the user's exit or launch into a
            // shutting-down OS. Leave the marker for startup diagnostics: if the OS kills this
            // watchdog too, the next session still reads the same evidence.
            ExitMarker.TryParseExiting(markerContent, out var since, out var why);
            string crash;
            try { crash = GetCrashReason(targetPid); } catch { crash = UnknownCrashReason; }
            AppendWatchdogEvent(appData, crash != UnknownCrashReason ? "WRN" : "INF",
                $"Process {targetPid} ended during its own shutdown ({why}, began {since}) before teardown " +
                "finished" + (crash != UnknownCrashReason ? $"; teardown crashed — {crash}" : "") +
                ". Not restarting.");
            return 0;
        }

        // The process exited without deleting its .running marker. That is USUALLY a
        // crash — but not always, and the watchdog has no business asserting a cause it
        // can't see. On 2026-08-05 07:39 this logged "[FTL] ... crashed — unknown (no
        // matching event log entry found)" 41 seconds before Kernel-Power logged "The
        // system is entering sleep", with no 1026/1000 event anywhere. Report the
        // evidence (unclean exit) and let the reason stand on its own.
        var logDir = Path.Combine(appData, "logs");

        void AppendLog(string level, string message)
        {
            try
            {
                if (!Directory.Exists(logDir)) return;
                ActiveLogFile.AppendLine(logDir,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] WATCHDOG: {message}");
            }
            catch { /* logging must never take the watchdog down */ }
        }

        try
        {
            var crashReason = GetCrashReason(targetPid);
            var identified = crashReason != UnknownCrashReason;
            // GetCrashReason may be slow. Recheck and claim the primary mutex before
            // changing shared evidence, so a WER replacement cannot claim it between
            // our liveness check and the write. Do not hold this probe during backoff.
            using var markerGuard = new SingleInstanceGuard(retryDelay: TimeSpan.Zero);
            if (!markerGuard.TryAcquire()) return 0;
            string? latest = null;
            var latestExists = File.Exists(crashMarker);
            if (latestExists) try { latest = File.ReadAllText(crashMarker); } catch { }
            if (ExitMarker.ClassifyForWatchdog(latestExists, latest) != WatchdogExitVerdict.Unclean) return 0;
            File.WriteAllText(crashMarker, $"CRASH:{DateTime.UtcNow:O}" + (origin == null ? "" : $"|session:{origin.SessionId}"));

            AppendLog(identified ? "FTL" : "WRN",
                identified
                    ? $"Process {targetPid} crashed — {crashReason}"
                    : $"Process {targetPid} exited without a clean-exit marker; no crash event found in the " +
                      "Windows event log. Cause unidentified — an OS sleep/shutdown or an external kill looks " +
                      "the same as a crash from here. Restarting.");
        }
        catch (Exception ex)
        {
            AppendLog("WRN", $"Failed to classify exit of process {targetPid}: {ex.GetType().Name}: {ex.Message}");
        }

        // Restart is a separate try block: a failure to *classify* the exit above must
        // never skip the restart, and a failure to restart must never be silent. The
        // old code wrapped both in one bare catch{}, so a restart that never happened
        // was indistinguishable from one that did.
        try
        {
            // Extra backoff is carried only by an already-safe-mode process. Normal
            // single-crash recovery has exactly the original three-second wait above.
            var delayIndex = Array.IndexOf(args, "--watchdog-delay");
            if (origin?.WasSafeMode == true && delayIndex >= 0 && delayIndex + 1 < args.Length &&
                int.TryParse(args[delayIndex + 1], out var delaySeconds) && delaySeconds > 3)
            {
                Thread.Sleep(TimeSpan.FromSeconds(Math.Clamp(delaySeconds, 3, 3600) - 3));
                string? current = null;
                var exists = File.Exists(crashMarker);
                if (exists) try { current = File.ReadAllText(crashMarker); } catch { }
                if (ExitMarker.BelongsToDifferentSession(current, origin.SessionId) && AnotherPrimaryIsRunning() ||
                    ExitMarker.ClassifyForWatchdog(exists, current) != WatchdogExitVerdict.Unclean) return 0;
            }
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                AppendLog("ERR", "Cannot restart — Environment.ProcessPath is empty.");
                return 0;
            }

            if (AnotherPrimaryIsRunning()) return 0;

            var started = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = origin?.Arguments(recovery) ?? (recovery ? "--recovery" : ""),
                UseShellExecute = false
            });

            if (started == null)
                AppendLog("ERR", $"Restart of {exe} returned no process handle — GlDrive may stay down.");
            else
                AppendLog("INF", $"Restarted GlDrive as PID {started.Id}.");
        }
        catch (Exception ex)
        {
            AppendLog("ERR", $"Restart failed — GlDrive stays down until launched manually. " +
                             $"{ex.GetType().Name}: {ex.Message}");
        }

        return 0;
    }

    private static void AppendWatchdogEvent(string appData, string level, string message)
    {
        try
        {
            var logDir = Path.Combine(appData, "logs");
            if (!Directory.Exists(logDir)) return;
            ActiveLogFile.AppendLine(logDir,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] WATCHDOG: {message}");
        }
        catch { }
    }

    private static bool AnotherPrimaryIsRunning()
    {
        try
        {
            // A different historical marker alone is not a live replacement: the target
            // may have crashed during WPF construction before claiming its own marker.
            using var probe = new SingleInstanceGuard(retryDelay: TimeSpan.Zero);
            return !probe.TryAcquire();
        }
        catch { return false; } // Unknown bookkeeping/liveness cannot suppress recovery.
    }

    internal const string UnknownCrashReason = "unknown (no matching event log entry found)";

    /// <summary>
    /// Query Windows Event Log for the crash reason of the given process.
    /// Checks both .NET Runtime (1026) and Application Error (1000) events.
    /// </summary>
    private static string GetCrashReason(int pid)
    {
        try
        {
            using var log = new System.Diagnostics.Eventing.Reader.EventLogReader(
                new System.Diagnostics.Eventing.Reader.EventLogQuery(
                    "Application", System.Diagnostics.Eventing.Reader.PathType.LogName,
                    $"*[System[(EventID=1026 or EventID=1000) and TimeCreated[timediff(@SystemTime) <= 30000]]]"));

            // Read recent events (last 30 seconds), find ones matching our process
            var reasons = new List<string>();
            while (log.ReadEvent() is { } evt)
            {
                var msg = evt.FormatDescription() ?? "";
                // .NET Runtime (1026) includes the exception, Application Error (1000) includes the faulting module
                if (msg.Contains("GlDrive", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract the useful part
                    if (evt.Id == 1026)
                    {
                        // .NET exception — find the Exception Info line
                        var exIdx = msg.IndexOf("Exception Info:", StringComparison.OrdinalIgnoreCase);
                        if (exIdx >= 0)
                        {
                            var exLine = msg[exIdx..];
                            // Take first 2 lines of the exception
                            var lines = exLine.Split('\n', 3);
                            reasons.Add(string.Join(" | ", lines.Take(2)).Trim());
                            continue;
                        }
                        // Fallback: find the Description line
                        var descIdx = msg.IndexOf("Description:", StringComparison.OrdinalIgnoreCase);
                        if (descIdx >= 0)
                        {
                            var descEnd = msg.IndexOf('\n', descIdx + 50);
                            reasons.Add(descEnd > 0 ? msg[descIdx..descEnd].Trim() : msg[descIdx..].Trim());
                            continue;
                        }
                    }
                    else if (evt.Id == 1000)
                    {
                        // Application Error — extract exception code and faulting module
                        var codeMatch = System.Text.RegularExpressions.Regex.Match(msg,
                            @"Exception code:\s*(0x[0-9a-fA-F]+)");
                        var modMatch = System.Text.RegularExpressions.Regex.Match(msg,
                            @"Faulting module name:\s*(\S+)");
                        if (codeMatch.Success)
                        {
                            var mod = modMatch.Success ? modMatch.Groups[1].Value : "unknown";
                            reasons.Add($"Exception {codeMatch.Groups[1].Value} in {mod}");
                        }
                    }
                }
            }

            if (reasons.Count > 0)
                return string.Join(" ; ", reasons);
        }
        catch
        {
            // Event log query failed — non-critical
        }

        return UnknownCrashReason;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
