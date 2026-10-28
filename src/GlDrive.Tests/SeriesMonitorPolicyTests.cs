using GlDrive.Config;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class SeriesMonitorPolicyTests
{
    internal static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    internal static WishlistItem Series(SeriesMonitorMode mode = SeriesMonitorMode.All) => new()
    {
        Title = "Example Show", Type = MediaType.TvShow, TvMazeId = 123,
        MonitorMode = mode, MonitorSinceUtc = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
        SeriesInventory =
        [
            new() { Season = 1, Number = 1, AirDate = new(2026, 1, 1), AirAtUtc = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero) },
            new() { Season = 1, Number = 2, AirDate = new(2026, 1, 2), AirAtUtc = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero) },
            new() { Season = 2, Number = 1, AirDate = new(2026, 2, 1), AirAtUtc = new(2026, 2, 1, 12, 0, 0, TimeSpan.Zero) }
        ]
    };
    internal static string Release(string episode) => $"Example.Show.{episode}.1080p.WEB.H264-ONE";
    internal static SearchResult Candidate(string episode, string server = "a") => new()
    {
        ReleaseName = Release(episode), RemotePath = "/tv/" + Release(episode), ServerId = server, Category = "tv"
    };

    [Fact]
    public void LegacyDefaultDoesNotRequireInventoryOrChangeMatching()
    {
        var item = new WishlistItem { Type = MediaType.TvShow };
        Assert.Equal(SeriesMonitorMode.Legacy, item.MonitorMode);
        Assert.True(SeriesMonitorPolicy.Evaluate(item, Release("S01E01"), Now).Accepted);
        Assert.False(SeriesMonitorPolicy.IsMonitored(item));
        item.Type = MediaType.Movie; item.MonitorMode = SeriesMonitorMode.All;
        Assert.True(SeriesMonitorPolicy.Evaluate(item, "Example.Movie.2026.1080p.WEB-ONE", Now).Accepted);
    }

    [Theory]
    [InlineData(SeriesMonitorMode.Future, "S01E01", false)]
    [InlineData(SeriesMonitorMode.Future, "S01E02", true)]
    [InlineData(SeriesMonitorMode.Future, "S02E01", false)]
    [InlineData(SeriesMonitorMode.All, "S01E01", true)]
    [InlineData(SeriesMonitorMode.Missing, "S01E01", true)]
    [InlineData(SeriesMonitorMode.LatestSeason, "S01E01", true)]
    [InlineData(SeriesMonitorMode.All, "S09E01", false)]
    public void ModesAndAiringGateAdmission(SeriesMonitorMode mode, string episode, bool expected)
    {
        Assert.Equal(expected, SeriesMonitorPolicy.Evaluate(Series(mode), Release(episode), Now).Accepted);
    }

    [Fact]
    public void LatestSeasonMovesOnlyOnceTheLaterSeasonHasAired()
    {
        var item = Series(SeriesMonitorMode.LatestSeason);
        var later = Now.AddMonths(2);
        Assert.False(SeriesMonitorPolicy.Evaluate(item, Release("S01E01"), later).Accepted);
        Assert.True(SeriesMonitorPolicy.Evaluate(item, Release("S02E01"), later).Accepted);
    }

    [Fact]
    public void MissingInventoryAndUnknownDatesFailClosed()
    {
        var item = Series(); item.SeriesInventory.Clear();
        Assert.Equal("episode-inventory-unavailable", SeriesMonitorPolicy.Evaluate(item, Release("S01E01"), Now).Reason);
        item = Series(); item.SeriesInventory[0].AirDate = null; item.SeriesInventory[0].AirAtUtc = null;
        Assert.Equal("episode-not-aired", SeriesMonitorPolicy.Evaluate(item, Release("S01E01"), Now).Reason);
    }

    [Fact]
    public void AirdateWithoutTimestampWaitsUntilTheFollowingUtcDay()
    {
        var episode = new SeriesEpisode { AirDate = DateOnly.FromDateTime(Now.UtcDateTime) };
        Assert.False(SeriesMonitorPolicy.HasAired(episode, Now));
        Assert.True(SeriesMonitorPolicy.HasAired(episode, Now.AddDays(1)));
    }

    [Fact]
    public void DateAndNumberAliasesShareOwnedAndPendingState()
    {
        var item = Series();
        Assert.Equal(["S01E01"], SeriesMonitorPolicy.KeysForRelease(item, Release("2026.01.01")));
        item.BestOwned.Add(new() { ReleaseName = Release("2026.01.01"), LocalPath = "owned" });
        Assert.Equal("episode-already-owned", SeriesMonitorPolicy.Evaluate(item, Release("S01E01"), Now).Reason);
        item.BestOwned.Clear(); item.PendingGrabs.Add(new() { ReleaseName = Release("S01E01") });
        Assert.Equal("episode-already-downloading", SeriesMonitorPolicy.Evaluate(item, Release("2026.01.01"), Now).Reason);
    }

    [Fact]
    public void AmbiguousDailyEpisodeDoesNotFabricateOwnershipOfTwoEpisodes()
    {
        var item = Series(); item.SeriesInventory[1].AirDate = item.SeriesInventory[0].AirDate;
        Assert.Empty(SeriesMonitorPolicy.KeysForRelease(item, Release("2026.01.01")));
        Assert.False(SeriesMonitorPolicy.Evaluate(item, Release("2026.01.01"), Now).Accepted);
    }

    [Fact]
    public void StateUsesVerifiedOwnedAndPendingEvidenceBeforeCachedPresence()
    {
        var item = Series(); item.BestOwned.Add(new() { ReleaseName = Release("S01E01"), LocalPath = "owned-path" });
        var states = SeriesMonitorPolicy.GetEpisodes(item, [Candidate("S01E02")], Now);
        Assert.Equal(SeriesEpisodeState.Owned, states[0].State);
        Assert.Equal("owned-path", states[0].OwnedPath);
        Assert.Equal(SeriesEpisodeState.OnSite, states[1].State);
        Assert.Equal(SeriesEpisodeState.NotAired, states[2].State);
        item.PendingGrabs.Add(new() { ReleaseName = Release("S01E02") });
        Assert.Equal(SeriesEpisodeState.Downloading, SeriesMonitorPolicy.GetEpisodes(item, [], Now)[1].State);
        item.PendingGrabs.Clear();
        Assert.Equal(SeriesEpisodeState.Missing, SeriesMonitorPolicy.GetEpisodes(item, [], Now)[1].State);
    }

    [Fact]
    public void PacksNeedAllModeAndEndedSeasonAndCannotRepeatOwnedEpisodes()
    {
        var item = Series();
        Assert.True(SeriesMonitorPolicy.Evaluate(item, Release("S01"), Now).Accepted);
        Assert.False(SeriesMonitorPolicy.Evaluate(item, Release("S02"), Now.AddMonths(2)).Accepted);
        item.MonitorMode = SeriesMonitorMode.Missing;
        Assert.False(SeriesMonitorPolicy.Evaluate(item, Release("S01"), Now).Accepted);
        item.MonitorMode = SeriesMonitorMode.All;
        item.BestOwned.Add(new() { ReleaseName = Release("S01E01") });
        Assert.False(SeriesMonitorPolicy.Evaluate(item, Release("S01"), Now).Accepted);
        Assert.False(SeriesMonitorPolicy.Evaluate(item, Release("S01E01E02"), Now).Accepted);
        Assert.True(SeriesMonitorPolicy.Evaluate(item, Release("S01E02"), Now).Accepted);
    }

    [Fact]
    public void AssemblerPrefersOneSafePackAndOtherwiseChoosesOnlyMissingEpisodes()
    {
        var item = Series(); var config = new DownloadConfig();
        var candidates = new[] { Candidate("S01"), Candidate("S01E01"), Candidate("S01E02", "a"), Candidate("S01E02", "b") };
        var pack = SeriesSeasonAssembler.Plan(item, 1, candidates, config, Now);
        Assert.True(pack.Ready); Assert.Equal(Release("S01"), Assert.Single(pack.Releases).ReleaseName);
        item.BestOwned.Add(new() { ReleaseName = Release("S01E01") });
        var episodes = SeriesSeasonAssembler.Plan(item, 1, candidates, config, Now, site => site == "b" ? 100 : 10);
        Assert.True(episodes.Ready); Assert.Equal("b", Assert.Single(episodes.Releases).ServerId);
        Assert.Equal(Release("S01E02"), episodes.Releases[0].ReleaseName);
    }

    [Fact]
    public void AssemblerReportsUnavailableGapsAndNeverOverlapsMultiEpisodeCopies()
    {
        var item = Series();
        var missing = SeriesSeasonAssembler.Plan(item, 1, [Candidate("S01E01")], new(), Now);
        Assert.False(missing.Ready); Assert.Equal(["S01E02"], missing.MissingEpisodeKeys);
        var multi = SeriesSeasonAssembler.Plan(item, 1,
            [Candidate("S01E01E02"), Candidate("S01E02")], new(), Now);
        Assert.True(multi.Ready); Assert.Single(multi.Releases);
    }

    [Fact]
    public void AssemblerBacktracksToAvailableExactCoverInsteadOfStrandingAnEpisode()
    {
        var item = Series();
        item.SeriesInventory.Add(new() { Season = 1, Number = 3, AirDate = new(2026, 1, 3) });
        var preferredOverlap = Candidate("S01E01E02");
        preferredOverlap.ReleaseName = preferredOverlap.ReleaseName.Replace("WEB", "BluRay");
        preferredOverlap.RemotePath = "/tv/" + preferredOverlap.ReleaseName;
        var plan = SeriesSeasonAssembler.Plan(item, 1,
            [preferredOverlap, Candidate("S01E01"), Candidate("S01E02E03")],
            new DownloadConfig { ScoredQualityEnabled = true }, Now);
        Assert.True(plan.Ready);
        Assert.Equal(2, plan.Releases.Count);
        Assert.Contains(plan.Releases, c => c.ReleaseName == Release("S01E01"));
        Assert.Contains(plan.Releases, c => c.ReleaseName == Release("S01E02E03"));
    }

    [Fact]
    public void AssemblySearchIsBoundedForOversizedSeasons()
    {
        var item = Series();
        item.SeriesInventory = Enumerable.Range(1, 513).Select(number => new SeriesEpisode
        { Season = 1, Number = number, AirDate = new(2026, 1, 1) }).ToList();
        var plan = SeriesSeasonAssembler.Plan(item, 1, [], new(), Now);
        Assert.False(plan.Ready); Assert.Empty(plan.Releases);
        Assert.Equal("assembly-search-budget-exhausted", plan.Reason);
    }

    [Theory]
    [InlineData("/tv/../release")]
    [InlineData("relative/release")]
    [InlineData("/different-release")]
    public void AssemblerRejectsUnsafeOrMismatchedCachedPaths(string path)
    {
        var entry = Candidate("S01E01"); entry.RemotePath = path;
        var plan = SeriesSeasonAssembler.Plan(Series(), 1, [entry, Candidate("S01E02")], new(), Now);
        Assert.False(plan.Ready);
        Assert.Equal(["S01E01"], plan.MissingEpisodeKeys);
        Assert.DoesNotContain(plan.Releases, release => release.ReleaseName == entry.ReleaseName);
    }
}
