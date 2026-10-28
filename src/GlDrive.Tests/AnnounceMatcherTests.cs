using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class AnnounceMatcherTests
{
    private const string Release = "Synthetic.Show.S01E01.1080p.WEB-TEST";
    private static string Builtin(string release = Release) => $"[ NEW ] in [ TV ] {release}";

    [Fact]
    public void Builtin_works_without_rules_and_uses_global_default()
    {
        var match = Assert.Single(new AnnounceMatcher([], true).Match("#fixture", Builtin()));
        Assert.Equal(new AnnouncePatternMatch("TV", Release, true, "builtin", true), match);
    }

    [Fact]
    public void Builtin_uses_first_enabled_channel_rule_autorace_even_if_pattern_does_not_match()
    {
        var matcher = new AnnounceMatcher([
            new() { Channel = "#elsewhere", Pattern = "unused", AutoRace = true },
            new() { Channel = "#fixture", Pattern = "unused", AutoRace = true, Enabled = false },
            new() { Channel = "#FIXTURE", Pattern = "unused", AutoRace = false },
            new() { Channel = "", Pattern = "unused", AutoRace = true }
        ], true);
        Assert.False(Assert.Single(matcher.Match("#fixture", Builtin())).AutoRace);
    }

    [Fact]
    public void Captures_are_ordered_builtin_then_custom_with_trimmed_sections()
    {
        var matcher = new AnnounceMatcher([
            new() { Pattern = @"\[(?<section> TV )\] (?<release>\S+)", AutoRace = false },
            new() { Pattern = @"(?<release>Synthetic\S+)", AutoRace = true }
        ], true);
        var matches = matcher.Match("#fixture", Builtin());
        Assert.Equal(new[] { "builtin", "custom", "custom" }, matches.Select(m => m.RuleSource));
        Assert.Equal(new[] { "TV", "TV", "" }, matches.Select(m => m.Section));
        Assert.All(matches, m => Assert.True(m.Plausible));
    }

    [Theory]
    [InlineData("DELETE:")]
    [InlineData("[ NUKE ]")]
    [InlineData("DELPRE:")]
    public void Removal_guard_applies_before_builtin_and_custom(string prefix)
    {
        var matcher = new AnnounceMatcher([new() { Pattern = @"(?<release>Synthetic\S+)" }], true);
        Assert.Empty(matcher.Match("#fixture", prefix + " " + Builtin()));
    }

    [Theory]
    [InlineData("peer")]
    [InlineData("*")]
    [InlineData("")]
    public void Private_messages_never_enter_replay(string channel) =>
        Assert.Empty(new AnnounceMatcher([], true).Match(channel, Builtin()));

    [Theory]
    [InlineData("father-to-be")]
    [InlineData("brings")]
    [InlineData("in")]
    public void Broad_custom_captures_remain_visible_as_implausible(string release)
    {
        var matcher = new AnnounceMatcher([new() { Pattern = @"^(?<section>\w+) (?<release>\S+)$" }], true);
        var capture = Assert.Single(matcher.Match("#fixture", "TV " + release));
        Assert.Equal(release, capture.Release);
        Assert.False(capture.Plausible);
    }

    [Fact]
    public void Invalid_disabled_and_wrong_channel_rules_cannot_match()
    {
        var matcher = new AnnounceMatcher([
            new() { Pattern = "(" },
            new() { Pattern = @"(?<release>\S+)", Enabled = false },
            new() { Pattern = @"(?<release>\S+)", Channel = "#other" }
        ], true);
        Assert.Empty(matcher.Match("#fixture", Release));
        Assert.Equal("(", Assert.Single(matcher.InvalidPatterns));
    }

    [Fact]
    public void Pure_matching_does_not_deduplicate_or_change_rules()
    {
        var rule = new IrcAnnounceRule { Channel = "#fixture", Pattern = @"(?<release>\S+)" };
        var matcher = new AnnounceMatcher([rule], false);
        var first = matcher.Match("#fixture", Release);
        Assert.Equal(first, matcher.Match("#fixture", Release));
        Assert.Equal(@"(?<release>\S+)", rule.Pattern);
        Assert.Equal("#fixture", rule.Channel);
    }

    [Fact]
    public void Listener_keeps_custom_fallback_after_builtin_dedupe()
    {
        using var listener = new IrcAnnounceListener("fixture", [new()
        {
            Channel = "#fixture", Pattern = @"fallback:(?<section>\w+) (?<release>\S+)", AutoRace = true
        }], true, recordMatchedAnnounce: _ => { });
        var sections = new List<string>();
        listener.ReleaseAnnounced += (_, section, _, _) => sections.Add(section);
        var message = new IrcMessageItem { Type = IrcMessageType.Normal, Nick = "fixture",
            Text = Builtin() + " fallback:ALT " + Release, Timestamp = DateTime.UtcNow };
        listener.OnMessage("#fixture", message);
        listener.OnMessage("#fixture", message);
        Assert.Equal(new[] { "TV", "ALT" }, sections);
    }
}
