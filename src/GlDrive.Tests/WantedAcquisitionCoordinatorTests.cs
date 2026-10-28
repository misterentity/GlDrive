using System.IO;
using GlDrive.Config;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class WantedAcquisitionCoordinatorTests
{
    private const string Web = "Example.Movie.2026.1080p.WEB.H264-ONE";
    private const string Bluray = "Example.Movie.2026.1080p.BluRay.H264-TWO";
    private sealed class Fixture
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public readonly DownloadConfig Config = new()
        {
            WantedDecisionWindowEnabled = true, WantedDecisionWindowMinutes = 15,
            WantedCandidateMaxAgeHours = 24, ScoredQualityEnabled = true,
            LocalPath = Path.Combine(Path.GetTempPath(), "wanted-decision-tests")
        };
        public readonly WishlistStore Store = new(() => null, _ => { });
        public readonly WishlistItem Item;
        public readonly WantedAcquisitionCoordinator Coordinator;
        public readonly List<DownloadItem> Downloads = [];
        public readonly WishlistMatcher A;
        public readonly WishlistMatcher B;

        public Fixture(bool tv = false)
        {
            Item = new() { Title = tv ? "Example Show" : "Example Movie", Year = tv ? null : 2026,
                Type = tv ? MediaType.TvShow : MediaType.Movie, ProfileId = "scored-q1080p" };
            Store.Add(Item);
            Coordinator = new(Store, Config, () => Now);
            A = Matcher("a"); B = Matcher("b");
        }
        private WishlistMatcher Matcher(string server)
        {
            var matcher = new WishlistMatcher(Store, download => { Downloads.Add(download); return true; },
                Config, server, server);
            matcher.AttachWantedCoordinator(Coordinator);
            return matcher;
        }
        public void Offer(string release = Web, bool complete = true, WishlistMatcher? matcher = null,
            DateTimeOffset? completedAt = null) => Coordinator.Offer(matcher ?? A, "video", release, "/video/" + release,
                complete ? WantedReadiness.TrustedComplete : WantedReadiness.Unknown,
                fromIrc: true, sizeBytes: 1024, completeAtUtc: completedAt);
        public void Close() { Now = Now.AddMinutes(15); Coordinator.ProcessDue(); }
    }

    [Fact]
    public void BestCompleteVariantAcrossServersWaitsForWholeWindow()
    {
        var f = new Fixture();
        f.Offer();
        f.Now = f.Now.AddMinutes(10);
        f.Offer(Bluray, matcher: f.B);
        Assert.Equal(0, f.Coordinator.ProcessDue());
        f.Now = f.Now.AddMinutes(5);
        Assert.Equal(1, f.Coordinator.ProcessDue());
        var download = Assert.Single(f.Downloads);
        Assert.Equal(Bluray, download.ReleaseName);
        Assert.Equal("b", download.ServerId);
        Assert.Single(f.Item.PendingGrabs);
    }

    [Fact]
    public void HigherScoreUnknownCandidateCannotBeatCompleteOne()
    {
        var f = new Fixture(); f.Offer(); f.Offer(Bluray, complete: false);
        f.Close(); Assert.Equal(Web, Assert.Single(f.Downloads).ReleaseName);
    }

    [Fact]
    public void RecentAndIndexObservationsNeverFabricateCompletion()
    {
        var f = new Fixture();
        f.A.OnNewRelease("video", Web, "/video/" + Web);
        f.Close();
        Assert.Empty(f.Downloads);
        Assert.Equal("waiting-for-completion", Assert.Single(f.Coordinator.Snapshot()).State);
        f.Offer();
        Assert.Equal(1, f.Coordinator.ProcessDue());
    }

    [Fact]
    public void RepeatsDoNotExtendWindowAndCompleteOnlyCanOpenOne()
    {
        var f = new Fixture(); f.Offer();
        var first = Assert.Single(f.Coordinator.Snapshot());
        f.Now = f.Now.AddMinutes(14); f.Offer();
        Assert.Equal(first.DecideAtUtc, Assert.Single(f.Coordinator.Snapshot()).DecideAtUtc);
        f.Now = f.Now.AddMinutes(1); f.Coordinator.ProcessDue();
        Assert.Single(f.Downloads);
    }

    [Fact]
    public void InvalidationRequiresANewCompletion()
    {
        var f = new Fixture(); f.Offer();
        f.Coordinator.Invalidate("a", Web);
        f.Close(); Assert.Empty(f.Downloads);
        f.Offer(complete: false); f.Coordinator.ProcessDue(); Assert.Empty(f.Downloads);
        f.Offer(); f.Coordinator.ProcessDue(); Assert.Single(f.Downloads);
    }

    [Fact]
    public void ExternalFactRevocationIsCheckedAgainAtDispatch()
    {
        var f = new Fixture(); f.Offer(); var checks = 0;
        // First check ranks the candidate; the second protects final admission.
        f.Coordinator.CompletionStillValid = (_, _) => ++checks == 1;
        f.Close(); Assert.Empty(f.Downloads); Assert.Equal(2, checks);
    }

    [Fact]
    public void InvalidationDuringFinalCheckDoesNotLeakDispatchingCandidate()
    {
        var f = new Fixture(); f.Offer(); var checks = 0;
        f.Coordinator.CompletionStillValid = (_, _) =>
        {
            if (++checks == 2) { f.Coordinator.Invalidate("a", Web); return false; }
            return true;
        };
        f.Close();
        Assert.Empty(f.Downloads);
        Assert.Equal(0, f.Coordinator.DispatchingCount);
    }

    [Theory]
    [InlineData(-7)]
    [InlineData(1)]
    public void StaleOrFutureCompletionCannotAuthorizeADownload(int hours)
    {
        var f = new Fixture(); f.Offer(completedAt: f.Now.AddHours(hours));
        f.Close(); Assert.Empty(f.Downloads);
    }

    [Fact]
    public void CandidateExpiryIsFixedDespiteRepeatedUnknownObservations()
    {
        var f = new Fixture(); f.Offer(complete: false);
        f.Now = f.Now.AddHours(23); f.Offer(complete: false);
        f.Now = f.Now.AddHours(2);
        Assert.Empty(f.Coordinator.Snapshot()); Assert.Empty(f.Downloads);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("removed")]
    [InlineData("dead")]
    [InlineData("disabled")]
    [InlineData("auto-disabled")]
    [InlineData("quality")]
    public void LiveChangesAreRevalidatedBeforeAdmission(string change)
    {
        var f = new Fixture(); f.Offer();
        switch (change)
        {
            case "paused": f.Item.Status = WishlistStatus.Paused; break;
            case "removed": f.Store.Remove(f.Item.Id); break;
            case "dead": f.Item.Dead = true; break;
            case "disabled": f.Config.WantedDecisionWindowEnabled = false; break;
            case "auto-disabled": f.Config.AutoDownloadWishlist = false; break;
            case "quality": f.Item.ProfileId = "scored-q2160p"; break;
        }
        f.Close(); Assert.Empty(f.Downloads);
    }

    [Fact]
    public void DisabledSettingPreservesLegacyNewBehaviorAndIgnoresCompletion()
    {
        var f = new Fixture(); f.Config.WantedDecisionWindowEnabled = false;
        f.A.OnIrcCompletion(new ServerConfig(), "video", Bluray);
        Assert.Empty(f.Downloads);
        f.A.OnNewRelease("video", Web, "/video/" + Web, fromIrc: true);
        Assert.Equal(Web, Assert.Single(f.Downloads).ReleaseName);
    }

    [Fact]
    public void RemovedOrReplacedSourceCannotBeDispatched()
    {
        var f = new Fixture(); f.Offer();
        f.Coordinator.RemoveServer("a");
        f.Offer(); f.Close();
        Assert.Empty(f.Downloads); Assert.Empty(f.Coordinator.Snapshot());
        // A newly attached instance can accept fresh evidence for the same ID.
        var replacement = new WishlistMatcher(f.Store, d => { f.Downloads.Add(d); return true; }, f.Config, "a", "a");
        replacement.AttachWantedCoordinator(f.Coordinator);
        f.Offer(matcher: replacement); f.Close(); Assert.Single(f.Downloads);
    }

    [Fact]
    public void DeactivatedMatcherCannotDispatchAlreadySelectedWindow()
    {
        var f = new Fixture(); f.Offer();
        f.Coordinator.CompletionStillValid = (_, _) => { f.A.Deactivate(); return true; };
        f.Close(); Assert.Empty(f.Downloads);
    }

    [Fact]
    public void DispatchOnlyTouchesSelectedWishlistItem()
    {
        var f = new Fixture(); f.Config.ScoredQualityEnabled = false;
        f.Offer();
        // This item did not participate in the window and must not be grabbed
        // by looping through the matcher again at decision time.
        var later = new WishlistItem { Title = "Example Movie", Year = 2026, Type = MediaType.Movie };
        f.Store.Add(later); f.Close();
        Assert.Single(f.Downloads); Assert.Empty(later.GrabbedReleases);
    }

    [Fact]
    public void LegacyModeDoesNotEnableScorerAndOnlyGrabsOneVariantPerIdentity()
    {
        var f = new Fixture(); f.Config.ScoredQualityEnabled = false;
        f.Offer(); f.Now = f.Now.AddMinutes(1); f.Offer(Bluray); f.Close();
        Assert.Equal(Web, Assert.Single(f.Downloads).ReleaseName);
        f.Offer(Bluray); f.Close(); Assert.Single(f.Downloads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MultiEpisodeCandidatesDoNotGrabOverlappingEpisodesTwice(bool scored)
    {
        var f = new Fixture(tv: true); f.Config.ScoredQualityEnabled = scored;
        f.Offer("Example.Show.S01E01E02.1080p.WEB.H264-ONE");
        f.Now = f.Now.AddMinutes(1);
        f.Offer("Example.Show.S01E02.1080p.WEB.H264-TWO");
        f.Offer("Example.Show.S01E03.1080p.WEB.H264-ONE");
        f.Close();
        Assert.Equal(2, f.Downloads.Count);
        Assert.Contains(f.Downloads, d => d.ReleaseName.Contains("S01E01E02"));
        Assert.Contains(f.Downloads, d => d.ReleaseName.Contains("S01E03"));
    }

    [Theory]
    [InlineData("..Example.Movie.2026.1080p.WEB.H264-ONE", "/video/release")]
    [InlineData(Web, "/video/../release")]
    [InlineData(Web, "relative/release")]
    [InlineData("words without release syntax", "/video/release")]
    public void UnsafeCandidatesDoNotEnterWindows(string release, string path)
    {
        var f = new Fixture();
        f.Coordinator.Offer(f.A, "video", release, path, WantedReadiness.TrustedComplete);
        Assert.Empty(f.Coordinator.Snapshot());
    }

    [Fact]
    public void CandidateLimitPreservesExistingWindow()
    {
        var f = new Fixture();
        for (var i = 0; i < WantedAcquisitionCoordinator.MaximumCandidatesPerWindow + 5; i++)
            f.Offer($"Example.Movie.2026.1080p.WEB.H264-G{i:D3}");
        Assert.Equal(WantedAcquisitionCoordinator.MaximumCandidatesPerWindow,
            Assert.Single(f.Coordinator.Snapshot()).Candidates);
    }
}
