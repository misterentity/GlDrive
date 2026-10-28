using FluentFTP;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class WantedIndexSweepTests
{
    private const string Web = "Example.Movie.2026.1080p.WEB.H264-GROUP";
    private const string Bluray = "Example.Movie.2026.1080p.BluRay.H264-GROUP";
    private static readonly DateTime Now = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);
    private static WishlistItem Movie() => new() { Title = "Example Movie", Year = 2026 };
    private static WishlistStore Store(params WishlistItem[] items)
    {
        var store = new WishlistStore(() => null, _ => { });
        foreach (var item in items) store.Add(item);
        return store;
    }
    private static SearchIndexEntry Entry(string release, string? path = null) =>
        new(release, path ?? "/archive/" + release, "movies", 4096, Now);
    private static SearchIndexSnapshot Snapshot(params SearchIndexEntry[] entries) => new(entries, Now, false);

    [Fact]
    public void ScoredSweepChoosesBestVariantEvenWhenItAppearsAfterTheCap()
    {
        var result = WantedIndexSweep.Find(Snapshot(Entry(Web), Entry(Bluray)), Store(Movie()),
            new() { ScoredQualityEnabled = true }, 1);
        var offer = Assert.Single(result);
        Assert.Equal(Bluray, offer.ReleaseName);
        Assert.False(offer.Complete);
        Assert.Null(offer.SizeBytes);
        Assert.Equal(Now, offer.IndexBuiltAtUtc);
    }

    [Fact]
    public void SweepRetainsRunnerUpForLaterCompletionEvidence()
    {
        var result = WantedIndexSweep.Find(Snapshot(Entry(Web), Entry(Bluray)), Store(Movie()),
            new() { ScoredQualityEnabled = true });
        Assert.Equal(new[] { Bluray, Web }, result.Select(candidate => candidate.ReleaseName));
        Assert.All(result, candidate => Assert.False(candidate.Complete));
    }

    [Fact]
    public void LegacyQualityFilterIsUsedWhenScoringIsDisabled()
    {
        var result = WantedIndexSweep.Find(Snapshot(Entry(Web),
            Entry("Example.Movie.2026.2160p.BluRay.H265-GROUP")), Store(Movie()), new());
        Assert.Equal(Web, Assert.Single(result).ReleaseName);
    }

    [Fact]
    public void ScoredOwnedAndPendingCopiesUseTheExistingAcquisitionPolicy()
    {
        var movie = Movie();
        var store = Store(movie);
        var config = new DownloadConfig { ScoredQualityEnabled = true };
        movie.PendingGrabs.Add(new() { Key = WishlistAcquisition.Identity(movie), ReleaseName = Bluray });
        Assert.Empty(WantedIndexSweep.Find(Snapshot(Entry(Web), Entry(Bluray)), store, config));
        movie.BestOwned = movie.PendingGrabs;
        movie.PendingGrabs = [];
        Assert.Empty(WantedIndexSweep.Find(Snapshot(Entry(Web), Entry(Bluray)), store, config));
    }

    [Theory]
    [InlineData(WishlistStatus.Paused, false)]
    [InlineData(WishlistStatus.Completed, false)]
    [InlineData(WishlistStatus.Watching, true)]
    public void InactiveItemsAreNotOffered(WishlistStatus status, bool dead)
    {
        var item = Movie();
        item.Status = status;
        item.Dead = dead;
        Assert.Empty(WantedIndexSweep.Find(Snapshot(Entry(Web)), Store(item), new()));
    }

    [Fact]
    public void LegacyGrabbedReleaseIsNotOfferedAgain()
    {
        var item = Movie();
        item.GrabbedReleases.Add(Web);
        Assert.Empty(WantedIndexSweep.Find(Snapshot(Entry(Web)), Store(item), new()));
    }

    [Theory]
    [InlineData(".Example.Movie.2026.1080p.WEB-GROUP", null)]
    [InlineData("-MISSING-Example.Movie.2026.1080p.WEB-GROUP", null)]
    [InlineData("Example Movie", null)]
    [InlineData(Web, "/archive/../" + Web)]
    [InlineData(Web, "relative/" + Web)]
    [InlineData(Web, "/archive/different-name")]
    [InlineData(Web, "/archive/\r\n" + Web)]
    public void UnsafeOrNonReleaseEntriesCannotBecomeOffers(string name, string? path)
    {
        Assert.Empty(WantedIndexSweep.Find(Snapshot(Entry(name, path)), Store(Movie()), new()));
    }

    [Fact]
    public void MissingCacheIsEmptyAndPartialCacheRemainsAnUnknownCompletenessOffer()
    {
        var store = Store(Movie());
        Assert.Empty(WantedIndexSweep.Find(new([Entry(Web)], null, true), store, new()));
        var result = WantedIndexSweep.Find(new([Entry(Web)], Now, true), store, new());
        Assert.False(Assert.Single(result).Complete);
    }

    [Fact]
    public void CapIsSharedFairlyBetweenWantedTitles()
    {
        var show = new WishlistItem { Title = "Example Show", Type = MediaType.TvShow, AddedAt = Now.AddDays(-1) };
        var movie = Movie();
        var entries = Enumerable.Range(1, 20).Select(i => Entry($"Example.Show.S01E{i:D2}.1080p.WEB-GROUP"))
            .Append(Entry(Web)).ToArray();
        var result = WantedIndexSweep.Find(Snapshot(entries), Store(show, movie), new(), 2);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, candidate => candidate.WishlistItemId == movie.Id);
        Assert.Contains(result, candidate => candidate.WishlistItemId == show.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonpositiveCapReturnsNoOffers(int cap) =>
        Assert.Empty(WantedIndexSweep.Find(Snapshot(Entry(Web)), Store(Movie()), new(), cap));

    [Fact]
    public void DuplicatePathsAndVariantsProduceOneOfferPerMovie()
    {
        var result = WantedIndexSweep.Find(Snapshot(Entry(Web), Entry(Web), Entry(Web, "/recent/" + Web)),
            Store(Movie()), new());
        Assert.Equal("/archive/" + Web, Assert.Single(result).RemotePath);
    }

    [Fact]
    public async Task SnapshotPublicationIsDetachedAndReadingNeverRunsFtp()
    {
        await using var pool = new FtpConnectionPool(null!, 1);
        var listCalls = 0;
        var release = Web;
        using var search = new FtpSearchService(pool, new() { SearchPaths = ["/movies"], MaxDepth = 0 },
            (_, _) => { listCalls++; return Task.FromResult(new[] {
                new FtpListItem { Name = release, FullName = "/movies/" + release, Type = FtpObjectType.Directory }
            }); }, (_, _) => throw new InvalidOperationException("No FTP command allowed"));
        Assert.Empty(search.GetCachedIndexSnapshot().Entries);
        Assert.Equal(0, listCalls);
        var published = new List<SearchIndexSnapshot>();
        var readFromOtherThread = false;
        search.IndexPublished += _ => throw new InvalidOperationException("Observer failure is isolated");
        search.IndexPublished += published.Add;
        search.IndexPublished += snapshot =>
        {
            readFromOtherThread = ReferenceEquals(snapshot, Task.Run(search.GetCachedIndexSnapshot)
                .WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
        };
        await search.RefreshIndex();
        var first = Assert.Single(published);
        var firstEntries = Assert.IsAssignableFrom<IList<SearchIndexEntry>>(first.Entries);
        Assert.Throws<NotSupportedException>(() => firstEntries.Clear());
        release = Bluray;
        await search.RefreshIndex();
        Assert.Equal(Web, Assert.Single(first.Entries).ReleaseName);
        Assert.Equal(Bluray, Assert.Single(search.GetCachedIndexSnapshot().Entries).ReleaseName);
        Assert.Equal(2, listCalls);
        Assert.Equal(2, published.Count);
        Assert.True(readFromOtherThread);
        Assert.Same(search.GetCachedIndexSnapshot(), search.GetCachedIndexSnapshot());
    }

    [Fact]
    public async Task FailedRefreshPublishesPartialEvidenceAndCancelledRefreshKeepsPriorGeneration()
    {
        await using var pool = new FtpConnectionPool(null!, 1);
        var fail = false;
        using var search = new FtpSearchService(pool, new() { SearchPaths = ["/movies"], MaxDepth = 0 },
            (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (fail) throw new InvalidOperationException("offline");
                return Task.FromResult(new[] { new FtpListItem { Name = Web, FullName = "/movies/" + Web,
                    Type = FtpObjectType.Directory } });
            }, (_, _) => throw new InvalidOperationException("No FTP command allowed"));
        var events = 0;
        search.IndexPublished += _ => events++;
        await search.RefreshIndex();
        fail = true;
        await search.RefreshIndex();
        var partial = search.GetCachedIndexSnapshot();
        Assert.True(partial.IsPartial);
        Assert.Equal(Web, Assert.Single(partial.Entries).ReleaseName);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.RefreshIndex(ct: new(true)));
        Assert.Same(partial, search.GetCachedIndexSnapshot());
        Assert.Equal(2, events);
    }
}
