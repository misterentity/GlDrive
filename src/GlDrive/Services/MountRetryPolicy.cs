namespace GlDrive.Services;

/// <summary>
/// Backoff schedule and eligibility rules for retrying a server mount that failed.
///
/// Why this exists (v3.10.49): <see cref="ServerManager.MountAll"/> caught mount
/// failures, logged an error and moved on. <see cref="ServerManager.MountServer"/>
/// disposes the <c>MountService</c> on failure — and the ConnectionMonitor that would
/// normally reconnect lives *inside* that service. So a mount that failed once was
/// dead for the whole process lifetime. On 2026-08-06 the box resumed from sleep at
/// 09:04 and GlDrive started at 09:07 before the network was up: all three servers
/// failed with "unreachable host" / "No such host is known" inside 200ms, and the app
/// then did zero FTP work for the next 10 hours. IRC recovered on its own — that
/// asymmetry is what localized the fault.
///
/// The retry never gives up: an unreachable network is exactly the condition that
/// resolves by itself, and giving up permanently is the failure mode being fixed.
/// Delays are capped so a BNC is never hammered into its ~2h rate-limit cooldown.
/// </summary>
public static class MountRetryPolicy
{
    /// <summary>First retry fires this soon after the failure.</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    /// <summary>Backoff never exceeds this, so recovery stays prompt without hammering.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Delay before retry number <paramref name="attempt"/> (1-based): 30s, 60s, 120s,
    /// 240s, then pinned at 300s. Attempts below 1 are treated as the first attempt.
    /// </summary>
    public static TimeSpan DelayFor(int attempt)
    {
        if (attempt < 1) attempt = 1;
        // Cap the exponent before shifting so a large attempt count can't overflow.
        var steps = Math.Min(attempt - 1, 16);
        var seconds = InitialDelay.TotalSeconds * Math.Pow(2, steps);
        return seconds >= MaxDelay.TotalSeconds ? MaxDelay : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Whether a server that previously failed to mount should be retried now.
    /// A server that has since been mounted (manually, or by a config sync) or that
    /// has been disabled/removed from config is no longer our problem.
    /// </summary>
    public static bool ShouldRetry(bool alreadyMounted, bool existsInConfig, bool enabled, bool autoMountOnStart)
        => !alreadyMounted && existsInConfig && enabled && autoMountOnStart;
}
