namespace GlDrive.Ftp;

/// <summary>
/// Per-account rate limit on <c>!username</c> ghost-kill logins.
///
/// A ghost-kill logs in as <c>!user</c>, which makes glftpd terminate EVERY other
/// session of that user on the site — stale BNC ghosts and our own live transfers
/// alike. <see cref="FtpConnectionPool"/> already limits itself to one kill per
/// "pressure episode", but an episode ends on the next successful connect, and a
/// kill guarantees that the next connect succeeds. Under sustained pressure the
/// per-pool guard therefore re-armed within a second or two and the site received a
/// kill for every failed borrow: 7 <c>!entity</c> logins in 20 s on 2026-09-04,
/// each one severing the in-flight FXP transfer whose failure had triggered it.
///
/// The interval lives here, on the account, because every pool for a server shares
/// one <see cref="FtpClientFactory"/> and the BNC counts reconnects per account,
/// not per pool.
/// </summary>
public sealed class GhostKillThrottle
{
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(60);

    private readonly object _lock = new();
    private long _lastKillUtcTicks;

    public GhostKillThrottle(TimeSpan? minInterval = null)
    {
        MinInterval = minInterval ?? DefaultMinInterval;
    }

    public TimeSpan MinInterval { get; }

    /// <summary>UTC time of the last permitted kill, or null if none yet.</summary>
    public DateTime? LastKillUtc
    {
        get
        {
            var t = Interlocked.Read(ref _lastKillUtcTicks);
            return t == 0 ? null : new DateTime(t, DateTimeKind.Utc);
        }
    }

    /// <summary>
    /// Returns true and records the kill if at least <see cref="MinInterval"/> has
    /// elapsed since the last permitted kill (or none has happened). Returns false
    /// otherwise; <paramref name="sinceLast"/> then says how long ago that kill was.
    /// </summary>
    public bool TryAcquire(DateTime nowUtc, out TimeSpan sinceLast)
    {
        lock (_lock)
        {
            var last = _lastKillUtcTicks;
            if (last != 0)
            {
                sinceLast = nowUtc - new DateTime(last, DateTimeKind.Utc);
                if (sinceLast < MinInterval) return false;
            }
            else
            {
                sinceLast = TimeSpan.Zero;
            }
            _lastKillUtcTicks = nowUtc.Ticks;
            return true;
        }
    }

    public bool TryAcquire(DateTime nowUtc) => TryAcquire(nowUtc, out _);
}
