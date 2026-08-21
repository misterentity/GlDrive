namespace GlDrive.Downloads;

/// <summary>
/// Cooldown ladder for the PreDB HTTP endpoint.
///
/// Root cause this exists for (observed 2026-08-19/20): api.predb.net answered 503 for hours at
/// a stretch. The dashboard refreshes on a FIXED 15-second timer with no failure path of its
/// own, and <c>PreDbClient</c> swallowed each failure with a full-stack
/// <c>Log.Warning(ex, "PreDB latest fetch failed")</c>. The result was 135 warnings on 08-19 and
/// 64 more on 08-20 — the second-largest warning cluster in the log — each carrying a stack
/// trace, and roughly four pointless requests per minute aimed at a service that was telling us
/// plainly it could not serve them.
///
/// The service being down is not ours to fix. Hammering it at a rate that ignores its answer,
/// and burying the rest of the log while doing so, is. A 503 is information: it says "not now",
/// and a client that keeps the same cadence has declined to use it.
///
/// The ladder resets completely on any success, so a brief blip costs one skipped refresh
/// rather than a quarter hour of blindness.
/// </summary>
public sealed class PreDbBackoff
{
    /// <summary>Cooldown after the Nth consecutive failure (1-based), capped at the last entry.</summary>
    internal static readonly TimeSpan[] Ladder =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
    ];

    private readonly object _lock = new();
    private int _consecutiveFailures;
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;

    /// <summary>Consecutive failures since the last success. Zero when healthy.</summary>
    public int ConsecutiveFailures { get { lock (_lock) return _consecutiveFailures; } }

    /// <summary>
    /// True when a request should be skipped outright. The caller returns its empty result
    /// without touching the network.
    /// </summary>
    public bool ShouldSkip(DateTimeOffset now)
    {
        lock (_lock) return now < _openUntil;
    }

    /// <summary>Clear the ladder. Any successful call, however long the outage was.</summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _openUntil = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Escalate one step and return the cooldown now in force, so the caller can say how long
    /// it is standing down in the single line it logs.
    /// </summary>
    public TimeSpan RecordFailure(DateTimeOffset now)
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            var delay = Ladder[Math.Min(_consecutiveFailures, Ladder.Length) - 1];
            _openUntil = now + delay;
            return delay;
        }
    }

    /// <summary>
    /// Whether this failure should be logged with its exception. The first one carries the
    /// stack because it is the one that explains the outage; the rest are the same fact
    /// restated, and the cooldown already bounds how often they can appear.
    /// </summary>
    public bool ShouldLogWithException => ConsecutiveFailures <= 1;
}
