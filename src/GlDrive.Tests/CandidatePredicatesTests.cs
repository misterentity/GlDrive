using System;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Unit coverage for the spread scheduler's skip-rule policy (extracted from
/// FindBestTransfer in v3.6 Phase 3b). Locks the cbftp-derived retry caps and the
/// backoff / dirscript / sfv-first / slots matching rules so a future edit can't
/// silently change scheduling behavior.
/// </summary>
public class CandidatePredicatesTests
{
    [Theory]
    [InlineData("Show.S01E01-GROUP", "GROUP", false, false, true)]
    [InlineData("Show.S01E01-group", "GROUP", false, false, true)]
    [InlineData("Narco.Menomanites.S01E01-OTHER", "NOMA", false, false, false)]
    [InlineData("Show.S01E01-OTHER", "GROUP", true, false, true)]
    [InlineData("Show.S01E01-OTHER", "GROUP", false, true, true)]
    [InlineData("Show.S01E01-OTHER", "GROUP", false, false, false)]
    public void CanReceiveRelease_applies_destination_only_exclusions(
        string release, string affil, bool downloadOnly, bool blacklisted, bool excluded)
        => Assert.Equal(!excluded, CandidatePredicates.CanReceiveRelease(
            release, new[] { affil }, downloadOnly, blacklisted));

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]   // cap boundary
    [InlineData(9, true)]
    public void PairRetryCapped_at_4(int fails, bool expected)
        => Assert.Equal(expected, CandidatePredicates.PairRetryCapped(fails));

    [Theory]
    [InlineData(0, false)]
    [InlineData(6, false)]
    [InlineData(7, true)]   // cap boundary
    [InlineData(12, true)]
    public void FileRetryCapped_at_7(int fails, bool expected)
        => Assert.Equal(expected, CandidatePredicates.FileRetryCapped(fails));

    [Fact]
    public void DestInBackoff_respects_window_and_dropped_sentinel()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(CandidatePredicates.DestInBackoff(null, now));               // no backoff
        Assert.True(CandidatePredicates.DestInBackoff(now.AddSeconds(30), now));  // parked, future
        Assert.False(CandidatePredicates.DestInBackoff(now.AddSeconds(-1), now)); // expired
        Assert.True(CandidatePredicates.DestInBackoff(DateTime.MaxValue, now));   // dropped for race
    }

    [Fact]
    public void DirscriptBlocked_matches_prefix_case_insensitive()
    {
        var denied = new[] { "/incoming/tv-hd/" };
        Assert.True(CandidatePredicates.DirscriptBlocked("/incoming/TV-HD/Some.Release", denied));
        Assert.False(CandidatePredicates.DirscriptBlocked("/incoming/mp3/Some.Release", denied));
        Assert.False(CandidatePredicates.DirscriptBlocked("/anything", null));
        Assert.False(CandidatePredicates.DirscriptBlocked("/anything", Array.Empty<string>()));
    }

    [Theory]
    [InlineData("release.r01", true, true)]    // needs sfv, not sfv/nfo → blocked
    [InlineData("release.sfv", true, false)]   // the sfv itself passes
    [InlineData("release.nfo", true, false)]   // nfo passes
    [InlineData("release.r01", false, false)]  // dest already has sfv → not blocked
    public void SfvFirstBlocked(string file, bool needsSfv, bool expected)
        => Assert.Equal(expected, CandidatePredicates.SfvFirstBlocked(file, needsSfv));

    [Theory]
    [InlineData(0, 3, 0, 3, false)]  // room on both
    [InlineData(3, 3, 0, 3, true)]   // dest full
    [InlineData(0, 3, 3, 3, true)]   // source full
    [InlineData(2, 3, 2, 3, false)]  // both under
    public void SlotsFull(int dstActive, int dstMax, int srcActive, int srcMax, bool expected)
        => Assert.Equal(expected, CandidatePredicates.SlotsFull(dstActive, dstMax, srcActive, srcMax));

    // ---- v3.10.42: bounded fill-only dir-confirmed override ----
    // Regression cover for the 2026-07-27 MLB loop: superbnc was fill-only for
    // [tv-sports] (MKD path-denied), a scan confirmed the release dir, the dir
    // was then removed site-side, and the stale confirmation permanently
    // overrode the dirscript gate — 278 MKD 550s in 29 minutes on one release.

    [Fact]
    public void DirscriptBlockedAfterOverride_unconfirmed_dest_stays_blocked()
    {
        var denied = new[] { "/incoming/tv-sports/" };
        Assert.True(CandidatePredicates.DirscriptBlockedAfterOverride(
            "/incoming/tv-sports/MLB.Release", denied, dirConfirmed: false, mkdDenialCount: 0));
    }

    [Fact]
    public void DirscriptBlockedAfterOverride_confirmed_dir_opens_fill_only_dest()
    {
        // The feature this override exists for: another racer created the dir,
        // so CWD succeeds and a fill-only dest can receive without any MKD.
        var denied = new[] { "/incoming/tv-sports/" };
        Assert.False(CandidatePredicates.DirscriptBlockedAfterOverride(
            "/incoming/tv-sports/MLB.Release", denied, dirConfirmed: true, mkdDenialCount: 0));
    }

    [Fact]
    public void DirscriptBlockedAfterOverride_undenied_path_never_blocked()
    {
        Assert.False(CandidatePredicates.DirscriptBlockedAfterOverride(
            "/incoming/mp3/Some.Release", new[] { "/incoming/tv-sports/" },
            dirConfirmed: false, mkdDenialCount: 99));
    }

    [Fact]
    public void DirscriptBlockedAfterOverride_stops_trusting_confirmation_after_repeated_mkd_denials()
    {
        // The loop-breaker: the dir-confirmed override is evidence, not a
        // permanent exemption. Once the dest has actually denied MKD on this
        // path enough times, the confirmation is stale — block regardless.
        var denied = new[] { "/incoming/tv-sports/" };
        const string path = "/incoming/tv-sports/MLB.Release";

        for (var n = 0; n < CandidatePredicates.MaxMkdDenialsWithDirConfirmed; n++)
            Assert.False(CandidatePredicates.DirscriptBlockedAfterOverride(
                path, denied, dirConfirmed: true, mkdDenialCount: n));

        Assert.True(CandidatePredicates.DirscriptBlockedAfterOverride(
            path, denied, dirConfirmed: true,
            mkdDenialCount: CandidatePredicates.MaxMkdDenialsWithDirConfirmed));
    }

    [Fact]
    public void DirscriptBlockedAfterOverride_bounds_the_observed_production_loop()
    {
        // 278 consecutive attempts must not all be admitted.
        var denied = new[] { "/incoming/tv-sports/" };
        var admitted = 0;
        for (var attempt = 0; attempt < 278; attempt++)
        {
            if (!CandidatePredicates.DirscriptBlockedAfterOverride(
                    "/incoming/tv-sports/MLB.Release", denied,
                    dirConfirmed: true, mkdDenialCount: admitted))
                admitted++;
        }
        Assert.Equal(CandidatePredicates.MaxMkdDenialsWithDirConfirmed, admitted);
    }
}
