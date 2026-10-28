using GlDrive.Downloads;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class RacePullPolicyTests
{
    private const string Release = "Example.Show.S01E01.1080p.WEB.H264-GROUP";
    private static WishlistItem Show() => new() { Id = "wanted", Title = "Example Show", Type = MediaType.TvShow };
    private static RaceDetail Race() => new()
    {
        Release = Release, Section = "tv-hd", State = "Completed",
        Sites = [new() { ServerId = "mirror", IsComplete = true, State = nameof(DestState.Complete),
            Path = "/tv/" + Release, RemoteSection = "TV" }],
        Files = [new() { Name = "video.mkv", Size = 1000 }, new() { Name = "release.nfo", Size = 10 }]
    };

    [Fact]
    public void CompletedReceivingSiteCarriesExactPathInventoryAndMatchedShow()
    {
        var result = Assert.Single(RacePullPolicy.Find(Race(), [Show()]));
        Assert.Equal("mirror", result.ServerId);
        Assert.Equal("/tv/" + Release, result.RemotePath);
        Assert.Equal("TV", result.Category);
        Assert.Equal(1010, result.SizeBytes);
        Assert.Equal("wanted", Assert.Single(result.WishlistItemIds));
    }

    [Theory]
    [InlineData(false, "Complete", false)]
    [InlineData(true, "AwaitingCompletion", false)]
    [InlineData(true, "TimedOut", false)]
    [InlineData(true, null, false)]
    [InlineData(true, "Complete", true)]
    public void BothCompletionSignalsAndReceivingRoleAreRequired(bool complete, string? state, bool source)
    {
        var race = Race();
        race.Sites[0].IsComplete = complete;
        race.Sites[0].State = state;
        race.Sites[0].IsSource = source;
        Assert.Empty(RacePullPolicy.Find(race, [Show()]));
    }

    [Fact]
    public void RaceOverallSuccessDoesNotInventDestinationCompletionAndPartialRaceKeepsRealSuccess()
    {
        var race = Race();
        race.Sites.Add(new() { ServerId = "unfinished", State = nameof(DestState.TimedOut),
            Path = "/tv/" + Release, IsComplete = false });
        race.State = "Stopped";
        Assert.Equal("mirror", Assert.Single(RacePullPolicy.Find(race, [Show()])).ServerId);
        race.Sites[0].State = nameof(DestState.AwaitingCompletion);
        race.State = "Completed";
        Assert.Empty(RacePullPolicy.Find(race, [Show()]));
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("paused")]
    [InlineData("completed")]
    [InlineData("dead")]
    [InlineData("different-title")]
    public void OnlyActiveFollowedShowsCanPull(string kind)
    {
        var item = Show();
        switch (kind)
        {
            case "movie": item.Type = MediaType.Movie; break;
            case "paused": item.Status = WishlistStatus.Paused; break;
            case "completed": item.Status = WishlistStatus.Completed; break;
            case "dead": item.Dead = true; break;
            case "different-title": item.Title = "Other Show"; break;
        }
        Assert.Empty(RacePullPolicy.Find(Race(), [item]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/tv")]
    [InlineData("/tv/../Example.Show.S01E01.1080p.WEB.H264-GROUP")]
    [InlineData("tv/Example.Show.S01E01.1080p.WEB.H264-GROUP")]
    [InlineData("/tv\\Example.Show.S01E01.1080p.WEB.H264-GROUP")]
    [InlineData("/tv/\r\nExample.Show.S01E01.1080p.WEB.H264-GROUP")]
    public void UnknownOrUnsafePathCannotBeReconstructedIntoAPull(string? path)
    {
        var race = Race(); race.Sites[0].Path = path;
        Assert.Empty(RacePullPolicy.Find(race, [Show()]));
    }

    [Theory]
    [InlineData("Example.Show.2026.01.02.1080p.WEB.H264-GROUP")]
    [InlineData("Example.Show.S01.1080p.WEB.H264-GROUP")]
    [InlineData("Example.Show.S01E01E02.1080p.WEB.H264-GROUP")]
    public void DateSeasonAndMultiEpisodeIdentityUseExistingWishlistParser(string release)
    {
        var race = Race(); race.Release = release; race.Sites[0].Path = "/tv/" + release;
        Assert.Single(RacePullPolicy.Find(race, [Show()]));
    }

    [Fact]
    public void DuplicateDestinationsAndWishlistItemsDoNotDuplicateCandidate()
    {
        var race = Race(); race.Sites.Add(race.Sites[0]);
        var show = Show();
        var candidate = Assert.Single(RacePullPolicy.Find(race, [show, show]));
        Assert.Single(candidate.WishlistItemIds);
    }

    [Fact]
    public void InventoryDoesNotCountMissingStubsMetadataOrOverflowAsReleaseBytes()
    {
        var race = Race();
        race.Files.Add(new() { Name = "-MISSING-video.r00", Size = 100000 });
        race.Files.Add(new() { Name = ".message", Size = 100000 });
        Assert.Equal(1010, Assert.Single(RacePullPolicy.Find(race, [Show()])).SizeBytes);
        race.Files.Add(new() { Name = "huge.bin", Size = long.MaxValue });
        Assert.Null(Assert.Single(RacePullPolicy.Find(race, [Show()])).SizeBytes);
        race.Files = [];
        Assert.Null(Assert.Single(RacePullPolicy.Find(race, [Show()])).SizeBytes);
    }
}
