using System;
using System.IO;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression guard for v3.10.51 — the dest scan starving its own FXP transfers.
///
/// ScanSites tries the main pool first and falls back to the dedicated spread (FXP)
/// pool when the main pool's borrow times out. That fallback was UNCONDITIONAL, and a
/// scan re-runs every ~2s while an FXP borrow waits up to 30s — so on a login-capped
/// account the scan won the permit race every time and no transfer ever started.
///
/// 2026-08-09 on zephyr (loginCap 4 = main pool 1 + spread pool 3, zero headroom):
/// 1,393 "main pool exhausted ... falling back to spread pool", 2,779 FXP borrow
/// timeouts, 1,464 "scan FAILED (both pools unavailable)" — against 1 FXP transfer
/// error and 2 MKD failures in the entire day. Both endpoints were healthy. 321 races
/// delivered files in 4 of them; every dest eventually exhausted its backoff ladder and
/// was dropped, so releases like Big.Brother.US.S28E17 got an empty directory created
/// on zephyr and then SITE WIPEd.
///
/// v3.10.43 fixed the scan deadlocking against itself and already observed the scan
/// "stealing transfer slots" — this closes that second half.
/// </summary>
public class SpreadScanSpreadPoolYieldTests
{
    // ---- the predicate ----

    [Theory]
    // spreadActive, spreadMaxSize -> may the scan borrow?
    [InlineData(0, 3, true)]   // idle pool: 2 would remain, fine
    [InlineData(1, 3, true)]   // one transfer in flight: 1 would remain, fine
    [InlineData(2, 3, false)]  // taking it would leave zero for an FXP borrow
    [InlineData(3, 3, false)]  // already saturated
    public void Scan_yields_the_last_spread_permit_to_transfers(int active, int max, bool expected)
        => Assert.Equal(expected, CandidatePredicates.ScanMayBorrowSpreadPool(active, max, mainPoolUsable: true));

    [Fact]
    public void A_single_slot_spread_pool_is_never_raided_by_a_scan()
    {
        // max=1 means the only permit IS the transfer permit.
        Assert.False(CandidatePredicates.ScanMayBorrowSpreadPool(0, 1, mainPoolUsable: true));
        Assert.False(CandidatePredicates.ScanMayBorrowSpreadPool(1, 1, mainPoolUsable: true));
    }

    [Fact]
    public void An_unusable_main_pool_still_permits_the_original_recovery_fallback()
    {
        // The v3.10.x fallback exists so a DEAD main pool can't abandon the scan outright.
        // "Busy" must not qualify — only genuinely unusable.
        Assert.True(CandidatePredicates.ScanMayBorrowSpreadPool(3, 3, mainPoolUsable: false));
        Assert.True(CandidatePredicates.ScanMayBorrowSpreadPool(0, 1, mainPoolUsable: false));
    }

    [Fact]
    public void Zephyr_2026_08_09_shape_would_have_yielded()
    {
        // zephyr: spread pool size=3, two FXP transfers in flight, main pool alive but
        // saturated (size 1). The old code took the third permit here, every 2s.
        Assert.False(CandidatePredicates.ScanMayBorrowSpreadPool(
            spreadActive: 2, spreadUsableMax: 3, mainPoolUsable: true));
    }

    // ---- the call site actually uses it ----

    private static string ReadSpreadJobSource()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "src", "GlDrive", "Spread", "SpreadJob.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException(
            "Could not locate src/GlDrive/Spread/SpreadJob.cs from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Spread_pool_fallback_is_gated_by_the_predicate()
    {
        var source = ReadSpreadJobSource();

        var gate = source.IndexOf("CandidatePredicates.ScanMayBorrowSpreadPool", StringComparison.Ordinal);
        Assert.True(gate >= 0,
            "ScanSites must gate its spread-pool fallback on ScanMayBorrowSpreadPool — " +
            "an unconditional fallback starves FXP transfers (v3.10.51).");

        var fallback = source.IndexOf("using spread pool fallback", StringComparison.Ordinal);
        Assert.True(fallback > gate,
            "The gate must be evaluated BEFORE the fallback borrow, not after it.");
    }

    [Fact]
    public void A_deliberate_yield_is_not_logged_as_a_scan_failure()
    {
        var source = ReadSpreadJobSource();

        Assert.Contains("yieldedToTransfers", source);
        var warn = source.IndexOf("Spread scan FAILED for {Server}", StringComparison.Ordinal);
        Assert.True(warn >= 0, "The both-pools-unavailable warning was renamed or removed.");

        // The WRN must sit behind an `else if (!yieldedToTransfers)`, otherwise a healthy
        // yield re-creates the 1,464 warnings/day that buried the real signal.
        // Window widened in v3.10.54: the branch gained the contention/fault severity
        // split and its rationale. The `else if (!yieldedToTransfers)` guard is unchanged.
        var guardWindow = source[Math.Max(0, warn - 1000)..warn];
        Assert.Contains("!yieldedToTransfers", guardWindow);
    }
}
