using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class SitebotEventParserTests
{
    private const string Release = "Fixture.Show.S01E02.1080p.WEB.H264-GRP";

    [Theory]
    [InlineData("[ NEW ] in [ tv-hd ]", SitebotEventKind.New)]
    [InlineData("[ CHECKERED-FLAG ] in [ tv-hd ]", SitebotEventKind.Complete)]
    [InlineData("[ CROSSED STICKS ] in [ tv-hd ]", SitebotEventKind.Progress)]
    [InlineData("NEW RELEASE: tv-hd", SitebotEventKind.New)]
    [InlineData("COMPLETE: tv-hd", SitebotEventKind.Complete)]
    [InlineData("HALFWAY: tv-hd", SitebotEventKind.Halfway)]
    [InlineData("RACE: tv-hd", SitebotEventKind.Race)]
    [InlineData("NUKE: tv-hd", SitebotEventKind.Nuke)]
    [InlineData("UNNUKE: tv-hd", SitebotEventKind.Unnuke)]
    [InlineData("[ TV-MAZE ] in [ tv-hd ]", SitebotEventKind.Metadata)]
    [InlineData("IMDB: tv-hd", SitebotEventKind.Metadata)]
    public void Redacted_theme_families_parse_into_typed_events(string header, SitebotEventKind kind)
    {
        var parsed = SitebotEventParser.Parse($"{header} {Release} [ 682MB - 15F ]");
        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal("tv-hd", parsed.Section);
        Assert.Equal(Release, parsed.Release);
        Assert.Equal(682L * 1024 * 1024, parsed.Bytes);
        Assert.Equal(15, parsed.Files);
    }

    [Fact]
    public void Complete_solo_records_explicit_final_winner_and_size_speed()
    {
        var parsed = SitebotEventParser.Parse($"COMPLETE: -TV- {Release} - 1136MB/24F/7.83MB/s/2m25s - Solo by racerOne");
        Assert.NotNull(parsed);
        Assert.Equal(1136L << 20, parsed.Bytes);
        Assert.Equal(24, parsed.Files);
        Assert.Equal((double)(long)(7.83m * (1 << 20)), parsed.SpeedBytesPerSecond);
        Assert.Equal("racerOne", parsed.Winner);
        Assert.True(parsed.Solo);
    }

    [Theory]
    [InlineData("HALFWAY")]
    [InlineData("RACE")]
    public void Progress_leaders_and_solo_text_never_become_final_winners(string label)
    {
        var parsed = SitebotEventParser.Parse($"{label}: -TV- {Release} leader: racerOne @ 8.2MB/s Solo by racerTwo");
        Assert.NotNull(parsed);
        Assert.Equal("racerOne", parsed.Leader);
        Assert.Null(parsed.Winner);
        Assert.False(parsed.Solo);
        Assert.Null(parsed.Bytes);
        Assert.NotNull(parsed.SpeedBytesPerSecond);
    }

    [Fact]
    public void Completion_without_explicit_final_account_is_unknown()
    {
        var parsed = SitebotEventParser.Parse($"COMPLETE: -TV- {Release} - 1136MB/24F - leader: racerOne");
        Assert.NotNull(parsed);
        Assert.Null(parsed.Winner);
        Assert.False(parsed.Solo);
    }

    [Theory]
    [InlineData("NUKED", SitebotEventKind.Nuke)]
    [InlineData("UNNUKED", SitebotEventKind.Unnuke)]
    public void Arrow_theme_preserves_nukees_reason_and_factor(string label, SitebotEventKind kind)
    {
        var parsed = SitebotEventParser.Parse($"{label} >> tv-hd/{Release} >> nukees: racerOne, racerTwo >> reason: incomplete (10x)");
        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(["racerOne", "racerTwo"], parsed.Nukees);
        Assert.Equal("incomplete (10x)", parsed.Reason);
        Assert.Equal(10, parsed.NukeFactor);
    }

    [Fact]
    public void Metadata_fields_are_advisory_and_missing_fields_stay_unknown()
    {
        var parsed = SitebotEventParser.Parse($"[ TV-MAZE ] in [ tv-hd ] {Release} [ network: Example TV | status: Running | genres: Drama, Comedy | imdb: tt1234567 | rating: 8.4 | votes: 12,345 ]");
        Assert.NotNull(parsed);
        Assert.Equal("tt1234567", parsed.ImdbId);
        Assert.Equal("Example TV", parsed.Network);
        Assert.Equal("Running", parsed.ShowStatus);
        Assert.Equal(["Drama", "Comedy"], parsed.Genres);
        Assert.Equal(8.4, parsed.ImdbRating);
        Assert.Equal(12345, parsed.ImdbVotes);
        var sparse = SitebotEventParser.Parse($"IMDB: tv-hd {Release} tt1234567");
        Assert.Null(sparse!.ShowStatus);
        Assert.Null(sparse.ImdbRating);
        Assert.Empty(sparse.Genres);
    }

    [Fact]
    public void Configurable_template_can_capture_an_alternate_theme()
    {
        var template = new SitebotTemplate
        {
            Kind = SitebotEventKind.Complete,
            Pattern = @"^DONE (?<section>\S+) (?<release>\S+) bytes=(?<bytes>\d+) files=(?<files>\d+) winner=(?<winner>\S+)$"
        };
        Assert.Empty(SitebotEventParser.ValidateTemplate(template));
        var parsed = SitebotEventParser.Parse($"DONE tv-hd {Release} bytes=123456 files=12 winner=racerOne", [template]);
        Assert.Equal(123456, parsed!.Bytes);
        Assert.Equal(12, parsed.Files);
        Assert.Equal("racerOne", parsed.Winner);
        Assert.True(SitebotEventParser.IsNonNewMessage($"DONE tv-hd {Release} bytes=123456 files=12 winner=racerOne", [template]));
    }

    [Theory]
    [InlineData(@"(?<section>\S+) (?<release>\S+)")]
    [InlineData(@"^(?<release>\S+)$")]
    [InlineData(@"^(?<section>\S+) (?<release>(?<=x).*)$")]
    [InlineData(@"^(?<section>\S+) (?<release>\S+)\1$")]
    [InlineData("[")]
    public void Unsafe_or_incomplete_templates_are_rejected(string pattern)
    {
        Assert.NotEmpty(SitebotEventParser.ValidateTemplate(new SitebotTemplate { Pattern = pattern }));
    }

    [Fact]
    public void Invalid_template_shape_limits_are_checked()
    {
        Assert.NotEmpty(SitebotEventParser.ValidateTemplate(null));
        Assert.NotEmpty(SitebotEventParser.ValidateTemplate(new() { Kind = (SitebotEventKind)999, Pattern = "^x" }));
        Assert.NotEmpty(SitebotEventParser.ValidateTemplate(new() { Pattern = "^" + new string('x', 1025) }));
    }

    [Theory]
    [InlineData("ordinary prose DONE tv-hd ")]
    [InlineData("ordinary prose\nDONE tv-hd ")]
    public void Template_alternation_cannot_match_a_midline_or_second_line_fragment(string prefix)
    {
        var template = new SitebotTemplate
        {
            Kind = SitebotEventKind.Complete,
            Pattern = @"^SAFE|DONE (?<section>\S+) (?<release>\S+)"
        };
        Assert.Null(SitebotEventParser.Parse(prefix + Release, [template]));
        Assert.False(SitebotEventParser.IsNonNewMessage(prefix + Release, [template]));
        Assert.NotNull(SitebotEventParser.Parse("DONE tv-hd " + Release, [template]));
    }

    [Theory]
    [InlineData("0MB - 0F")]
    [InlineData("-12MB - -3F")]
    [InlineData("99999999999999999999999TB - 9999999999999999F")]
    public void Invalid_numeric_fields_are_unknown_and_cannot_overflow(string details)
    {
        var parsed = SitebotEventParser.Parse($"COMPLETE: tv-hd {Release} [ {details} ]");
        Assert.NotNull(parsed);
        Assert.Null(parsed.Bytes);
        Assert.Null(parsed.Files);
    }

    [Fact]
    public void Fractions_and_binary_size_units_are_supported()
    {
        var parsed = SitebotEventParser.Parse($"COMPLETE: tv-hd {Release} [ 1.5GiB - 24FILES ]");
        Assert.Equal(3L << 29, parsed!.Bytes);
        Assert.Equal(24, parsed.Files);
    }

    [Theory]
    [InlineData("COMPLETE: tv-hd prose")]
    [InlineData("COMPLETE: tv-hd ../../Fixture.Release")]
    [InlineData("ordinary channel conversation")]
    [InlineData("COMPLETE: tv-hd .Fixture.Release.Name")]
    public void Noise_and_unsafe_release_tokens_do_not_emit_facts(string text) => Assert.Null(SitebotEventParser.Parse(text));

    [Fact]
    public void Input_bound_and_explicit_nonnew_label_guard_precede_legacy_rules()
    {
        Assert.Null(SitebotEventParser.Parse(new string('x', 4097)));
        Assert.True(SitebotEventParser.IsNonNewMessage("COMPLETE: malformed"));
        Assert.True(SitebotEventParser.IsNonNewMessage("[ CHECKERED-FLAG ] malformed"));
        Assert.False(SitebotEventParser.IsNonNewMessage($"NEW RELEASE: tv-hd {Release}"));
    }

    [Theory]
    [InlineData("bot", "bot@host.example", "bot", "bot@host.example", true)]
    [InlineData("Bot", "bot@*.example", "bOT", "bot@host.example", true)]
    [InlineData("bot", "?ot@host.example", "bot", "bot@host.example", true)]
    [InlineData("bot", "bot@host.example", "imposter", "bot@host.example", false)]
    [InlineData("bot", "bot@host.example", "bot", "bot@evil.example", false)]
    [InlineData("bot", "bot@host.example", "bot", "", false)]
    [InlineData("bot", "", "bot", "", true)]
    [InlineData("", "", "bot", "bot@host.example", false)]
    [InlineData("*", "", "bot", "bot@host.example", false)]
    public void Channel_trust_requires_exact_nick_and_optional_wildcard_host(string nick, string mask,
        string incomingNick, string incomingHost, bool accepted)
    {
        Assert.Equal(accepted, SitebotEventParser.IsTrusted(new() { BotNick = nick, UserHostPattern = mask }, incomingNick, incomingHost));
    }

    [Theory]
    [InlineData("*", "bot@host.example", true)]
    [InlineData("**", "", false)]
    [InlineData("*@*", "bot@host.example", true)]
    [InlineData("*@*", "bothost.example", false)]
    [InlineData("b*t@*.ex?mple", "bOT@a.b.example", true)]
    [InlineData("b*t@*.ex?mple", "bot@example", false)]
    [InlineData("bot@host.example*", "bot@host.example", true)]
    [InlineData("bot@host.example", "bot@host.example.evil", false)]
    [InlineData("bot+1@h(o)st.[ex]", "bot+1@h(o)st.[ex]", true)]
    [InlineData("bot+1@h(o)st.[ex]", "bott1@hostXex", false)]
    [InlineData("a*a*a*a*a*a*a*a*a*b", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("?*?", "ab", true)]
    [InlineData("?*?", "a", false)]
    public void Host_mask_wildcards_match_the_whole_host_literally(string mask, string host, bool accepted)
    {
        Assert.Equal(accepted, SitebotEventParser.IsTrusted(new() { BotNick = "bot", UserHostPattern = mask }, "bot", host));
    }

    [Fact]
    public void Trust_verdict_does_not_depend_on_machine_load()
    {
        // CI run 41164126807: the first exact-host check took 65 ms against a 50 ms regex
        // timeout, and the timeout was answered as "untrusted". A busy box at startup must not
        // reject its own sitebot.
        var config = new SitebotChannelConfig { BotNick = "bot", UserHostPattern = "*b*o*t*@*h*o*s*t*.*e*x*a*m*p*l*e*" };
        var host = "bot@" + new string('h', 120) + "host." + new string('e', 100) + "example";
        var rejected = 0;
        Parallel.For(0, Environment.ProcessorCount * 400, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 4 },
            _ => { if (!SitebotEventParser.IsTrusted(config, "bot", host)) Interlocked.Increment(ref rejected); });
        Assert.Equal(0, rejected);
    }

    [Fact]
    public void Trusted_envelope_is_channel_bound_and_default_is_inert()
    {
        var config = new SitebotChannelConfig { BotNick = "bot" };
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var text = $"COMPLETE: tv-hd {Release}";
        Assert.Null(SitebotEventParser.ParseTrusted("site", "someone", "bot", "", text, config, now));
        Assert.Null(SitebotEventParser.ParseTrusted("site", "#channel", "bot", "", text, new(), now));
        var parsed = SitebotEventParser.ParseTrusted("site", "#channel", "bot", "", text, config, now);
        Assert.Equal("site", parsed!.ServerId);
        Assert.Equal("#channel", parsed.Channel);
        Assert.Equal(now, parsed.ObservedAtUtc);
        config.Enabled = false;
        Assert.False(SitebotEventParser.IsTrusted(config, "bot", ""));
    }
}
