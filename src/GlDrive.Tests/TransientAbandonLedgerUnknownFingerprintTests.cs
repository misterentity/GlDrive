using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// The third link of the 2026-08-18 hot loop: an UNREADABLE fingerprint was answered as
/// "the set changed".
///
/// The old code's comment was right about the danger and wrong about the remedy — "an
/// unreadable fingerprint must never hold a path down" is true, but dropping the record and
/// returning Revived does far more than not-hold-it-down. It restarts the full readiness and
/// retry cycle, and since the very next evaluation is unreadable too, it does so on every
/// tick: 70 detect → fail → abandon → revive cycles against one archive in 90 minutes.
///
/// This is the over-correction of "a decision that never expires is a permanent exemption"
/// (v3.10.41, v3.10.42): the fix for a verdict that never lapses is not a verdict that lapses
/// on every evaluation. <see cref="TransientAbandonLedger.AbandonState.Unknown"/> is the third
/// answer that lets a caller do nothing without either extreme.
/// </summary>
public sealed class TransientAbandonLedgerUnknownFingerprintTests
{
    private const string Rar = @"D:\x265\Disclosure.Day.2026.2160p.UHD.BluRay.H265-GAZPROM\d.rar";

    private static readonly int UnknownCount = VolumeSetFingerprint.Unknown.VolumeCount;
    private static readonly long UnknownBytes = VolumeSetFingerprint.Unknown.TotalBytes;

    [Fact]
    public void An_unreadable_fingerprint_reports_Unknown_not_Revived()
    {
        var ledger = new TransientAbandonLedger();
        ledger.Abandon(Rar, 82, 8200);

        Assert.Equal(
            TransientAbandonLedger.AbandonState.Unknown,
            ledger.Evaluate(Rar, UnknownCount, UnknownBytes));
    }

    [Fact]
    public void An_unreadable_fingerprint_does_not_drop_the_record()
    {
        // The heart of the bug. Revived removes the entry, so the NEXT evaluation returned
        // NotAbandoned, the caller ran the whole cycle again, abandoned again, and round it
        // went. Unknown must leave the ledger exactly as it found it.
        var ledger = new TransientAbandonLedger();
        ledger.Abandon(Rar, 82, 8200);

        for (var i = 0; i < 25; i++)
            Assert.Equal(
                TransientAbandonLedger.AbandonState.Unknown,
                ledger.Evaluate(Rar, UnknownCount, UnknownBytes));

        Assert.Contains(Rar, ledger.AbandonedPaths());

        // ...and the record it kept is still the ORIGINAL one, so a real change is still detected.
        Assert.Equal(
            TransientAbandonLedger.AbandonState.StillAbandoned,
            ledger.Evaluate(Rar, 82, 8200));
    }

    [Fact]
    public void Parking_on_Unknown_cannot_strand_a_path()
    {
        // The guarantee that makes returning Unknown safe: whatever the fingerprint was when
        // we gave up, it differs from a later readable one, so revival happens by the ordinary
        // route as soon as the set becomes observable again. No expiry timer needed.
        var ledger = new TransientAbandonLedger();
        ledger.Abandon(Rar, 82, 8200);

        Assert.Equal(
            TransientAbandonLedger.AbandonState.Unknown,
            ledger.Evaluate(Rar, UnknownCount, UnknownBytes));

        Assert.Equal(
            TransientAbandonLedger.AbandonState.Revived,
            ledger.Evaluate(Rar, 83, 9000));
    }

    [Fact]
    public void A_path_abandoned_while_unobservable_revives_once_it_becomes_observable()
    {
        // The inverse ordering: Unknown at abandon time. Recording the sentinel is fine
        // precisely because it can never compare equal to a real measurement.
        var ledger = new TransientAbandonLedger();
        ledger.Abandon(Rar, UnknownCount, UnknownBytes);

        Assert.Equal(
            TransientAbandonLedger.AbandonState.Revived,
            ledger.Evaluate(Rar, 83, 9000));
    }

    [Fact]
    public void IsAbandoned_treats_Unknown_as_stop()
    {
        // "Should I proceed?" — an evaluation that learned nothing is not grounds to proceed.
        // Answering false here let the abandon path recompute "first time" as true on every
        // cycle of the loop and re-log its warning each time.
        var ledger = new TransientAbandonLedger();
        ledger.Abandon(Rar, 82, 8200);

        Assert.True(ledger.IsAbandoned(Rar, UnknownCount, UnknownBytes));
    }

    [Fact]
    public void An_unknown_fingerprint_for_an_untracked_path_is_still_NotAbandoned()
    {
        // Unknown is a qualifier on an existing record, never an opinion about a path we have
        // no record for. A fresh archive must not be parked just because it is mid-write.
        var ledger = new TransientAbandonLedger();

        Assert.Equal(
            TransientAbandonLedger.AbandonState.NotAbandoned,
            ledger.Evaluate(Rar, UnknownCount, UnknownBytes));
    }

    [Fact]
    public void Genuine_growth_still_revives_exactly_once_per_change()
    {
        // Guard against fixing the loop by making the ledger inert. The 2026-08-18 log's three
        // LEGITIMATE revivals (21 → 53 → 83 parts) must still happen.
        var ledger = new TransientAbandonLedger();

        ledger.Abandon(Rar, 21, 15_739_201_510);
        Assert.Equal(TransientAbandonLedger.AbandonState.Revived, ledger.Evaluate(Rar, 53, 42_139_201_510));

        ledger.Abandon(Rar, 53, 42_139_201_510);
        Assert.Equal(TransientAbandonLedger.AbandonState.Revived, ledger.Evaluate(Rar, 83, 66_139_201_510));

        // Unchanged means unchanged — one revival per change, not one per evaluation.
        ledger.Abandon(Rar, 83, 66_139_201_510);
        Assert.Equal(TransientAbandonLedger.AbandonState.StillAbandoned, ledger.Evaluate(Rar, 83, 66_139_201_510));
    }
}
