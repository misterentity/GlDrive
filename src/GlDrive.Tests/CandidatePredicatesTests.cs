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
}
