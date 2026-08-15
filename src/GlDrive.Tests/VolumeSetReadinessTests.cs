using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the 2026-08-14 watch-folder defect: extraction was gated on the
/// first volume only, so a set whose remaining parts were still downloading reached
/// SharpCompress. That produced locked-volume IOExceptions, and — worse — two failure
/// messages ("unpacked file size does not match header", "UnRAR.exe failed (exit 3)") that
/// the classifier legitimately rules Permanent, issued against sets that were merely
/// incomplete at the time.
///
/// The gate must therefore hold until nothing about the SET is moving: no locked members,
/// and neither the part count nor the byte total changing between samples.
/// </summary>
public sealed class VolumeSetReadinessTests
{
    private static VolumeSetReadiness.Snapshot Snap(int count, long bytes, int locked = 0) =>
        new(count, bytes, locked);

    [Fact]
    public void SettledSet_IsReady()
    {
        var previous = Snap(31, 5_000_000_000);
        var current = Snap(31, 5_000_000_000);

        Assert.True(VolumeSetReadiness.IsReady(previous, current));
        Assert.False(VolumeSetReadiness.IsStillArriving(previous, current));
    }

    [Fact]
    public void LockedVolume_IsNotReady()
    {
        // The verbatim shape of the logged failure: the .rar had settled, .r22 had not.
        var previous = Snap(31, 5_000_000_000, locked: 1);
        var current = Snap(31, 5_000_000_000, locked: 1);

        Assert.False(VolumeSetReadiness.IsReady(previous, current));
        Assert.True(VolumeSetReadiness.IsStillArriving(previous, current));
    }

    [Fact]
    public void GrowingByteCount_IsNotReady()
    {
        var previous = Snap(31, 1_994_329_334);
        var current = Snap(31, 3_100_000_000);

        Assert.False(VolumeSetReadiness.IsReady(previous, current));
        Assert.True(VolumeSetReadiness.IsStillArriving(previous, current));
    }

    /// <summary>
    /// The check the byte total alone cannot make. Parts 1-5 can be complete and unlocked
    /// while parts 6-30 have not started arriving — byte-stable between two close samples,
    /// but emphatically not a finished set. This is the sampling window that produced
    /// "expected 16924333715 found 1994329334" against a live download.
    /// </summary>
    [Fact]
    public void GrowingVolumeCount_IsNotReady_EvenWhenBytesMatch()
    {
        var previous = Snap(5, 1_994_329_334);
        var current = Snap(6, 1_994_329_334);

        Assert.False(VolumeSetReadiness.IsReady(previous, current));
        Assert.True(VolumeSetReadiness.IsStillArriving(previous, current));
    }

    [Fact]
    public void EmptySet_IsNeverReady()
    {
        // Discovery racing the first write, or an unreadable directory. Treating this as
        // settled would hand SharpCompress an archive with no parts.
        Assert.False(VolumeSetReadiness.IsReady(Snap(0, 0), Snap(0, 0)));
    }

    [Fact]
    public void SetThatFinishesArriving_BecomesReadyOnTheFollowingSample()
    {
        var arriving = Snap(31, 4_000_000_000, locked: 1);
        var landed = Snap(31, 5_000_000_000);

        // The sample where the last part completes still differs from its predecessor,
        // so the gate waits one more cycle rather than racing the final write.
        Assert.False(VolumeSetReadiness.IsReady(arriving, landed));

        Assert.True(VolumeSetReadiness.IsReady(landed, landed));
    }

    /// <summary>
    /// A single-volume archive reduces to the original first-file behaviour: one part,
    /// unlocked, stable. The set gate must not make the common case stricter.
    /// </summary>
    [Fact]
    public void SingleVolume_IsReadyWhenStable()
    {
        var stable = Snap(1, 700_000_000);
        Assert.True(VolumeSetReadiness.IsReady(stable, stable));
    }
}
