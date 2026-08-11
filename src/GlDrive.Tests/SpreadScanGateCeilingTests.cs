using System;
using System.IO;
using System.Linq;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression guard for v3.10.54 — the v3.10.51 scan-yield guard was 100% INERT.
///
/// v3.10.51 stopped the dest scan raiding the FXP pool by requiring two free slots
/// before the scan may take one: <c>spreadMaxSize - spreadActive >= 2</c>. That was
/// written against zephyr's spread pool of 3, where the pool's nominal size was a fair
/// PROXY for how many logins could actually be opened.
///
/// The proxy broke. `spread.spreadPoolSize` is 10 in production and the pool auto-scales
/// to max(SpreadPoolSize, maxSlots), so spreadMaxSize is 10 — while the binding ceiling
/// is the ACCOUNT LOGIN GATE's priority sub-cap, which on Dave's accounts is 1:
///
///     loginCap=4, loginHeadroom=2  =>  usable = 2
///     reserved     = min(2, usable-1) = 1
///     mainReserved = (usable >= 2) ? 1 : 0 = 1
///     priority sub-cap = usable - mainReserved = 1     <-- the real ceiling
///
/// So `10 - active >= 2` was true on every single evaluation and the scan NEVER yielded.
/// 2026-08-10 gldrive log: 534 "using spread pool fallback", **0** "yielding ... to FXP
/// transfers", 1,253 FXP borrow timeouts — every one of them reporting the identical
/// counter signature `created=1/3, active=1`, i.e. the pool held exactly one login and
/// the gate would never grant a second.
///
/// The fix keys on the property that DEFINES the ceiling (what the gate can actually
/// grant this pool's role) instead of the pool's nominal size, per recurring-bug
/// pattern #4. Pattern #1 also applies: the guard decided "there is room" from a number
/// that does not describe the resource being contended.
/// </summary>
public class SpreadScanGateCeilingTests
{
    // ---- the gate exposes the ceiling it will actually grant ----

    [Fact]
    public void Production_gate_shape_grants_a_priority_caller_exactly_one_login()
    {
        // cap=4, headroom=2 -> usable=2. This is Dave's live config on all three sites.
        var gate = new ServerLoginGate("test:1:u", limit: 2, maxLimit: 2, reserved: 1);

        Assert.Equal(2, gate.Limit);
        Assert.Equal(1, gate.PriorityLimit);   // FXP/spread ceiling
        Assert.Equal(1, gate.GeneralLimit);    // main-pool ceiling
    }

    [Theory]
    // usable, reserved -> expected priority ceiling (= usable - mainReserved)
    [InlineData(1, 0, 1)]   // single login: priority must still be able to run
    [InlineData(2, 1, 1)]   // PRODUCTION shape — the whole bug
    [InlineData(3, 2, 2)]
    [InlineData(4, 2, 3)]
    public void Priority_ceiling_is_the_total_minus_the_mount_reservation(
        int usable, int reserved, int expectedPriority)
    {
        var gate = new ServerLoginGate("k", usable, usable, reserved);
        Assert.Equal(expectedPriority, gate.PriorityLimit);
    }

    [Fact]
    public void The_two_sub_caps_never_exceed_the_total_login_limit()
    {
        // A sub-cap pair that oversubscribes the account would re-create the 530 storms
        // the gate exists to prevent.
        for (var usable = 1; usable <= 8; usable++)
        {
            var gate = new ServerLoginGate("k" + usable, usable, usable, Math.Min(2, usable - 1));
            Assert.True(gate.PriorityLimit >= 1, $"usable={usable}: priority deadlocked at 0");
            Assert.True(gate.GeneralLimit >= 1, $"usable={usable}: main pool deadlocked at 0");
        }
    }

    // ---- a gated pool reports the ceiling, not its nominal size ----

    [Fact]
    public void A_priority_pool_reports_the_gate_ceiling_not_its_nominal_size()
    {
        // The exact production mismatch: pool sized 10, gate will grant 1.
        var gate = new ServerLoginGate("k", limit: 2, maxLimit: 2, reserved: 1);
        var pool = new FtpConnectionPool(factory: null!, maxSize: 10, loginGate: gate, priorityLogins: true);

        Assert.Equal(10, pool.MaxSize);
        Assert.Equal(1, pool.EffectiveMaxSize);
    }

    [Fact]
    public void A_non_priority_pool_reports_the_general_ceiling()
    {
        var gate = new ServerLoginGate("k", limit: 2, maxLimit: 2, reserved: 1);
        var pool = new FtpConnectionPool(factory: null!, maxSize: 10, loginGate: gate, priorityLogins: false);

        Assert.Equal(1, pool.EffectiveMaxSize);
    }

    [Fact]
    public void An_ungated_pool_is_bounded_only_by_its_own_size()
    {
        // The 2-arg ctor (tests / legacy) must behave exactly as before.
        var pool = new FtpConnectionPool(factory: null!, maxSize: 4);
        Assert.Equal(4, pool.EffectiveMaxSize);
    }

    [Fact]
    public void The_ceiling_is_the_smaller_of_pool_size_and_gate_grant()
    {
        // A pool smaller than the gate allowance is still bounded by itself.
        var gate = new ServerLoginGate("k", limit: 6, maxLimit: 6, reserved: 2);
        var pool = new FtpConnectionPool(factory: null!, maxSize: 2, loginGate: gate, priorityLogins: true);

        Assert.Equal(5, gate.PriorityLimit);
        Assert.Equal(2, pool.EffectiveMaxSize);
    }

    // ---- the predicate, fed the real ceiling, actually yields ----

    [Fact]
    public void The_2026_08_10_production_shape_now_yields()
    {
        // What actually happened 1,253 times: pool nominal 10, one live login, main pool
        // alive but busy. Fed MaxSize the guard said "go"; fed the real ceiling it yields.
        Assert.True(CandidatePredicates.ScanMayBorrowSpreadPool(
            spreadActive: 1, spreadUsableMax: 10, mainPoolUsable: true),
            "sanity: the OLD (broken) inputs did permit the raid");

        Assert.False(CandidatePredicates.ScanMayBorrowSpreadPool(
            spreadActive: 1, spreadUsableMax: 1, mainPoolUsable: true));
        Assert.False(CandidatePredicates.ScanMayBorrowSpreadPool(
            spreadActive: 0, spreadUsableMax: 1, mainPoolUsable: true));
    }

    [Fact]
    public void A_dead_main_pool_still_overrides_the_yield()
    {
        // Never trade the v3.10.x recovery fallback away: if the main pool is unusable
        // the scan is the only thing that can make progress.
        Assert.True(CandidatePredicates.ScanMayBorrowSpreadPool(
            spreadActive: 1, spreadUsableMax: 1, mainPoolUsable: false));
    }

    // ---- the call site passes the ceiling, not MaxSize ----

    private static string ReadSource(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(new[] { dir }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not locate " + string.Join('/', parts));
    }

    [Fact]
    public void ScanSites_feeds_the_predicate_the_gate_ceiling()
    {
        var source = ReadSource("src", "GlDrive", "Spread", "SpreadJob.cs");
        // Anchor on the CALL (trailing paren) — the identifier also appears in prose above it.
        var call = source.IndexOf("ScanMayBorrowSpreadPool(", StringComparison.Ordinal);
        Assert.True(call >= 0, "the yield guard was removed");

        // Look at the argument list actually passed.
        var args = source.Substring(call, Math.Min(220, source.Length - call));
        Assert.Contains("EffectiveMaxSize", args);
        Assert.DoesNotContain("spreadPool.MaxSize", args);
    }
}
