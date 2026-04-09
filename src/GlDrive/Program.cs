using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GlDrive;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Watchdog mode — lightweight process monitor, no WPF.
        // Launched by the main app as: GlDrive.exe --watchdog <pid>
        var wdIdx = Array.IndexOf(args, "--watchdog");
        if (wdIdx >= 0 && wdIdx + 1 < args.Length && int.TryParse(args[wdIdx + 1], out var targetPid))
        {
            return RunWatchdog(targetPid);
        }

        // Normal mode — spawn watchdog then start WPF app
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

        // If an update is in progress, the updater handles restarting — stand down
        if (File.Exists(updateMarker))
        {
            try { File.Delete(updateMarker); } catch { }
            return 0;
        }

        if (!File.Exists(crashMarker))
        {
            // Clean exit — marker was deleted by OnExit. Nothing to do.
            return 0;
        }

        // Crash detected — log reason and restart GlDrive
        try
        {
            var crashReason = GetCrashReason(targetPid);
            File.WriteAllText(crashMarker, $"CRASH:{DateTime.UtcNow:O}");

            // Append crash details to the current log file
            var logDir = Path.Combine(appData, "logs");
            if (Directory.Exists(logDir))
            {
                var logFile = Path.Combine(logDir, $"gldrive-{DateTime.Now:yyyyMMdd}.log");
                var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [FTL] WATCHDOG: Process {targetPid} crashed — {crashReason}{Environment.NewLine}";
                File.AppendAllText(logFile, entry);
            }

            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false
                });
            }
        }
        catch
        {
            // Nothing we can do
        }

        return 0;
    }

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

        return "unknown (no matching event log entry found)";
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
