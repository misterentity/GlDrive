using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using GlDrive.Services;

namespace GlDrive;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Updater modes must run before constructing the WPF Application. The scheduled task
        // runs as SYSTEM in a non-interactive session, where WPF startup can terminate before
        // App.OnStartup is reached. Dispatching here keeps the updater headless and makes the
        // fixed-action SYSTEM task usable without a logged-in desktop.
        var taskIdx = Array.FindIndex(args,
            arg => arg.Equals("--apply-update-task", StringComparison.OrdinalIgnoreCase));
        if (taskIdx >= 0)
        {
            UpdateChecker.ApplyUpdateFromTask();
            Process.GetCurrentProcess().Kill();
            return -1;
        }

        var applyIdx = Array.FindIndex(args,
            arg => arg.Equals("--apply-update", StringComparison.OrdinalIgnoreCase));
        if (applyIdx >= 0 && args.Length >= applyIdx + 4 &&
            int.TryParse(args[applyIdx + 1], out var updatePid))
        {
            UpdateChecker.ApplyUpdate(updatePid, args[applyIdx + 2], args[applyIdx + 3]);
            Process.GetCurrentProcess().Kill();
            return -1;
        }

        // Watchdog mode — lightweight process monitor, no WPF.
        // Launched by the main app as: GlDrive.exe --watchdog <pid>
        var wdIdx = Array.IndexOf(args, "--watchdog");
        if (wdIdx >= 0 && wdIdx + 1 < args.Length && int.TryParse(args[wdIdx + 1], out var targetPid))
        {
            return RunWatchdog(targetPid);
        }

        // Normal mode — spawn watchdog then start WPF app. Screenshot mode is an isolated
        // renderer; updater modes have already returned above.
        if (Array.IndexOf(args, "--screenshots") < 0)
            SpawnWatchdog();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>
    /// Spawns a background copy of ourselves in watchdog mode to monitor our PID.
    /// The watchdog is a hidden process that restarts GlDrive if it crashes.
    /// </summary>
    private static void SpawnWatchdog()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--watchdog {Environment.ProcessId}",
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
    private static int RunWatchdog(int targetPid)
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
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlDrive");
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
            try { File.Delete(updateMarker); } catch { }
            return 0;
        }
        // Stale/invalid/plain marker — delete it and fall through to crash-restart logic
        if (File.Exists(updateMarker))
        {
            try { File.Delete(updateMarker); } catch { }
        }

        if (!File.Exists(crashMarker))
        {
            // Clean exit — marker was deleted by OnExit. Nothing to do.
            return 0;
        }

        // The process exited without deleting its .running marker. That is USUALLY a
        // crash — but not always, and the watchdog has no business asserting a cause it
        // can't see. On 2026-08-05 07:39 this logged "[FTL] ... crashed — unknown (no
        // matching event log entry found)" 41 seconds before Kernel-Power logged "The
        // system is entering sleep", with no 1026/1000 event anywhere. Report the
        // evidence (unclean exit) and let the reason stand on its own.
        var logDir = Path.Combine(appData, "logs");
        var logFile = Path.Combine(logDir, $"gldrive-{DateTime.Now:yyyyMMdd}.log");

        void AppendLog(string level, string message)
        {
            try
            {
                if (!Directory.Exists(logDir)) return;
                File.AppendAllText(logFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] WATCHDOG: {message}{Environment.NewLine}");
            }
            catch { /* logging must never take the watchdog down */ }
        }

        try
        {
            var crashReason = GetCrashReason(targetPid);
            var identified = crashReason != UnknownCrashReason;
            File.WriteAllText(crashMarker, $"CRASH:{DateTime.UtcNow:O}");

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
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                AppendLog("ERR", "Cannot restart — Environment.ProcessPath is empty.");
                return 0;
            }

            var started = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
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
