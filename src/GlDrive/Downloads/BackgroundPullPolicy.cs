using System.IO;

namespace GlDrive.Downloads;

/// <summary>Background acquisitions may use an existing login only while racing does not need it.</summary>
internal static class BackgroundPullPolicy
{
    internal static bool MayRun(bool backgroundPull, Func<bool>? raceIdle)
    {
        if (!backgroundPull) return true;
        try { return raceIdle?.Invoke() == true; }
        catch { return false; } // Unknown racing state is not permission to occupy its login.
    }
}

internal sealed class BackgroundPullYieldedException() : IOException("Waiting for this site's racing work");

internal sealed record DownloadExecutionPolicy(Func<bool>? YieldWhen = null, Action<long>? OnBytes = null);
