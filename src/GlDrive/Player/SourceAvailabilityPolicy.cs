namespace GlDrive.Player;

/// <summary>
/// Tracks which torrent-search backends are currently worth talking to, and — crucially —
/// lets a failed one back in after a cooldown.
///
/// Root cause this exists for (observed 2026-08-15): each source had its own ad-hoc
/// `_xChecked` / `_xHost` pair, and the probe was a one-shot latch. `_apibayChecked = true`
/// was set BEFORE the probe loop, so a probe that failed left the host empty and the source
/// disabled for the entire process lifetime. The documented reset — a 403/503 on a real
/// search — could never fire, because no search is issued when the host is empty.
/// `_csvChecked` had no reset path whatsoever. A single transient blip at startup silently
/// removed a source until the user restarted GlDrive.
///
/// Fourth instance of "a decision that never expires is a permanent exemption" in this
/// codebase (v3.10.41 declined UAC stranded the box 51h; v3.10.42 _destDirConfirmed overrode
/// the MKD gate forever; v3.10.65 _watchAbandoned killed externally-landed extractions).
///
/// Time is the right expiry here. Unlike a volume set there is no fingerprint to watch, and a
/// dead public indexer is exactly the sort of thing that comes back without warning. The
/// cooldown is fixed rather than exponential on purpose: repeated failures must not compound
/// into an ever-longer exile, or the "permanent exemption" returns wearing a backoff curve.
/// </summary>
public sealed class SourceAvailabilityPolicy
{
    /// <summary>
    /// How long a failed source sits out. Short enough that retrying a search a few minutes
    /// later picks up a recovered host; long enough not to hammer a dead one on every search.
    /// </summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private enum Health { Unknown, Available, Unavailable }

    private readonly object _lock = new();
    private readonly Dictionary<string, (Health State, DateTime At)> _sources =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when this source has never been probed, or has served its cooldown.</summary>
    public bool ShouldProbe(string source, DateTime utcNow)
    {
        lock (_lock)
        {
            if (!_sources.TryGetValue(source, out var entry)) return true;

            return entry.State switch
            {
                Health.Available => false,
                Health.Unavailable => utcNow - entry.At > RetryAfter,
                _ => true,
            };
        }
    }

    /// <summary>True when this source is known good and may be queried without probing.</summary>
    public bool IsUsable(string source, DateTime utcNow)
    {
        lock (_lock)
        {
            return _sources.TryGetValue(source, out var entry)
                   && entry.State == Health.Available
                   && utcNow >= entry.At;
        }
    }

    public void MarkAvailable(string source, DateTime utcNow)
    {
        lock (_lock) _sources[source] = (Health.Available, utcNow);
    }

    /// <summary>
    /// Record a failure — from a probe or from a live search that returned 403/503. Restarts
    /// the fixed cooldown; it never lengthens it.
    /// </summary>
    public void MarkUnavailable(string source, DateTime utcNow)
    {
        lock (_lock) _sources[source] = (Health.Unavailable, utcNow);
    }

    /// <summary>Diagnostic snapshot: source name to its current state, for logging.</summary>
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_lock)
            return _sources.ToDictionary(kv => kv.Key, kv => kv.Value.State.ToString());
    }
}
