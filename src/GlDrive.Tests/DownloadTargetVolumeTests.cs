using System.IO;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the 2026-08-17/18 loss of four wishlist grabs.
///
/// Every Disclosure.Day release matched the wishlist and was enqueued to
/// <c>T:\Movies\Disclosure Day (2026)\…</c>. The box has C, D and E — there is no T. Each item
/// threw <see cref="DirectoryNotFoundException"/>, the generic arm retried it at 30/60/90s,
/// and after three minutes marked it Failed. Four releases, including a COMPLETE BLURAY, gone.
///
/// The retry ladder encodes a belief about how long the condition lasts. Three minutes is a
/// reasonable guess for a flaky socket and a nonsense one for a drive that is not plugged in;
/// and the operator-facing text was the raw "Could not find a part of the path 'T:\Movies\…'",
/// which never says the DRIVE is the missing part — so one configuration fault read as four
/// unrelated per-release failures.
/// </summary>
public sealed class DownloadTargetVolumeTests
{
    /// <summary>
    /// A drive letter that is genuinely not mounted HERE. Probed rather than hardcoded to T:
    /// so the suite asserts the same thing on a machine that happens to have one.
    /// </summary>
    private static readonly string AbsentRoot =
        "ZYXWVUTQ".Select(c => c + @":\").First(r => !Directory.Exists(r));

    private static readonly string AbsentPath =
        Path.Combine(AbsentRoot, "Movies", "Disclosure Day (2026)",
            "Disclosure.Day.2026.COMPLETE.BLURAY-UNTOUCHED");

    /// <summary>A path on a live drive whose leaf directories do not exist.</summary>
    private static readonly string LivePathMissingDirs =
        Path.Combine(Path.GetTempPath(), "gldrive-does-not-exist", "nested", "release");

    [Fact]
    public void An_absent_drive_letter_is_reported_by_its_root()
    {
        Assert.Equal(AbsentRoot, DownloadTargetVolume.MissingVolumeRoot(AbsentPath));
    }

    [Fact]
    public void A_mounted_drive_reports_nothing_missing()
    {
        // A missing intermediate directory on a live drive is ours to create, not a reason to
        // park. Narrowness matters: parking a fault that really is ours would hide it forever.
        Assert.Null(DownloadTargetVolume.MissingVolumeRoot(LivePathMissingDirs));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\path\release")]
    [InlineData(@"\\nas\share\Movies\release")]
    public void Paths_we_cannot_reason_about_keep_ordinary_retry_semantics(string? path)
    {
        // A UNC share being unreachable is a network fault with its own transient character.
        // Only a local drive-letter root qualifies.
        Assert.Null(DownloadTargetVolume.MissingVolumeRoot(path));
    }

    [Fact]
    public void Parking_requires_both_a_path_exception_and_a_genuinely_absent_volume()
    {
        // Both conditions present → park.
        Assert.True(DownloadTargetVolume.IsVolumeAbsent(new DirectoryNotFoundException(), AbsentPath));

        // Right exception, live volume → ordinary retry. DirectoryNotFoundException is also
        // raised for perfectly ordinary missing subdirectories, so the exception alone is not
        // evidence about the drive.
        Assert.False(DownloadTargetVolume.IsVolumeAbsent(new DirectoryNotFoundException(), LivePathMissingDirs));

        // Absent volume, unrelated exception → ordinary retry. A socket failure that happens to
        // occur while a drive is unplugged is still a socket failure.
        Assert.False(DownloadTargetVolume.IsVolumeAbsent(new IOException("connection reset"), AbsentPath));
        Assert.False(DownloadTargetVolume.IsVolumeAbsent(new TimeoutException(), AbsentPath));
    }

    [Fact]
    public void Recheck_backoff_widens_from_five_minutes_to_a_one_hour_ceiling()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), DownloadTargetVolume.RecheckDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(10), DownloadTargetVolume.RecheckDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(20), DownloadTargetVolume.RecheckDelay(3));
        Assert.Equal(TimeSpan.FromMinutes(40), DownloadTargetVolume.RecheckDelay(4));
        Assert.Equal(TimeSpan.FromHours(1), DownloadTargetVolume.RecheckDelay(5));
    }

    [Fact]
    public void The_ceiling_holds_so_a_parked_item_cannot_flood_the_log()
    {
        // A parked item logs one WRN per re-check. Without a ceiling the interval would either
        // grow without bound (never noticing the drive returned) or stay small (12 warnings an
        // hour, forever). One hour bounds it to ~24 lines/day and still recovers promptly.
        foreach (var attempt in new[] { 6, 10, 100, int.MaxValue })
            Assert.Equal(TimeSpan.FromHours(1), DownloadTargetVolume.RecheckDelay(attempt));
    }

    [Fact]
    public void A_nonsensical_attempt_number_still_yields_the_initial_interval()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), DownloadTargetVolume.RecheckDelay(0));
        Assert.Equal(TimeSpan.FromMinutes(5), DownloadTargetVolume.RecheckDelay(-7));
    }
}
