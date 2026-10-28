using System.IO;
using GlDrive.Config;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class SeriesAcquisitionIntegrationTests
{
    private static DownloadConfig Config() => new()
    {
        ScoredQualityEnabled = false, LocalPath = Path.Combine(Path.GetTempPath(), "series-acquisition-tests")
    };
    private static WishlistStore Store(WishlistItem item)
    {
        var store = new WishlistStore(() => null, _ => { }); store.Add(item); return store;
    }

    [Fact]
    public void MonitoredSeriesTracksClaimsAndOwnedSuccessWithGlobalScoringDisabled()
    {
        var item = SeriesMonitorPolicyTests.Series(); var store = Store(item); var config = Config();
        var downloads = new List<DownloadItem>();
        var matcher = new WishlistMatcher(store, d => { downloads.Add(d); return true; }, config, "a", "A");
        var release = SeriesMonitorPolicyTests.Release("S01E01");
        matcher.OnNewRelease("tv", release, "/tv/" + release);
        var download = Assert.Single(downloads);
        Assert.True(download.BackgroundWantedPull);
        Assert.Single(item.PendingGrabs); Assert.Empty(item.BestOwned);
        download.Status = DownloadStatus.Completed; store.ObserveDownloadStatus(download, config);
        Assert.Empty(item.PendingGrabs); Assert.Single(item.BestOwned);
        Assert.Equal(SeriesEpisodeState.Owned, SeriesMonitorPolicy.GetEpisodes(item, [], DateTimeOffset.UtcNow)[0].State);
        Assert.Equal(WishlistStatus.Watching, item.Status);
    }

    [Theory]
    [InlineData(DownloadStatus.Failed)]
    [InlineData(DownloadStatus.Cancelled)]
    public void FailedMonitoredEpisodeReleasesClaimForAlternativeAlias(DownloadStatus terminal)
    {
        var item = SeriesMonitorPolicyTests.Series(); var store = Store(item); var config = Config();
        var downloads = new List<DownloadItem>();
        var matcher = new WishlistMatcher(store, d => { downloads.Add(d); return true; }, config, "a", "A");
        var numbered = SeriesMonitorPolicyTests.Release("S01E01");
        var dated = SeriesMonitorPolicyTests.Release("2026.01.01");
        matcher.OnNewRelease("tv", numbered, "/tv/" + numbered);
        matcher.OnNewRelease("tv", dated, "/tv/" + dated);
        Assert.Single(downloads);
        downloads[0].Status = terminal; store.ObserveDownloadStatus(downloads[0], config);
        Assert.Empty(item.PendingGrabs); Assert.Empty(item.GrabbedReleases);
        matcher.OnNewRelease("tv", dated, "/tv/" + dated);
        Assert.Equal(2, downloads.Count); Assert.Single(item.PendingGrabs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateRowsShareDateNumberAliasEvidence(bool pending)
    {
        var first = SeriesMonitorPolicyTests.Series(); var second = SeriesMonitorPolicyTests.Series();
        second.TvMazeId = null; // Same title still shares evidence when metadata is absent.
        var store = Store(first); store.Add(second);
        var evidence = new WishlistCopy { ReleaseName = SeriesMonitorPolicyTests.Release("S01E01"), DownloadId = "prior" };
        if (pending) first.PendingGrabs.Add(evidence); else first.BestOwned.Add(evidence);
        var decision = store.EvaluateScoredRelease(second.Id, SeriesMonitorPolicyTests.Release("2026.01.01"), Config());
        Assert.False(decision.Accepted);
    }

    [Fact]
    public void LegacySeriesRetainsPreviousUntrackedAdmissionBehavior()
    {
        var item = SeriesMonitorPolicyTests.Series(SeriesMonitorMode.Legacy); var store = Store(item);
        var downloads = new List<DownloadItem>();
        var matcher = new WishlistMatcher(store, d => { downloads.Add(d); return true; }, Config(), "a", "A");
        var release = SeriesMonitorPolicyTests.Release("S01E01");
        matcher.OnNewRelease("tv", release, "/tv/" + release);
        Assert.False(Assert.Single(downloads).BackgroundWantedPull);
        Assert.Empty(item.PendingGrabs); Assert.Single(item.GrabbedReleases);
    }

    [Theory]
    [InlineData(false, "a")]
    [InlineData(true, "b")]
    public void MeasuredSourcePreferenceIsExplicitAndRanksEqualQualityCopies(bool enabled, string expected)
    {
        var item = new WishlistItem { Title = "Example Movie", Type = MediaType.Movie, Year = 2026 };
        var store = Store(item); var config = Config(); config.WantedDecisionWindowEnabled = true;
        config.WantedDecisionWindowMinutes = 1; config.PreferFastestWantedSource = enabled;
        var now = DateTimeOffset.UtcNow;
        var downloads = new List<DownloadItem>();
        var coordinator = new WantedAcquisitionCoordinator(store, config, () => now)
        {
            SourceSpeed = id => id == "b" ? 100 : 10
        };
        var a = new WishlistMatcher(store, d => { downloads.Add(d); return true; }, config, "a", "A");
        var b = new WishlistMatcher(store, d => { downloads.Add(d); return true; }, config, "b", "B");
        a.AttachWantedCoordinator(coordinator); b.AttachWantedCoordinator(coordinator);
        const string release = "Example.Movie.2026.1080p.WEB.H264-ONE";
        coordinator.Offer(a, "movies", release, "/movies/" + release, WantedReadiness.TrustedComplete);
        now = now.AddSeconds(1);
        coordinator.Offer(b, "movies", release, "/movies/" + release, WantedReadiness.TrustedComplete);
        now = now.AddMinutes(1); coordinator.ProcessDue();
        Assert.Equal(expected, Assert.Single(downloads).ServerId);
    }

    [Fact]
    public void RacePullOptOutBeforeDispatchSurvivesLaterUnknownIndexObservation()
    {
        var item = SeriesMonitorPolicyTests.Series(); var store = Store(item); var config = Config();
        config.WantedDecisionWindowEnabled = true; config.WantedDecisionWindowMinutes = 1;
        var now = DateTimeOffset.UtcNow; var allowed = true; var enqueued = 0;
        var coordinator = new WantedAcquisitionCoordinator(store, config, () => now);
        var matcher = new WishlistMatcher(store, _ => { enqueued++; return true; }, config, "a", "A");
        matcher.AttachWantedCoordinator(coordinator);
        var release = SeriesMonitorPolicyTests.Release("S01E01");
        coordinator.Offer(matcher, "tv", release, "/tv/" + release, WantedReadiness.TrustedComplete,
            wantedItemId: item.Id, acquisitionAllowed: () => allowed);
        allowed = false;
        coordinator.Offer(matcher, "tv", release, "/tv/" + release, WantedReadiness.Unknown);
        now = now.AddMinutes(1); coordinator.ProcessDue();
        Assert.Equal(0, enqueued); Assert.Empty(item.PendingGrabs);
    }
}
