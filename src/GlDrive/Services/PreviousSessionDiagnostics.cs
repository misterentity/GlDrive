using System.IO;
using Serilog;

namespace GlDrive.Services;

/// <summary>Capture before replacing the marker; report after logging is configured.</summary>
internal sealed record PreviousSessionDiagnostics(bool? HadRunningMarker, string? WatchdogTime)
{
    internal static PreviousSessionDiagnostics Capture(string markerPath)
    {
        try
        {
            var marker = File.ReadAllText(markerPath).Trim();
            return new(true, marker.StartsWith("CRASH:", StringComparison.Ordinal) ? marker[6..] : null);
        }
        catch (FileNotFoundException) { return new(false, null); }
        catch (DirectoryNotFoundException) { return new(false, null); }
        catch { return new(null, null); }
    }

    /// <returns>Whether an unclean-exit notification is supported by the marker.</returns>
    internal bool Report(ILogger logger, HeartbeatCheckResult heartbeat)
    {
        if (WatchdogTime != null)
            logger.Warning("GlDrive: restarted by watchdog after unclean exit at {ExitTime}; " +
                "see watchdog logs for the observed cause", WatchdogTime);
        else if (HadRunningMarker == true)
            logger.Warning("GlDrive: previous session left a running marker; " +
                "shutdown did not complete, cause unknown (including OS shutdown or external termination)");
        else if (HadRunningMarker == null)
            logger.Warning("GlDrive: previous running marker could not be read; exit state unknown");

        if (heartbeat.HadHeartbeat)
            logger.Information("Previous heartbeat age={AgeSec}s, previous running marker={HadRunningMarker}, " +
                "snapshot={Snapshot}. Heartbeat age alone does not identify a crash or hang",
                (int)(heartbeat.AgeAtStartup ?? TimeSpan.Zero).TotalSeconds, HadRunningMarker, heartbeat.RawJson);
        else
            logger.Information("No readable previous heartbeat found");

        return HadRunningMarker == true;
    }
}
