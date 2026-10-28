using System.IO;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class WishlistReleaseFlowTests
{
    private const string Release = "Example.Show.S01E02.1080p.WEB.H264-GROUP";

    private static WishlistItem Wanted() => new() { Title = "Example Show", Type = MediaType.TvShow };
    private static ServerConfig Server() => new()
    {
        Id = "site-a", Name = "Example Site",
        Notifications = new() { WatchPath = "/recent", ExcludedCategories = ["tv-hd"] }
    };
    private static DownloadConfig Downloads() => new() { LocalPath = Path.Combine(Path.GetTempPath(), "wishlist-test-target") };
    private static WishlistStore Store(WishlistItem item)
    {
        var store = new WishlistStore(() => null, _ => { });
        store.Add(item);
        return store;
    }

    [Fact]
    public void IrcWantedTitleInExcludedCategoryUsesNormalMatchAndDownloadPath()
    {
        var item = Wanted();
        var store = Store(item);
        var grabs = new List<DownloadItem>();
        var attempts = new List<WishlistAttemptEvent>();
        var matcher = new WishlistMatcher(store, download => { grabs.Add(download); return true; },
            Downloads(), "site-a", "Example Site", attempts.Add);
        matcher.OnIrcRelease(Server(), "tv-hd", Release);

        var download = Assert.Single(grabs);
        Assert.Equal("/recent/tv-hd/" + Release, download.RemotePath);
        Assert.Equal(item.Id, download.WishlistItemId);
        Assert.True(Assert.Single(attempts).Matched);
        Assert.Equal(Release, Assert.Single(item.GrabbedReleases));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IrcAndListingEvaluateAndGrabOnlyOnceInEitherOrder(bool ircFirst)
    {
        var item = Wanted();
        var attempts = 0;
        var grabs = 0;
        var matches = 0;
        var matcher = new WishlistMatcher(Store(item), _ => { grabs++; return true; }, Downloads(), "site-a", "Example Site", _ => attempts++);
        matcher.MatchFound += (_, _, _) => matches++;
        void Irc() => matcher.OnIrcRelease(Server(), "tv-hd", Release);
        void Listing() => matcher.OnNewRelease("TV_HD", Release.ToLowerInvariant(), "/recent/TV_HD/" + Release);
        if (ircFirst) { Irc(); Listing(); } else { Listing(); Irc(); }
        Assert.Equal(1, attempts);
        Assert.Equal(1, grabs);
        Assert.Equal(1, matches);
        Assert.Single(item.GrabbedReleases);
    }

    [Fact]
    public async Task ConcurrentSitesShareClaimWhileEnqueueRunsOutsideStoreLock()
    {
        var item = Wanted();
        var store = Store(item);
        var attempts = 0;
        var grabs = 0;
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var first = new WishlistMatcher(store, _ =>
        {
            Interlocked.Increment(ref grabs);
            entered.Set();
            return finish.Wait(TimeSpan.FromSeconds(10));
        }, Downloads(), "site-a", "Site A", _ => Interlocked.Increment(ref attempts));
        var second = new WishlistMatcher(store, _ => { Interlocked.Increment(ref grabs); return true; },
            Downloads(), "site-b", "Site B", _ => Interlocked.Increment(ref attempts));
        var firstTask = Task.Run(() => first.OnIrcRelease(Server(), "tv-hd", Release));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            // This call completes before first's enqueue: it neither waits under the
            // shared store lock nor gets a second evaluation/queue reservation.
            second.OnNewRelease("tv", Release, "/other/" + Release);
            Assert.Equal(1, attempts);
            Assert.Equal(1, grabs);
        }
        finally { finish.Set(); await firstTask; }
        Assert.Single(item.GrabbedReleases);
    }

    [Fact]
    public void MissIsDeduplicatedUntilWishlistEdit()
    {
        var item = Wanted();
        item.Title = "Different Programme";
        var store = Store(item);
        var attempts = 0;
        var grabs = 0;
        var matcher = new WishlistMatcher(store, _ => { grabs++; return true; }, Downloads(), "site-a", "Site A", _ => attempts++);
        matcher.OnIrcRelease(Server(), "tv-hd", Release);
        matcher.OnNewRelease("tv-hd", Release, "/recent/tv-hd/" + Release);
        Assert.Equal(1, attempts);
        Assert.Equal(0, grabs);
        item.Title = "Example Show";
        store.Update(item);
        matcher.OnNewRelease("tv-hd", Release, "/recent/tv-hd/" + Release);
        Assert.Equal(2, attempts);
        Assert.Equal(1, grabs);
    }

    [Fact]
    public void PersistedGrabPreventsReevaluationAfterRestart()
    {
        string? saved = null;
        var store = new WishlistStore(() => null, json => saved = json);
        store.Add(Wanted());
        new WishlistMatcher(store, _ => true, Downloads(), "site-a", "Site A")
            .OnIrcRelease(Server(), "tv-hd", Release);
        var restarted = new WishlistStore(() => saved, _ => { });
        var attempts = 0;
        var grabs = 0;
        new WishlistMatcher(restarted, _ => { grabs++; return true; }, Downloads(), "site-b", "Site B", _ => attempts++)
            .OnNewRelease("tv-hd", Release, "/recent/tv-hd/" + Release);
        Assert.Equal(0, attempts);
        Assert.Equal(0, grabs);
    }

    [Fact]
    public void RejectedEnqueueReleasesClaimForLaterValidDetection()
    {
        var item = Wanted();
        var calls = 0;
        var matcher = new WishlistMatcher(Store(item), _ => ++calls > 1, Downloads(), "site-a", "Site A");
        matcher.OnIrcRelease(Server(), "tv-hd", Release);
        Assert.Empty(item.GrabbedReleases);
        matcher.OnNewRelease("tv-hd", Release, "/recent/tv-hd/" + Release);
        Assert.Equal(2, calls);
        Assert.Single(item.GrabbedReleases);
    }

    [Theory]
    [InlineData(WishlistStatus.Paused, false)]
    [InlineData(WishlistStatus.Completed, false)]
    [InlineData(WishlistStatus.Watching, true)]
    public void InactiveWishlistItemsAreNotEvaluated(WishlistStatus status, bool dead)
    {
        var item = Wanted();
        item.Status = status;
        item.Dead = dead;
        var attempts = 0;
        var matcher = new WishlistMatcher(Store(item), _ => throw new Exception("Unexpected enqueue"), Downloads(), "site-a", "Site A", _ => attempts++);
        matcher.OnIrcRelease(Server(), "tv-hd", Release);
        Assert.Equal(0, attempts);
        Assert.Empty(item.GrabbedReleases);
    }
}
