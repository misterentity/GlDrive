using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Services.Automation;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class SitebotMetadataConfigTests
{
    private const string Release = "Example.Show.S01E02.1080p.WEB.H264-TEST";
    private static SitebotEvent Metadata => new()
    {
        Kind = SitebotEventKind.Metadata, Release = Release, ImdbRating = 8.2,
        ImdbVotes = 2000, Genres = ["Drama"], ShowStatus = "Running"
    };

    [Fact]
    public void Ordinary_channel_edits_preserve_configured_trust_and_templates()
    {
        var original = new IrcChannelConfig { Name = "#test", Sitebot = new() { BotNick = "Bot", UserHostPattern = "bot@*.test" } };
        var edited = GlDrive.UI.IrcChannelEditor.Parse("-#TEST key\n#new", [original]);
        Assert.False(edited[0].AutoJoin);
        Assert.Equal("key", edited[0].Key);
        Assert.Same(original.Sitebot, edited[0].Sitebot);
        Assert.Empty(edited[1].Sitebot.BotNick);
    }

    [Fact]
    public void Defaults_preserve_decisions_and_old_channels_are_untrusted()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("""{"servers":[{"irc":{"channels":[{"name":"#test"}]}}]}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(config.Spread.UseSitebotMetadata);
        Assert.False(config.Spread.UseSitebotCompletionHints);
        Assert.True(config.Servers[0].Irc.Channels[0].Sitebot.Enabled);
        Assert.Empty(config.Servers[0].Irc.Channels[0].Sitebot.BotNick);
        var before = RuntimeConfigPolicy.Capture(config.Servers[0], config.Downloads, "", 1);
        config.Servers[0].Irc.Channels[0].Sitebot.BotNick = "TrustedBot";
        var after = RuntimeConfigPolicy.Capture(config.Servers[0], config.Downloads, "", 1);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.NotEmpty(after.Changed(before, RuntimeConfigEffect.Irc));
    }

    [Fact]
    public void Complete_metadata_uses_same_rating_votes_genre_status_thresholds()
    {
        var filter = new MetadataFilterConfig { Enabled = true, MinImdbRating = 7, MinVotes = 1000, AllowGenres = "Drama", SkipEndedShows = true };
        Assert.True(MetadataFilterService.EvaluateSitebotMetadata(filter, Metadata, Release)!.Value.Allowed);
        Assert.False(MetadataFilterService.EvaluateSitebotMetadata(filter, Metadata with { ImdbRating = 6 }, Release)!.Value.Allowed);
        Assert.False(MetadataFilterService.EvaluateSitebotMetadata(filter, Metadata with { ImdbVotes = 10 }, Release)!.Value.Allowed);
        Assert.False(MetadataFilterService.EvaluateSitebotMetadata(filter, Metadata with { Genres = ["Comedy"] }, Release)!.Value.Allowed);
        Assert.False(MetadataFilterService.EvaluateSitebotMetadata(filter, Metadata with { ShowStatus = "Ended" }, Release)!.Value.Allowed);
    }

    [Fact]
    public async Task Enabled_exact_source_metadata_answers_before_any_http_lookup()
    {
        var facts = new SitebotFactStore();
        Assert.True(facts.Observe(Metadata with { ServerId = "source", ObservedAtUtc = DateTime.UtcNow, ImdbRating = 4 }));
        var app = new AppConfig { Spread = new() { UseSitebotMetadata = true } };
        using var filter = new MetadataFilterService(app, facts);
        // An already cancelled token makes a provider lookup unusable. A complete
        // passive fact answers locally before any client request is attempted.
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var verdict = await filter.EvaluateAsync(new() { Enabled = true, MinImdbRating = 7 }, Release,
            GlDrive.Downloads.SceneNameParser.Parse(Release), cancelled.Token, sourceServerId: "source");
        Assert.False(verdict.Allowed);
        Assert.Contains("rating", verdict.Reason);
    }

    [Fact]
    public void Missing_fields_or_wrong_release_cannot_bypass_http_filter()
    {
        var filter = new MetadataFilterConfig { Enabled = true, MinImdbRating = 7, MinVotes = 1000, AllowGenres = "Drama", SkipEndedShows = true };
        Assert.Null(MetadataFilterService.EvaluateSitebotMetadata(filter, Metadata with { ImdbRating = null }, Release));
        Assert.Null(MetadataFilterService.EvaluateSitebotMetadata(filter, Metadata with { ImdbVotes = null }, Release));
        Assert.Null(MetadataFilterService.EvaluateSitebotMetadata(filter, Metadata with { Genres = [] }, Release));
        Assert.Null(MetadataFilterService.EvaluateSitebotMetadata(filter, Metadata with { ShowStatus = "unknown" }, Release));
        Assert.Null(MetadataFilterService.EvaluateSitebotMetadata(filter, Metadata with { Release = "Other.Show.S01E02" }, Release));
        Assert.Null(MetadataFilterService.EvaluateSitebotMetadata(filter, Metadata with { Kind = SitebotEventKind.New }, Release));
    }

    [Theory]
    [InlineData("{\"botNick\":\"bad nick\"}")]
    [InlineData("{\"templates\":[{\"kind\":1,\"pattern\":\"(\"}]}")]
    [InlineData("{\"templates\":[{\"kind\":999,\"pattern\":\"^x$\"}]}")]
    [InlineData("{\"templates\":[{\"kind\":1,\"pattern\":\"^(?<release>.*)(?<=x)$\"}]}")]
    public void Invalid_bot_config_is_rejected_atomically(string bot)
    {
        var config = new AppConfig { Servers = [new() { Id = "test", Irc = new() { Channels = [new() { Name = "#test" }] } }] };
        var saved = false;
        var automation = new ConfigurationAutomation(config, _ => saved = true);
        Assert.Throws<ArgumentException>(() => automation.Apply(new JsonArray(new JsonObject
        {
            ["op"] = "replace", ["path"] = "/servers/0/irc/channels/0/sitebot", ["value"] = JsonNode.Parse(bot)
        })));
        Assert.False(saved);
        Assert.Empty(config.Servers[0].Irc.Channels[0].Sitebot.BotNick);
    }
}
