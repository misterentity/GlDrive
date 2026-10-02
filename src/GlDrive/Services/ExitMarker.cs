namespace GlDrive.Services;

internal enum WatchdogExitVerdict
{
    /// <summary>No marker: OnExit finished teardown and deleted it.</summary>
    Clean,
    /// <summary>The app had begun an intended exit (tray Exit, Windows restart/logoff) and
    /// died before teardown finished. Not a crash, and restarting would defeat the exit.</summary>
    IntendedExit,
    /// <summary>The process died while it believed it was running.</summary>
    Unclean,
}

/// <summary>
/// Contents of the <c>.running</c> crash marker. A live session writes an ISO timestamp, the
/// watchdog rewrites it to <c>CRASH:</c> after an unclean exit, and an intended exit rewrites
/// it to <c>EXITING:</c> BEFORE teardown starts. Without that last state, OnExit only deleted
/// the marker after a multi-second teardown, so a Windows restart that killed the process
/// mid-teardown (2026-10-01 17:07) was reported as an unclean exit, the watchdog relaunched
/// GlDrive into the shutting-down OS, and the next boot showed a "did not complete" balloon.
/// </summary>
internal static class ExitMarker
{
    internal const string ExitingPrefix = "EXITING:";
    internal const string CrashPrefix = "CRASH:";

    internal static string Exiting(DateTime utc, string reason)
        => $"{ExitingPrefix}{utc:O}|{reason.Replace('\r', ' ').Replace('\n', ' ')}";

    internal static bool TryParseExiting(string? content, out string since, out string reason)
    {
        since = reason = "";
        if (content == null) return false;
        content = content.Trim();
        if (!content.StartsWith(ExitingPrefix, StringComparison.Ordinal)) return false;
        var body = content[ExitingPrefix.Length..];
        var bar = body.IndexOf('|');
        since = bar < 0 ? body : body[..bar];
        reason = bar < 0 ? "unspecified" : body[(bar + 1)..];
        return true;
    }

    /// <param name="exists">Whether the marker file exists.</param>
    /// <param name="content">Its contents, or null when it exists but could not be read.</param>
    internal static WatchdogExitVerdict ClassifyForWatchdog(bool exists, string? content)
    {
        if (!exists) return WatchdogExitVerdict.Clean;
        // An unreadable marker keeps the old behaviour: restarting a dead app is the safe default.
        return TryParseExiting(content, out _, out _) ? WatchdogExitVerdict.IntendedExit : WatchdogExitVerdict.Unclean;
    }
}
