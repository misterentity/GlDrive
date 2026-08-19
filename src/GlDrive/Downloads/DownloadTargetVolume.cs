using System.IO;

namespace GlDrive.Downloads;

/// <summary>
/// Distinguishes "the destination drive is not mounted" from every other download failure.
///
/// Root cause this exists for (observed 2026-08-17/18): four wishlist grabs — the whole
/// Disclosure.Day set, including a COMPLETE BLURAY — were enqueued to
/// <c>T:\Movies\Disclosure Day (2026)\…</c> on a box whose only filesystem drives are C, D and
/// E. Every attempt threw <see cref="DirectoryNotFoundException"/>, the generic retry arm
/// treated it as transient, and after three attempts spread over three minutes each item was
/// marked Failed and the grab was lost.
///
/// Two separate misjudgements, both of the same shape as the recurring "a decision made from
/// information that doesn't support it":
///   * A retry budget of 30/60/90 seconds encodes a belief that the condition clears in about
///     three minutes. An absent volume clears when a human plugs a drive in or fixes a path —
///     hours or days. Retrying is right; giving up on that schedule is not.
///   * The operator-facing message was the raw exception text, "Could not find a part of the
///     path 'T:\Movies\…'". Nothing in it says the DRIVE is the missing part, so the report
///     reads like a per-release problem four times over instead of one configuration fault.
///
/// This is deliberately narrow: only a genuinely absent volume root qualifies. A missing
/// intermediate directory on a mounted drive is something the downloader creates itself, and
/// treating it as unmountable would park a fault that really is ours.
/// </summary>
public static class DownloadTargetVolume
{
    /// <summary>Longest wait between re-checks. Bounds the log to ~24 lines/day per parked item.</summary>
    public static readonly TimeSpan MaxRecheckInterval = TimeSpan.FromHours(1);

    /// <summary>First wait after the volume is found absent.</summary>
    public static readonly TimeSpan InitialRecheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The volume root of <paramref name="destinationPath"/> if that volume is absent, else
    /// null. Returns null for a rooted path we cannot reason about (UNC shares, relative
    /// paths) so those keep the ordinary retry semantics.
    /// </summary>
    public static string? MissingVolumeRoot(string? destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath)) return null;

        string root;
        try
        {
            root = Path.GetPathRoot(destinationPath) ?? "";
        }
        catch (ArgumentException)
        {
            return null;
        }

        // Only local drive-letter roots ("T:\"). A UNC root being unreachable is a network
        // fault with its own transient character, not a drive that is simply not plugged in.
        if (root.Length < 2 || root[1] != ':') return null;

        return Directory.Exists(root) ? null : root;
    }

    /// <summary>
    /// True when this failure is explained by the destination volume being absent. Requires
    /// BOTH a path-shaped exception and a volume that really is missing — the exception alone
    /// is also raised for ordinary missing subdirectories.
    /// </summary>
    public static bool IsVolumeAbsent(Exception ex, string? destinationPath) =>
        ex is DirectoryNotFoundException or DriveNotFoundException
        && MissingVolumeRoot(destinationPath) is not null;

    /// <summary>
    /// Backoff for a parked item: 5 min, doubling to a 1 h ceiling. Attempt is 1-based.
    /// </summary>
    public static TimeSpan RecheckDelay(int attempt)
    {
        if (attempt < 1) attempt = 1;

        var ticks = InitialRecheckInterval.Ticks;
        for (var i = 1; i < attempt && ticks < MaxRecheckInterval.Ticks; i++) ticks *= 2;

        return TimeSpan.FromTicks(Math.Min(ticks, MaxRecheckInterval.Ticks));
    }
}
