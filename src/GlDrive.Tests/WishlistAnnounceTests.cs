using GlDrive.Config;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class WishlistAnnounceTests
{
    private const string Release = "Example.Show.S01E02.1080p.WEB-GROUP";

    [Fact]
    public void IrcPathUsesExplicitMappingPathBeforeSectionDictionary()
    {
        var server = new ServerConfig();
        server.SpreadSite.Sections["remote-tv"] = "/dictionary/tv";
        server.SpreadSite.SectionMappings.Add(new SectionMapping
        {
            IrcSection = "TV", RemoteSection = "remote-tv", Path = "/mapped/tv/"
        });

        Assert.Equal("/mapped/tv/" + Release, WishlistIrcRelease.ResolvePath(server, "tv", Release));
    }

    [Fact]
    public void IrcMappingResolvesRemoteSectionCaseInsensitively()
    {
        var server = new ServerConfig();
        server.SpreadSite.Sections["REMOTE-TV"] = "/incoming/tv/";
        server.SpreadSite.SectionMappings.Add(new SectionMapping { IrcSection = "TV", RemoteSection = "remote-tv" });

        Assert.Equal("/incoming/tv/" + Release, WishlistIrcRelease.ResolvePath(server, "tv", Release));
    }

    [Fact]
    public void ExactRawSectionUsesConfiguredPathCaseInsensitively()
    {
        var server = new ServerConfig();
        server.SpreadSite.Sections["TV-HD"] = "/archive/television";
        server.Notifications.WatchPath = "/recent";

        Assert.Equal("/archive/television/" + Release, WishlistIrcRelease.ResolvePath(server, "tv-hd", Release));
    }

    [Fact]
    public void UnmappedIrcSectionUsesWatchPathEvenWhenExcludedFromPolling()
    {
        var server = new ServerConfig();
        server.Notifications.WatchPath = "/recent/";
        server.Notifications.ExcludedCategories.Add("tv-hd");

        Assert.Equal("/recent/tv-hd/" + Release, WishlistIrcRelease.ResolvePath(server, "tv-hd", Release));
    }

    [Theory]
    [InlineData(false, ".*")]
    [InlineData(true, "2160p")]
    public void InapplicableMappingDoesNotOverrideExactSection(bool enabled, string trigger)
    {
        var server = new ServerConfig();
        server.SpreadSite.Sections["tv"] = "/incoming/tv";
        server.SpreadSite.SectionMappings.Add(new SectionMapping
        {
            IrcSection = "tv", RemoteSection = "missing", Path = "/different", Enabled = enabled, TriggerRegex = trigger
        });

        Assert.Equal("/incoming/tv/" + Release, WishlistIrcRelease.ResolvePath(server, "tv", Release));
    }

    [Fact]
    public void BrokenExplicitMappingDoesNotGuessWatchPath()
    {
        var server = new ServerConfig();
        server.SpreadSite.SectionMappings.Add(new SectionMapping { IrcSection = "tv", RemoteSection = "missing" });
        Assert.Null(WishlistIrcRelease.ResolvePath(server, "tv", Release));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".metadata")]
    [InlineData("tv/other")]
    [InlineData("tv\\other")]
    [InlineData("tv\r\nother")]
    public void UnsafeSectionOrReleaseDoesNotProducePath(string unsafeSegment)
    {
        var server = new ServerConfig();
        Assert.Null(WishlistIrcRelease.ResolvePath(server, unsafeSegment, Release));
        Assert.Null(WishlistIrcRelease.ResolvePath(server, "tv", unsafeSegment));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/incoming\n/tv")]
    public void InvalidConfiguredPathDoesNotProduceDownload(string path)
    {
        var server = new ServerConfig();
        server.SpreadSite.Sections["tv"] = path;
        Assert.Null(WishlistIrcRelease.ResolvePath(server, "tv", Release));
    }

    [Fact]
    public void DedupeRejectsSecondSourceDuringAndAfterEvaluationIgnoringCase()
    {
        var dedupe = new WishlistReleaseDeduplicator();
        Assert.True(dedupe.TryBegin("wanted", Release));
        Assert.False(dedupe.TryBegin("wanted", Release.ToLowerInvariant()));
        dedupe.Complete("wanted", Release);
        Assert.False(dedupe.TryBegin("wanted", Release.ToUpperInvariant()));
        Assert.True(dedupe.TryBegin("another-item", Release));
    }

    [Fact]
    public void FailedEnqueueCanReleaseClaimForAnotherSource()
    {
        var dedupe = new WishlistReleaseDeduplicator();
        Assert.True(dedupe.TryBegin("wanted", Release));
        dedupe.Forget("wanted", Release);
        Assert.True(dedupe.TryBegin("wanted", Release));
    }

    [Fact]
    public void CapacityEvictsOldCompletedEvaluationsButNeverActiveClaims()
    {
        var dedupe = new WishlistReleaseDeduplicator(capacity: 1);
        Assert.True(dedupe.TryBegin("active", Release));
        Assert.True(dedupe.TryBegin("first", Release));
        dedupe.Complete("first", Release);
        Assert.True(dedupe.TryBegin("second", Release));
        dedupe.Complete("second", Release);

        Assert.False(dedupe.TryBegin("active", Release));
        Assert.False(dedupe.TryBegin("second", Release));
        Assert.True(dedupe.TryBegin("first", Release));
    }

    [Fact]
    public void ItemEditInvalidatesCompletedMissesButPreservesActiveClaimsAndOtherItems()
    {
        var dedupe = new WishlistReleaseDeduplicator();
        Assert.True(dedupe.TryBegin("wanted", "miss"));
        dedupe.Complete("wanted", "miss");
        Assert.True(dedupe.TryBegin("wanted", "active"));
        Assert.True(dedupe.TryBegin("another", "miss"));
        dedupe.Complete("another", "miss");

        dedupe.ForgetItem("wanted");

        Assert.True(dedupe.TryBegin("wanted", "miss"));
        Assert.False(dedupe.TryBegin("wanted", "active"));
        Assert.False(dedupe.TryBegin("another", "miss"));
        dedupe.Complete("wanted", "active");
        Assert.True(dedupe.TryBegin("wanted", "active"));
    }
}
