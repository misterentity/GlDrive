using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class WishlistScoredAcquisitionTests
{
    private const string Web = "Example.Movie.2026.1080p.WEB.H264-ONE";
    private const string OtherWeb = "Example.Movie.2026.1080p.WEB.H264-TWO";
    private static DownloadConfig Config(ScoredQualityProfile? profile = null) => new()
    {
        ScoredQualityEnabled = true, LocalPath = Path.Combine(Path.GetTempPath(), "wishlist-scored-target"),
        Profiles = profile == null ? [] : [profile]
    };
    private static WishlistItem Movie() => new()
    {
        Title = "Example Movie", Year = 2026, Type = MediaType.Movie,
        ProfileId = ScoredQualityProfile.Recommended(QualityProfile.Q1080p).Id
    };
    private static WishlistStore Store(WishlistItem item)
    {
        var store = new WishlistStore(() => null, _ => { });
        store.Add(item);
        return store;
    }
    private static DownloadItem Download(WishlistItem item, string name = Web, string server = "site-a") => new()
    {
        WishlistItemId = item.Id, ReleaseName = name, RemotePath = "/movies/" + name,
        ServerId = server, LocalPath = Path.Combine(Path.GetTempPath(), "wishlist-" + Guid.NewGuid().ToString("N")),
        TotalBytes = 1024
    };

    [Fact]
    public void SevenVariantsBecomeOnePendingGrabAndCompletionWaitsForSuccess()
    {
        var item = Movie();
        var store = Store(item);
        var config = Config();
        var downloads = new List<DownloadItem>();
        var matcher = new WishlistMatcher(store, d => { downloads.Add(d); return true; }, config, "site-a", "Site A");
        string[] variants = [Web, OtherWeb, "Example.Movie.2026.COMPLETE.BLURAY-ONE",
            "Example.Movie.2026.MULTI.COMPLETE.BLURAY-TWO", "Example.Movie.2026.BDRip.x264-ONE",
            "Example.Movie.2026.720p.WEB.H264-ONE", "Example.Movie.2026.PROPER.1080p.WEB.H264-ONE"];
        foreach (var name in variants) matcher.OnNewRelease("movies", name, "/movies/" + name);
        var download = Assert.Single(downloads);
        Assert.Equal(WishlistStatus.Watching, item.Status);
        Assert.Empty(item.BestOwned);
        Assert.Single(item.PendingGrabs);
        download.Status = DownloadStatus.Completed;
        store.ObserveDownloadStatus(download, config);
        Assert.Equal(WishlistStatus.Completed, item.Status);
        Assert.Equal(Web, Assert.Single(item.BestOwned).ReleaseName);
        Assert.Empty(item.PendingGrabs);
    }

    [Fact]
    public async Task DifferentVariantsOnConcurrentServersShareAtomicReservation()
    {
        var item = Movie();
        var store = Store(item);
        using var entered = new ManualResetEventSlim();
        using var exit = new ManualResetEventSlim();
        var count = 0;
        var first = new WishlistMatcher(store, _ => { Interlocked.Increment(ref count); entered.Set(); return exit.Wait(TimeSpan.FromSeconds(5)); },
            Config(), "site-a", "A");
        var second = new WishlistMatcher(store, _ => { Interlocked.Increment(ref count); return true; }, Config(), "site-b", "B");
        var running = Task.Run(() => first.OnNewRelease("movies", Web, "/movies/" + Web));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            second.OnNewRelease("movies", OtherWeb, "/movies/" + OtherWeb);
            Assert.Equal(1, count);
        }
        finally { exit.Set(); await running; }
        Assert.Single(item.PendingGrabs);
    }

    [Theory]
    [InlineData(DownloadStatus.Failed)]
    [InlineData(DownloadStatus.Cancelled)]
    public void TerminalFailureReleasesExactAndRejectedVariantForLaterDetection(DownloadStatus terminal)
    {
        var item = Movie();
        var store = Store(item);
        var downloads = new List<DownloadItem>();
        var config = Config();
        var matcher = new WishlistMatcher(store, d => { downloads.Add(d); return true; }, config, "site-a", "A");
        matcher.OnNewRelease("movies", Web, "/movies/" + Web);
        matcher.OnNewRelease("movies", OtherWeb, "/movies/" + OtherWeb);
        Assert.Single(downloads);
        downloads[0].Status = terminal;
        store.ObserveDownloadStatus(downloads[0], config);
        Assert.Empty(item.PendingGrabs);
        Assert.Empty(item.GrabbedReleases);
        matcher.OnNewRelease("movies", OtherWeb, "/movies/" + OtherWeb);
        Assert.Equal(2, downloads.Count);
        Assert.Empty(item.BestOwned);
    }

    [Theory]
    [InlineData(DownloadStatus.Queued)]
    [InlineData(DownloadStatus.Downloading)]
    [InlineData(DownloadStatus.Paused)]
    [InlineData(DownloadStatus.Extracting)]
    public void NonterminalStateRetainsClaim(DownloadStatus status)
    {
        var item = Movie(); var store = Store(item); var download = Download(item); var config = Config();
        Assert.True(store.TryReserveScoredGrab(item.Id, download, config).Accepted);
        download.Status = status;
        store.ObserveDownloadStatus(download, config);
        Assert.Single(item.PendingGrabs);
        Assert.False(store.EvaluateScoredRelease(item.Id, OtherWeb, config).Accepted);
        Assert.Empty(item.BestOwned);
    }

    [Fact]
    public void EnqueueRejectionRollsBackReservation()
    {
        var item = Movie(); var store = Store(item); var count = 0;
        var matcher = new WishlistMatcher(store, _ => ++count > 1, Config(), "site-a", "A");
        matcher.OnNewRelease("movies", Web, "/movies/" + Web);
        Assert.Empty(item.PendingGrabs);
        matcher.OnNewRelease("movies", Web, "/movies/" + Web);
        Assert.Equal(2, count);
        Assert.Single(item.PendingGrabs);
    }

    [Fact]
    public void LiveReconciliationCannotClearReservationBeforeItsEnqueueFinishes()
    {
        var item = Movie(); var store = Store(item); var config = Config(); var download = Download(item);
        Assert.True(store.TryReserveScoredGrab(item.Id, download, config).Accepted);
        store.ReconcileDownloads("site-a", [], config);
        Assert.Single(item.PendingGrabs);
        Assert.False(store.EvaluateScoredRelease(item.Id, OtherWeb, config).Accepted);
        store.ConfirmScoredGrab(item.Id, download.Id, download.ReleaseName);
        store.ReconcileDownloads("site-a", [], config);
        Assert.Empty(item.PendingGrabs);
    }

    [Fact]
    public void PersistedPendingSurvivesAndMissingClaimReconcilesOnlyAuthoritativeServer()
    {
        string? json = null;
        var item = Movie(); var config = Config();
        var store = new WishlistStore(() => null, value => json = value); store.Add(item);
        var download = Download(item);
        Assert.True(store.TryReserveScoredGrab(item.Id, download, config).Accepted);
        store.ConfirmScoredGrab(item.Id, download.Id, Web);
        var restarted = new WishlistStore(() => json, _ => { });
        Assert.False(restarted.EvaluateScoredRelease(item.Id, OtherWeb, config).Accepted);
        restarted.ReconcileDownloads("site-b", [], config);
        Assert.False(restarted.EvaluateScoredRelease(item.Id, OtherWeb, config).Accepted);
        restarted.ReconcileDownloads("site-a", [], config);
        Assert.True(restarted.EvaluateScoredRelease(item.Id, Web, config).Accepted);
    }

    [Fact]
    public void RestoredCompletedDownloadRecoversOwnedStateAndFailedDownloadFreesClaim()
    {
        var item = Movie(); var store = Store(item); var config = Config(); var download = Download(item);
        Assert.True(store.TryReserveScoredGrab(item.Id, download, config).Accepted);
        download.Status = DownloadStatus.Completed;
        store.ReconcileDownloads("site-a", [download], config);
        Assert.Equal(WishlistStatus.Completed, item.Status);
        Assert.Single(item.BestOwned);
        Assert.Empty(item.PendingGrabs);
        // Repeating reconciliation is idempotent.
        store.ReconcileDownloads("site-a", [download], config);
        Assert.Single(item.BestOwned);
    }

    [Fact]
    public void DuplicateWishlistRowsWithOrWithoutMetadataShareEvidence()
    {
        var item = Movie(); var store = Store(item); var duplicate = Movie(); duplicate.ImdbId = "tt1234567"; store.Add(duplicate);
        Assert.True(store.TryReserveScoredGrab(item.Id, Download(item), Config()).Accepted);
        Assert.False(store.EvaluateScoredRelease(duplicate.Id, OtherWeb, Config()).Accepted);
        item.ImdbId = duplicate.ImdbId; store.Update(item);
        Assert.False(store.EvaluateScoredRelease(item.Id, OtherWeb, Config()).Accepted);
    }

    [Fact]
    public void TelevisionTracksEpisodesAndNeverCompletesWholeShow()
    {
        var item = new WishlistItem { Title = "Example Show", Type = MediaType.TvShow, ProfileId = "scored-q1080p" };
        var store = Store(item); var config = Config();
        var download = Download(item, "Example.Show.S01E01E02.1080p.WEB.H264-ONE");
        Assert.True(store.TryReserveScoredGrab(item.Id, download, config).Accepted);
        Assert.False(store.EvaluateScoredRelease(item.Id, "Example.Show.S01E02.1080p.WEB.H264-TWO", config).Accepted);
        Assert.True(store.EvaluateScoredRelease(item.Id, "Example.Show.S01E03.1080p.WEB.H264-ONE", config).Accepted);
        download.Status = DownloadStatus.Completed; store.ObserveDownloadStatus(download, config);
        Assert.Equal(WishlistStatus.Watching, item.Status);
        Assert.Equal(2, item.BestOwned.Count);
    }

    [Fact]
    public void UpgradingOneEpisodeRetainsOtherEpisodeFromCompletedMultiEpisodeRelease()
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p); profile.CutoffScore = 5000;
        var item = new WishlistItem { Title = "Example Show", Type = MediaType.TvShow, ProfileId = profile.Id };
        var store = Store(item); var config = Config(profile);
        var original = Download(item, "Example.Show.S01E01E02.1080p.WEB.H264-ONE");
        original.Status = DownloadStatus.Completed; store.ObserveDownloadStatus(original, config);
        Assert.Equal(2, item.BestOwned.Count);
        var upgraded = Download(item, "Example.Show.S01E01.1080p.BluRay.H264-ONE");
        upgraded.Status = DownloadStatus.Completed; store.ObserveDownloadStatus(upgraded, config);
        Assert.Equal(2, item.BestOwned.Count);
        Assert.Contains(item.BestOwned, c => c.Key.EndsWith("S01E02") && c.ReleaseName == original.ReleaseName);
        Assert.Contains(item.BestOwned, c => c.Key.EndsWith("S01E01") && c.ReleaseName == upgraded.ReleaseName);
    }

    [Fact]
    public void ProfileAllowsResolutionUpgradeDespiteLegacyQualityAndLateCompletionDoesNotRegress()
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q2160p);
        profile.AllowedResolutions = [QualityProfile.Q1080p, QualityProfile.Q2160p];
        var config = Config(profile); var item = Movie(); item.ProfileId = profile.Id; var store = Store(item);
        var first = Download(item); var better = Download(item, Web.Replace("1080p", "2160p"));
        Assert.True(store.TryReserveScoredGrab(item.Id, first, config).Accepted);
        Assert.True(store.TryReserveScoredGrab(item.Id, better, config).Accepted);
        better.Status = first.Status = DownloadStatus.Completed;
        store.ObserveDownloadStatus(better, config); store.ObserveDownloadStatus(first, config);
        Assert.Equal(better.ReleaseName, Assert.Single(item.BestOwned).ReleaseName);
        Assert.Equal(WishlistStatus.Completed, item.Status);
    }

    [Fact]
    public void CorrectionsAfterCutoffAreExplicitAndRevisionBecomesOwned()
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.UpgradeProperRepackAfterCutoff = true;
        var config = Config(profile); var item = Movie(); var store = Store(item);
        var first = Download(item, Web.Replace(".1080p", ".REPACK.1080p"));
        Assert.True(store.TryReserveScoredGrab(item.Id, first, config).Accepted);
        first.Status = DownloadStatus.Completed; store.ObserveDownloadStatus(first, config);
        Assert.Equal(WishlistStatus.Completed, item.Status);
        var revision = Download(item, Web.Replace(".1080p", ".REPACK2.1080p"));
        Assert.True(store.TryReserveScoredGrab(item.Id, revision, config).Accepted);
        revision.Status = DownloadStatus.Completed; store.ObserveDownloadStatus(revision, config);
        Assert.Equal(revision.ReleaseName, Assert.Single(item.BestOwned).ReleaseName);
        Assert.False(store.EvaluateScoredRelease(item.Id, OtherWeb, config).Accepted);
    }

    [Fact]
    public void LegacyMigrationPreservesQualityAndNeverInventsOwnership()
    {
        var json = JsonSerializer.Serialize(new[] { new { id = "old", title = "Example Movie", year = 2026, quality = 3,
            grabbedReleases = new[] { Web } } });
        var store = new WishlistStore(() => json, _ => { });
        var item = Assert.Single(store.Items);
        Assert.Equal("legacy-q1080p", item.ProfileId);
        Assert.Empty(item.BestOwned);
        Assert.Equal(WishlistStatus.Watching, item.Status);
        Assert.False(store.EvaluateScoredRelease(item.Id, OtherWeb, Config()).Accepted);
    }

    [Fact]
    public void ManualForeignTitleCannotClaimWishlistOwnership()
    {
        var item = Movie(); var store = Store(item); var download = Download(item, "Different.Movie.2026.1080p.WEB.H264-ONE");
        download.Status = DownloadStatus.Completed; store.ObserveDownloadStatus(download, Config());
        Assert.Empty(item.BestOwned);
        Assert.Equal(WishlistStatus.Watching, item.Status);
    }

    [Fact]
    public void ReservationPersistenceFailurePreventsEnqueue()
    {
        var fail = false; var item = Movie();
        var store = new WishlistStore(() => null, _ => { if (fail) throw new IOException("test"); }); store.Add(item); fail = true;
        var calls = 0;
        new WishlistMatcher(store, _ => { calls++; return true; }, Config(), "site-a", "A")
            .OnNewRelease("movies", Web, "/movies/" + Web);
        Assert.Equal(0, calls);
        Assert.Empty(item.PendingGrabs);
    }

    [Fact]
    public void DownloadManagerQueuedCancelRetryAndCompletionDriveWishlistLifecycle()
    {
        var root = Path.Combine(Path.GetTempPath(), "wishlist-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var downloadStore = DownloadStore.ForFile(Path.Combine(root, "downloads.json"));
        try
        {
            var item = Movie(); var wishlist = Store(item); var config = Config();
            using var manager = new DownloadManager(downloadStore, new NeverSource(), config);
            manager.AttachWishlist(wishlist, "site-a");
            var download = Download(item);
            Assert.True(wishlist.TryReserveScoredGrab(item.Id, download, config).Accepted);
            Assert.True(manager.Enqueue(download));
            manager.Cancel(download.Id);
            Assert.Empty(item.PendingGrabs);
            manager.Retry(download.Id);
            Assert.Single(item.PendingGrabs);
            manager.CompleteItem(download, CancellationToken.None);
            Assert.Equal(WishlistStatus.Completed, item.Status);
            Assert.Single(item.BestOwned);
        }
        finally
        {
            downloadStore.Flush();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnablingScoringLiveReconcilesSuccessfulAndFailedLegacyDownloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "wishlist-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var downloadStore = DownloadStore.ForFile(Path.Combine(root, "downloads.json"));
        try
        {
            var item = Movie(); var wishlist = Store(item); var failedItem = Movie();
            failedItem.Title = "Failed Movie"; wishlist.Add(failedItem);
            var config = Config(); config.ScoredQualityEnabled = false;
            using var manager = new DownloadManager(downloadStore, new NeverSource(), config);
            manager.AttachWishlist(wishlist, "site-a");
            var completed = Download(item); completed.Status = DownloadStatus.Completed; downloadStore.Add(completed);
            var failed = Download(failedItem, "Failed.Movie.2026.1080p.WEB.H264-ONE");
            failed.Status = DownloadStatus.Failed; downloadStore.Add(failed);
            wishlist.RecordGrab(failedItem.Id, failed.ReleaseName);
            Assert.Empty(item.BestOwned);
            config.ScoredQualityEnabled = true; manager.RefreshOptions();
            Assert.Equal(WishlistStatus.Completed, item.Status);
            Assert.Single(item.BestOwned);
            Assert.Empty(failedItem.GrabbedReleases);
            Assert.True(wishlist.EvaluateScoredRelease(failedItem.Id, failed.ReleaseName, config).Accepted);
        }
        finally
        {
            downloadStore.Flush(); Directory.Delete(root, recursive: true);
        }
    }

    private sealed class NeverSource : IDownloadSource
    {
        public bool IsInCooldown => false;
        public Task<DownloadTreePlan> PlanAsync(string remotePath, string localPath, CancellationToken ct) => throw new InvalidOperationException("No network in lifecycle test");
        public Task DownloadAsync(IReadOnlyList<FtpDownloadFile> files, IProgress<DownloadProgress>? progress, CancellationToken ct) => throw new InvalidOperationException();
        public Task RefreshManifestAsync(FtpDownloadFile file, CancellationToken ct) => throw new InvalidOperationException();
    }
}
