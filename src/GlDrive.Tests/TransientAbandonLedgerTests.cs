using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the 2026-08-15 report: "sometimes releases land from outside the
/// GlDrive drive and need extraction" — and inconsistently don't get it.
///
/// Root cause: <c>ExtractorWindow._watchAbandoned</c> was an add-only HashSet consulted
/// before every other check. Once a watched path was abandoned, no watcher event, no folder
/// change and no fingerprint change could revive it — only an app restart. The abandon log
/// line promised "It will be retried after the folder changes", which was false.
///
/// Why it hit external arrivals specifically: GlDrive's own downloads stage into
/// <c>.gldrive-staging-*</c> (skipped) and are MOVED into place, so the watcher sees one
/// event for an already-complete file. An external copy is created empty and filled in
/// place, so the watcher fires at 0 bytes and the readiness budget — WaitForVolumeSetReady
/// (300s) x 6 attempts plus 30/60/90/120/150s backoff, ~37 minutes — expires while the copy
/// is still running. Whether a release extracted came down to whether it beat that timer.
///
/// Third occurrence of "a decision that never expires is a permanent exemption"
/// (v3.10.41 UAC decline, v3.10.42 _destDirConfirmed). The fix is the same one the DURABLE
/// twin already uses: key the verdict to the volume-set fingerprint so it lapses exactly
/// when a retry could plausibly do something different.
/// </summary>
public sealed class TransientAbandonLedgerTests
{
    private const string Rar = @"D:\x265\Some.Release.2026.2160p.WEB.h265-GRP\some.release.rar";

    [Fact]
    public void Unknown_path_is_not_abandoned()
    {
        var ledger = new TransientAbandonLedger();
        Assert.False(ledger.IsAbandoned(Rar, volumeCount: 3, totalBytes: 100));
    }

    [Fact]
    public void Abandoned_path_stays_abandoned_while_the_set_is_unchanged()
    {
        var ledger = new TransientAbandonLedger();
        ledger.Abandon(Rar, 3, 100);

        Assert.True(ledger.IsAbandoned(Rar, 3, 100));
        // Repeated asks must not erode the verdict — a stalled copy should stay parked.
        Assert.True(ledger.IsAbandoned(Rar, 3, 100));
    }

    /// <summary>
    /// THE test. A copy that was still running when we gave up keeps growing; the next
    /// watcher event or sweep must find the path live again. This is the assertion the
    /// old add-only HashSet could never satisfy.
    /// </summary>
    [Fact]
    public void GrowingSet_revives_the_path()
    {
        var ledger = new TransientAbandonLedger();
        ledger.Abandon(Rar, 3, 100);

        Assert.False(ledger.IsAbandoned(Rar, 3, 4_000_000_000));
        // Revival is a one-way door until it is abandoned again — the caller gets a clean retry.
        Assert.False(ledger.IsAbandoned(Rar, 3, 4_000_000_000));
    }

    [Fact]
    public void NewVolumeArriving_revives_the_path_even_at_equal_bytes()
    {
        var ledger = new TransientAbandonLedger();
        ledger.Abandon(Rar, 3, 100);

        Assert.False(ledger.IsAbandoned(Rar, 4, 100));
    }

    /// <summary>
    /// A set that stops changing stays abandoned. This is what keeps revival from becoming
    /// the inverse defect — an endless retry loop against a dead half-copy.
    /// </summary>
    [Fact]
    public void StalledCopy_stays_abandoned()
    {
        var ledger = new TransientAbandonLedger();
        ledger.Abandon(Rar, 12, 8_000_000_000);

        for (var i = 0; i < 20; i++)
            Assert.True(ledger.IsAbandoned(Rar, 12, 8_000_000_000));
    }

    /// <summary>
    /// An unreadable fingerprint is the (-1,-1) sentinel ComputeVolumeSetFingerprint returns.
    /// It must never match a recorded entry, so the path stays live rather than being frozen
    /// out by a directory we momentarily could not read.
    /// </summary>
    [Fact]
    public void UnreadableFingerprint_never_holds_a_path_abandoned()
    {
        var ledger = new TransientAbandonLedger();
        ledger.Abandon(Rar, -1, -1);

        Assert.False(ledger.IsAbandoned(Rar, -1, -1));
    }

    /// <summary>
    /// "Never abandoned" and "revived" both let the caller proceed, but only revival may clear
    /// the watcher's duplicate-event bookkeeping. Collapsing them into one bool would clear
    /// that gate on every ordinary event and let an archive that is mid-extraction be queued a
    /// second time — which is exactly the bug I nearly wrote while fixing this one.
    /// </summary>
    [Fact]
    public void Evaluate_distinguishes_never_abandoned_from_revived()
    {
        var ledger = new TransientAbandonLedger();

        Assert.Equal(TransientAbandonLedger.AbandonState.NotAbandoned,
            ledger.Evaluate(Rar, 3, 100));

        ledger.Abandon(Rar, 3, 100);

        Assert.Equal(TransientAbandonLedger.AbandonState.StillAbandoned,
            ledger.Evaluate(Rar, 3, 100));

        Assert.Equal(TransientAbandonLedger.AbandonState.Revived,
            ledger.Evaluate(Rar, 3, 900));

        // Revival is consumed: the next look is an ordinary unabandoned path, so the caller
        // does not clear its bookkeeping twice.
        Assert.Equal(TransientAbandonLedger.AbandonState.NotAbandoned,
            ledger.Evaluate(Rar, 3, 900));
    }

    /// <summary>
    /// The scenario end to end: a release copied in from outside GlDrive is still arriving
    /// when the retry budget runs out, keeps growing, and must extract without a restart.
    /// </summary>
    [Fact]
    public void ExternalCopy_that_outlives_the_retry_budget_still_extracts()
    {
        var ledger = new TransientAbandonLedger();

        // ~37 minutes in, the copy is 12 of 40 parts down and we give up.
        ledger.Abandon(Rar, 12, 3_000_000_000);
        Assert.True(ledger.IsAbandoned(Rar, 12, 3_000_000_000));

        // A sweep five minutes later sees it has grown.
        Assert.Equal(TransientAbandonLedger.AbandonState.Revived,
            ledger.Evaluate(Rar, 19, 4_800_000_000));

        // It stalls again mid-copy and is abandoned a second time...
        ledger.Abandon(Rar, 19, 4_800_000_000);
        Assert.True(ledger.IsAbandoned(Rar, 19, 4_800_000_000));

        // ...then finally lands complete, and revives for the run that succeeds.
        Assert.Equal(TransientAbandonLedger.AbandonState.Revived,
            ledger.Evaluate(Rar, 40, 9_900_000_000));
    }

    [Fact]
    public void Forget_clears_the_verdict()
    {
        var ledger = new TransientAbandonLedger();
        ledger.Abandon(Rar, 3, 100);
        ledger.Forget(Rar);

        Assert.False(ledger.IsAbandoned(Rar, 3, 100));
    }

    /// <summary>
    /// The sweep needs to know which paths to re-examine without walking every watch folder.
    /// </summary>
    [Fact]
    public void AbandonedPaths_are_enumerable_for_the_periodic_sweep()
    {
        var ledger = new TransientAbandonLedger();
        ledger.Abandon(Rar, 3, 100);
        ledger.Abandon(@"E:\movies\Other\other.rar", 1, 50);

        Assert.Equal(2, ledger.AbandonedPaths().Count);
        Assert.Contains(Rar, ledger.AbandonedPaths());
    }
}
