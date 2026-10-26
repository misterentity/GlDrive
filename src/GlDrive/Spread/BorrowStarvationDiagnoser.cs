namespace GlDrive.Spread;

/// <summary>
/// Explains WHY a pool borrow timed out, from the pool's own counters rather
/// than from a guess baked into the log message.
///
/// The old FXP borrow-timeout warning asserted a single fixed cause — "pool
/// exhausted, server may have ghost connections (try !username login to kill
/// them)" — while the lines printed immediately around it routinely said
/// something else entirely:
///
///     Pool: server entering 20s login-cap backoff (no permit available)
///     Pool: new connection failed (created=2, max=3)
///     System.InvalidOperationException: Account login cap reached
///
/// created=2/max=3 is not an exhausted pool, and a login-cap stall is not a
/// ghost session — running `!username` on that advice kills the operator's own
/// live logins and does nothing about the actual contention. On 2026-08-04 that
/// message fired 194 times for superbnc -> SYN, every one of them pointing at
/// the wrong subsystem.
///
/// The distinctions that matter to a reader:
///   - cooldown       — the server refused us recently; it clears on a timer.
///   - gate backoff   — no account login permit was free, so the pool parked new
///                      connections for ~20s. Local contention, nothing remote.
///                      Invisible until v3.10.55: the pool stored this in the SAME
///                      field as the refusal cooldown, so it printed as "server
///                      refused a recent login" — the exact class of misattribution
///                      this type exists to prevent. 2026-08-10/11: 1 real refusal
///                      vs 1,009 gate backoffs, all reported as refusals.
///   - empty pool     — no connection is available. Counters alone cannot distinguish
///                      failed connection establishment from discarded connections.
///   - at capacity    — the pool holds its max and they are all busy; this is
///                      concurrency pressure, nothing is broken.
///   - below capacity — connection creation or login contention may be delaying
///                      recovery; counters alone do not establish a login cap.
/// </summary>
public static class BorrowStarvationDiagnoser
{
    /// <summary>Observable state of one pool at the moment a borrow timed out.</summary>
    public readonly record struct PoolState(
        bool IsInCooldown,
        bool IsExhausted,
        int Created,
        int Active,
        int MaxSize,
        bool IsInLoginGateBackoff = false,
        bool IsHostUnreachable = false);

    /// <summary>
    /// One-line cause for a single pool. Never speculates beyond the counters.
    /// </summary>
    public static string Describe(PoolState s)
    {
        if (s.IsInCooldown)
            return $"in cooldown (server refused a recent login; created={s.Created}/{s.MaxSize})";

        // The pool parks new connections for this reason too, but nothing was
        // refused by anyone remote — a pool on this account is holding the
        // permits. Reporting it as a server refusal sent the reader to the BNC
        // for 357 of 359 borrow timeouts on 2026-08-11.
        if (s.IsInLoginGateBackoff)
            return $"account login-gate backoff — no login permit was free, so new " +
                   $"connections are parked ~20s (created={s.Created}/{s.MaxSize}, active={s.Active}); " +
                   "local contention on this account — nothing was refused remotely";

        // Checked BEFORE the empty-pool branch, because created=0 is what BOTH look
        // like and only one of them is about the server. "Every connection was
        // discarded" and "no connection was ever established" are different facts;
        // a ghost session is a stale login on a server we reached, so it cannot
        // explain a host that DNS does not resolve. On 2026-09-17 this branch told
        // the operator to run !username against an unresolvable hostname 14,061
        // times — advice that, taken, severs the account's own live sessions.
        if (s.IsHostUnreachable)
            return $"host unreachable — no connection was ever established (created=0/{s.MaxSize}); " +
                   "DNS or routing, not the server — nothing was refused and no login was attempted";

        if (s.IsExhausted || (s.Created <= 0 && s.Active <= 0))
            return $"pool empty — no connection is available (created=0/{s.MaxSize}); " +
                   "connection establishment failed or previous connections were discarded";

        if (s.Active < s.Created)
            return $"pool has unborrowed or connecting capacity now (created={s.Created}/{s.MaxSize}, active={s.Active}); " +
                   "the earlier wait cannot be diagnosed from this snapshot";

        if (s.Created >= s.MaxSize)
            return $"pool at capacity — all {s.MaxSize} connection(s) busy (active={s.Active}); " +
                   "concurrency pressure, not a fault";

        return $"connection unavailable below capacity (created={s.Created}/{s.MaxSize}, active={s.Active}); " +
               "connection creation or account login contention may be delaying recovery";
    }

    /// <summary>
    /// A successful peer borrow is held until the failed peer completes. Its active
    /// count includes this transfer itself, so it cannot explain this borrow failure.
    /// </summary>
    public static string DescribeBorrowFailure(string srcName, PoolState src, bool srcAcquired,
        string dstName, PoolState dst, bool dstAcquired)
    {
        if (srcAcquired && dstAcquired)
            return "both pool connections were acquired; setup failed after borrowing";
        if (srcAcquired) return $"{dstName}: {Describe(dst)}";
        if (dstAcquired) return $"{srcName}: {Describe(src)}";
        return Describe(srcName, src, dstName, dst);
    }

    /// <summary>
    /// Cause text for a borrow that waited on both endpoints. Only names the
    /// side(s) actually starved: a pool with an idle connection to spare did not
    /// cause the timeout and saying so would repeat the original mistake.
    /// </summary>
    public static string Describe(string srcName, PoolState src, string dstName, PoolState dst)
    {
        var srcStarved = IsStarved(src);
        var dstStarved = IsStarved(dst);

        // Neither side looks starved now — the state recovered between the
        // timeout and this read, or the wait was on the gate. Report both
        // rather than inventing a culprit.
        if (!srcStarved && !dstStarved)
            return $"neither pool is starved as of now — {srcName}: {Describe(src)}; " +
                   $"{dstName}: {Describe(dst)}";

        if (srcStarved && dstStarved)
            return $"{srcName}: {Describe(src)}; {dstName}: {Describe(dst)}";

        return srcStarved
            ? $"{srcName}: {Describe(src)}"
            : $"{dstName}: {Describe(dst)}";
    }

    /// <summary>A pool that could not hand out a connection on demand.</summary>
    public static bool IsStarved(PoolState s) =>
        s.IsInCooldown || s.IsInLoginGateBackoff || s.IsHostUnreachable
        || s.IsExhausted || s.Created <= 0 || s.Active >= s.Created;
}
