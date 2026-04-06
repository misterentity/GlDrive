using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using GlDrive.Config;

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

        var appData = ConfigManager.AppDataPath;
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

        // Crash detected — restart GlDrive
        try
        {
            // Write a crash timestamp so the restarted instance can log it
            File.WriteAllText(crashMarker, $"CRASH:{DateTime.UtcNow:O}");

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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
