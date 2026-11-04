using System.Diagnostics;

namespace GlDrive.Services;

/// <summary>
/// How long the interactive watchdog waits for the SYSTEM updater's completion signal before
/// relaunching the app itself. Relaunching early starts the app while files are mid-rename.
/// </summary>
internal static class UpdateWatchdogWait
{
    internal static readonly TimeSpan BaseWait = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan HardCap = TimeSpan.FromMinutes(20);

    internal static bool ShouldKeepWaiting(TimeSpan elapsed, bool completionSignalled, Func<bool> updaterAlive)
    {
        if (completionSignalled) return false;
        if (elapsed < BaseWait) return true;
        return elapsed < HardCap && updaterAlive();
    }

    /// <summary>Any GlDrive process other than this watchdog and the app being replaced: the updater.</summary>
    internal static bool OtherGlDriveProcessAlive(int replacedPid)
    {
        try
        {
            var self = Environment.ProcessId;
            var processes = Process.GetProcessesByName("GlDrive");
            try { return processes.Any(p => p.Id != self && p.Id != replacedPid); }
            finally { foreach (var p in processes) p.Dispose(); }
        }
        catch
        {
            return false;
        }
    }
}
