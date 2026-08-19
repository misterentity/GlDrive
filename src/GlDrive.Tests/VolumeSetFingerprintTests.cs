using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the 2026-08-18 extractor hot loop (GAZPROM 2160p UHD, 83 parts / 66 GB).
///
/// Causal chain, all three links reproduced below and in
/// <see cref="TransientAbandonLedgerUnknownFingerprintTests"/>:
///   1. <c>TryDiscoverRarVolumes</c> seeds its result with the caller's first-volume path
///      unconditionally. For old-style <c>.rar/.r00/.r01…</c> sets the <c>.rar</c> sorts AFTER
///      every continuation part, so over FXP it arrives LAST — for most of a 90-minute arrival
///      the list holds 82 real parts and one phantom.
///   2. The fingerprint was <c>volumes.Sum(v =&gt; v.Length)</c>. One unreadable part threw and
///      the entire measurement collapsed into the <c>(-1,-1)</c> "cannot read" sentinel.
///   3. <c>TransientAbandonLedger</c> read "differs from the record" as "the set changed" and
///      unparked the archive — on every evaluation, forever.
///
/// The observable tell was an INVARIANT magnitude: 8 of the 11 revivals that day logged
/// literally <c>changed (-1 parts, -1 bytes)</c>, while the 3 genuine ones read 21 → 53 → 83
/// parts with growing byte counts. A number that never varies across every occurrence is a
/// fixed sentinel, not live data.
/// </summary>
public sealed class VolumeSetFingerprintTests
{
    [Fact]
    public void Unknown_is_not_known()
    {
        Assert.False(VolumeSetFingerprint.Unknown.IsKnown);
        Assert.Equal(-1, VolumeSetFingerprint.Unknown.VolumeCount);
        Assert.Equal(-1, VolumeSetFingerprint.Unknown.TotalBytes);
    }

    [Fact]
    public void An_observed_empty_set_is_known()
    {
        // "Nothing is there" is a real answer and must never be confused with "we could not look".
        Assert.True(VolumeSetFingerprint.Absent.IsKnown);
        Assert.NotEqual(VolumeSetFingerprint.Unknown, VolumeSetFingerprint.Absent);
    }

    [Fact]
    public void A_vanished_part_degrades_the_fingerprint_rather_than_discarding_it()
    {
        // The exact shape that broke: 82 real parts plus the not-yet-arrived .rar. The old
        // Sum(v => v.Length) threw on the phantom and returned the sentinel; the whole set's
        // measurement was lost because one member of it was unreadable.
        var lengths = new List<long?> { null };
        for (var i = 0; i < 82; i++) lengths.Add(100);

        var fp = VolumeSetFingerprint.FromVolumes(lengths);

        Assert.True(fp.IsKnown);
        Assert.Equal(82, fp.VolumeCount);
        Assert.Equal(8200, fp.TotalBytes);
    }

    [Fact]
    public void Fingerprint_grows_monotonically_as_parts_arrive()
    {
        // This is what makes revival work at all: the ledger retries exactly once per genuine
        // change. The three real revivals on 2026-08-18 read 21 → 53 → 83 parts.
        var at21 = VolumeSetFingerprint.FromVolumes(Enumerable.Repeat((long?)1_000, 21));
        var at53 = VolumeSetFingerprint.FromVolumes(Enumerable.Repeat((long?)1_000, 53));
        var at83 = VolumeSetFingerprint.FromVolumes(Enumerable.Repeat((long?)1_000, 83));

        Assert.NotEqual(at21, at53);
        Assert.NotEqual(at53, at83);
        Assert.True(at21.TotalBytes < at53.TotalBytes && at53.TotalBytes < at83.TotalBytes);
    }

    [Fact]
    public void A_set_whose_every_part_is_unreadable_folds_to_Absent_not_Unknown()
    {
        // FromVolumes describes what it managed to observe. It never manufactures Unknown —
        // only a throw at the enumeration level (the directory itself gone) earns that, and
        // that decision belongs to the caller, not to the fold.
        var fp = VolumeSetFingerprint.FromVolumes(new List<long?> { null, null, null });

        Assert.True(fp.IsKnown);
        Assert.Equal(VolumeSetFingerprint.Absent, fp);
    }

    [Fact]
    public void Negative_lengths_are_ignored_rather_than_corrupting_the_total()
    {
        // Defence in depth: nothing should hand us a negative length, but folding one in would
        // reintroduce a "known" fingerprint that is really a sentinel in disguise.
        var fp = VolumeSetFingerprint.FromVolumes(new List<long?> { 500, -1, 500 });

        Assert.True(fp.IsKnown);
        Assert.Equal(2, fp.VolumeCount);
        Assert.Equal(1000, fp.TotalBytes);
    }
}
