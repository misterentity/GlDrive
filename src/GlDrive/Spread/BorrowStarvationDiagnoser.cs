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
///   - empty pool     — every connection really was discarded. THIS is the case
///                      where ghost sessions are a plausible cause.
///   - at capacity    — the pool holds its max and they are all busy; this is
///                      concurrency pressure, nothing is broken.
///   - login cap      — below max but no free account login permit, i.e. some
///                      other pool for the same account is holding them.
/// </summary>
public static class BorrowStarvationDiagnoser
{
    /// <summary>Observable state of one pool at the moment a borrow timed out.</summary>
    public readonly record struct PoolState(
        bool IsInCooldown,
        bool IsExhausted,
        int Created,
        int Active,
        int MaxSize);

    /// <summary>
    /// One-line cause for a single pool. Never speculates beyond the counters.
    /// </summary>
    public static string Describe(PoolState s)
    {
        if (s.IsInCooldown)
            return $"in cooldown (server refused a recent login; created={s.Created}/{s.MaxSize})";

        if (s.IsExhausted || (s.Created <= 0 && s.Active <= 0))
            return $"pool empty — every connection was discarded (created=0/{s.MaxSize}); " +
                   "ghost sessions are plausible here, try a !username login";

        if (s.Created >= s.MaxSize)
            return $"pool at capacity — all {s.MaxSize} connection(s) busy (active={s.Active}); " +
                   "concurrency pressure, not a fault";

        return $"account login cap — no free login permit for a new connection " +
               $"(created={s.Created}/{s.MaxSize}, active={s.Active}); another pool on this " +
               "account is holding the permits";
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
        s.IsInCooldown || s.IsExhausted || s.Created <= 0 || s.Active >= s.Created;
}
