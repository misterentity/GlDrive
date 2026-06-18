using GlDrive.Config;

namespace GlDrive.AiAgent;

public sealed class PoolSizingValidator : IChangeValidator
{
    public string Category => AgentCategories.PoolSizing;

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        if (change.After is null) return new(false, "after-null", null);
        var afterStr = change.After.ToString()?.Trim('"') ?? "";
        if (!int.TryParse(afterStr, out var after))
            return new(false, "after-not-int", null);
        after = Math.Clamp(after, 2, 32);

        // Global spread pool size: /spread/spreadPoolSize
        if (change.Target == "/spread/spreadPoolSize")
        {
            var before = config.Spread?.SpreadPoolSize ?? 3;
            if (!WithinPct(before, after, 0.25)) return new(false, "change-too-large", null);
            return new(true, null, cfg => { if (cfg.Spread != null) cfg.Spread.SpreadPoolSize = after; });
        }

        // Global max concurrent races: /spread/maxConcurrentRaces
        if (change.Target == "/spread/maxConcurrentRaces")
        {
            var before = config.Spread?.MaxConcurrentRaces ?? 1;
            if (!WithinPct(before, after, 0.25)) return new(false, "change-too-large", null);
            // Concurrent races each consume a login permit on the (shared) source
            // server, so this MUST stay ≤ the source's spread-usable logins or every
            // race burst exhausts the source pools. Observed 2026-06-17: the agent
            // had ratcheted this to 5 against a loginCap-4 source → 971 "main pool
            // exhausted" warnings, scan failures, and FXP borrow timeouts. Clamp to
            // the smallest spread-usable login budget across servers.
            int ceiling = MaxConcurrentRaceCeiling(config);
            if (after > ceiling) after = ceiling;
            var applied = after;
            return new(true, null, cfg => { if (cfg.Spread != null) cfg.Spread.MaxConcurrentRaces = applied; });
        }

        // Per-server: /servers/{id}/spread/maxUploadSlots or /servers/{id}/spread/maxDownloadSlots
        foreach (var (suffix, getOld, applyNew) in new (string, Func<ServerConfig, int>, Action<ServerConfig, int>)[]
        {
            ("/spread/maxUploadSlots",   s => s.SpreadSite.MaxUploadSlots,   (s, v) => s.SpreadSite.MaxUploadSlots = v),
            ("/spread/maxDownloadSlots", s => s.SpreadSite.MaxDownloadSlots, (s, v) => s.SpreadSite.MaxDownloadSlots = v),
        })
        {
            if (!SkiplistValidator.TryMatchServer(change.Target, suffix, out var resolver, out var trailing)) continue;
            if (!string.IsNullOrEmpty(trailing)) continue;
            var getOldLocal = getOld;
            var applyLocal = applyNew;
            var afterLocal = after;
            return new(true, null, cfg =>
            {
                var s = resolver(cfg);
                if (s is null) return;
                int before = getOldLocal(s);
                if (!WithinPct(before, afterLocal, 0.25)) return;
                applyLocal(s, afterLocal);
            });
        }

        return new(false, "target-shape-unsupported", null);
    }

    // Largest safe MaxConcurrentRaces: races share the source server's login
    // budget, so cap to the smallest (LoginCap − LoginHeadroom) across servers,
    // minus one for the main/keepalive pool that already squats a permit. This
    // mirrors the documented invariant "maxConcurrentRaces ≤ source spread-usable
    // logins" (e.g. loginCap 4, headroom 1 → usable 3 → ceiling 2).
    private static int MaxConcurrentRaceCeiling(AppConfig config)
    {
        int minUsable = int.MaxValue;
        foreach (var s in config.Servers)
        {
            if (s.Pool is null) continue;
            int usable = s.Pool.LoginCap - s.Pool.LoginHeadroom;
            if (usable < minUsable) minUsable = usable;
        }
        if (minUsable == int.MaxValue) return 2; // no servers configured — conservative default
        return Math.Max(1, minUsable - 1);
    }

    private static bool WithinPct(int before, int after, double pct)
    {
        if (before <= 0) return after > 0 && after <= 32;
        var delta = Math.Abs(after - before);
        return delta <= Math.Ceiling(before * pct);
    }
}
