using System.IO;
using GlDrive.Config;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// AI Setup's merge rules (Spread/CLAUDE.md "Merge semantics"), extracted from the dialog's
/// click handler into <see cref="AiSetupMerger"/>.
/// </summary>
public sealed class AiSetupMergerTests
{
    private static SectionMapping Mapping(string irc, string remote, string trigger = ".*", string path = "") =>
        new() { IrcSection = irc, RemoteSection = remote, TriggerRegex = trigger, Path = path };

    private static AiSetupMergeResult Merge(AiSetupResult ai, AiSetupEditState? state = null,
        params SkiplistRule[] deterministic) =>
        AiSetupMerger.Merge(ai, deterministic, state ?? new AiSetupEditState());

    // --- section_mappings -------------------------------------------------------------

    [Theory]
    [InlineData(".*")]
    [InlineData("")]
    [InlineData("  .*  ")]
    public void Default_trigger_on_an_existing_mapping_is_replaced(string existingTrigger)
    {
        var state = new AiSetupEditState { SectionMappings = [Mapping("TV", "TV_HD", existingTrigger, "/tv-hd")] };
        var ai = new AiSetupResult { SectionMappings = [new() { IrcSection = "tv", RemoteSection = "tv_hd", Trigger = @" (?i).*\.1080p\..* " }] };

        var result = Merge(ai, state);

        var m = Assert.Single(result.State.SectionMappings);
        Assert.Equal(@"(?i).*\.1080p\..*", m.TriggerRegex);
        Assert.Equal("/tv-hd", m.Path);
        Assert.Equal(["1 mapping trigger(s) updated from IRC logs"], result.Changes);
    }

    [Fact]
    public void User_edited_trigger_is_preserved()
    {
        var state = new AiSetupEditState { SectionMappings = [Mapping("TV", "TV_HD", @".*\.2160p\..*")] };
        var ai = new AiSetupResult { SectionMappings = [new() { IrcSection = "TV", RemoteSection = "TV_HD", Trigger = @".*\.1080p\..*" }] };

        var result = Merge(ai, state);

        Assert.Equal(@".*\.2160p\..*", Assert.Single(result.State.SectionMappings).TriggerRegex);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Ai_default_trigger_changes_nothing_on_an_existing_mapping()
    {
        var state = new AiSetupEditState { SectionMappings = [Mapping("TV", "TV_HD")] };
        var ai = new AiSetupResult { SectionMappings = [new() { IrcSection = "TV", RemoteSection = "TV_HD", Trigger = "  " }] };

        var result = Merge(ai, state);

        Assert.Equal(".*", Assert.Single(result.State.SectionMappings).TriggerRegex);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void New_mapping_is_added_with_the_path_of_its_remote_section()
    {
        var state = new AiSetupEditState { SectionMappings = [Mapping("TV-HD", "TV_HD", path: "/tv-hd")] };
        var ai = new AiSetupResult
        {
            SectionMappings =
            [
                new() { IrcSection = "TV", RemoteSection = "TV_HD", Trigger = @"(?i).*\.1080p\..*" },
                new() { IrcSection = "MOVIE", RemoteSection = "X265", Trigger = "" },
                new() { IrcSection = "", RemoteSection = "TV_HD" },
                new() { IrcSection = "TV", RemoteSection = " " },
            ]
        };

        var result = Merge(ai, state);

        Assert.Equal(3, result.State.SectionMappings.Count);
        var tv = result.State.SectionMappings[1];
        Assert.Equal(("TV", "TV_HD", "/tv-hd", @"(?i).*\.1080p\..*", true), (tv.IrcSection, tv.RemoteSection, tv.Path, tv.TriggerRegex, tv.Enabled));
        var movie = result.State.SectionMappings[2];
        Assert.Equal(("MOVIE", "X265", "", ".*"), (movie.IrcSection, movie.RemoteSection, movie.Path, movie.TriggerRegex));
        Assert.Equal(["2 IRC section mapping(s) detected"], result.Changes);
    }

    // --- sections -----------------------------------------------------------------------

    [Fact]
    public void Suggested_sections_add_rows_and_fill_only_missing_paths()
    {
        var state = new AiSetupEditState
        {
            SectionMappings = [Mapping("TV", "TV_HD"), Mapping("MP3", "MP3", path: "/mp3")]
        };
        var ai = new AiSetupResult
        {
            SuggestedSections = new() { ["tv_hd"] = "/incoming/tv-hd", ["MP3"] = "/other/mp3", ["FLAC"] = "/flac" }
        };

        var result = Merge(ai, state);

        var rows = result.State.SectionMappings;
        Assert.Equal("/incoming/tv-hd", rows[0].Path);
        Assert.Equal("/mp3", rows[1].Path);
        Assert.Equal(("FLAC", "FLAC", "/flac", ".*"), (rows[2].IrcSection, rows[2].RemoteSection, rows[2].Path, rows[2].TriggerRegex));
        Assert.Equal(["2 section mapping(s) added"], result.Changes);
    }

    // --- skiplist -----------------------------------------------------------------------

    [Fact]
    public void Skiplist_replaces_old_section_rules_keeps_global_ones_and_supplements_from_the_parser()
    {
        var userGlobal = new SkiplistRule { Pattern = "*.jpg", MatchDirectories = false };
        var oldSectionRule = new SkiplistRule { Pattern = "*GERMAN*", Section = "0DAY" };
        var state = new AiSetupEditState { Skiplist = [userGlobal, oldSectionRule] };
        var ai = new AiSetupResult
        {
            SkiplistRules =
            [
                new() { Pattern = "*FRENCH*", Section = "0DAY", MatchFiles = false },
                new() { Pattern = "*.JPG", Action = "Allow" },
            ]
        };

        var result = Merge(ai, state,
            new SkiplistRule { Pattern = "*french*", Section = "APPS" },
            new SkiplistRule { Pattern = "*BETA*", Section = "0DAY", MatchFiles = false });

        Assert.Equal(["*.jpg|-", "*FRENCH*|0DAY", "*BETA*|0DAY"],
            result.State.Skiplist.Select(r => $"{r.Pattern}|{r.Section ?? "-"}"));
        Assert.Same(userGlobal, result.State.Skiplist[0]);
        Assert.Equal(SkiplistAction.Deny, result.State.Skiplist[1].Action);
        Assert.Equal(["2 skiplist rules (replaced 1 old section rules)"], result.Changes);
    }

    [Fact]
    public void No_incoming_rules_leaves_section_rules_alone()
    {
        var state = new AiSetupEditState { Skiplist = [new SkiplistRule { Pattern = "*GERMAN*", Section = "0DAY" }] };

        var result = Merge(new AiSetupResult(), state);

        Assert.Single(result.State.Skiplist);
        Assert.Empty(result.Changes);
    }

    // --- scalar and text fields ---------------------------------------------------------

    [Fact]
    public void Slots_affils_and_exclusions_merge_into_the_text_fields()
    {
        var state = new AiSetupEditState
        {
            MaxUploadSlots = "3", MaxDownloadSlots = "3",
            Affils = "ZZZ, grp", ExcludedCategories = "xxx",
        };
        var ai = new AiSetupResult
        {
            MaxUploadSlots = 5, MaxDownloadSlots = 2,
            Affils = ["GRP", "AAA"],
            ExcludedNotificationCategories = ["XXX", " 0day-xxx ", ""],
        };

        var result = Merge(ai, state);

        Assert.Equal("5", result.State.MaxUploadSlots);
        Assert.Equal("2", result.State.MaxDownloadSlots);
        Assert.Equal("AAA, grp, ZZZ", result.State.Affils);
        Assert.Equal("0day-xxx, xxx", result.State.ExcludedCategories);
        Assert.Equal(["Max uploads: 5", "Max downloads: 2", "Affils: 2 groups", "1 notification category exclusion(s)"],
            result.Changes);
    }

    [Fact]
    public void Announce_rules_append_only_new_lines()
    {
        var state = new AiSetupEditState { AnnounceRules = "#announce \\[(?<section>\\w+)\\] (?<release>\\S+)\n" };
        var ai = new AiSetupResult
        {
            AnnounceRules =
            [
                new() { Channel = "#ANNOUNCE", Pattern = "\\[(?<section>\\w+)\\] (?<release>\\S+)" },
                new() { Channel = "", Pattern = "NEW (?<section>\\S+) (?<release>\\S+)" },
                new() { Channel = "#x", Pattern = " " },
            ]
        };

        var result = Merge(ai, state);

        Assert.Equal("#announce \\[(?<section>\\w+)\\] (?<release>\\S+)\nNEW (?<section>\\S+) (?<release>\\S+)",
            result.State.AnnounceRules);
        Assert.Equal(["1 IRC announce rule(s) detected"], result.Changes);
    }

    [Theory]
    [InlineData("Very High", SitePriority.VeryHigh)]
    [InlineData("very_low", SitePriority.VeryLow)]
    [InlineData(" high ", SitePriority.High)]
    public void Priority_is_parsed_and_applied(string ai, SitePriority expected)
    {
        var result = Merge(new AiSetupResult { Priority = ai }, new AiSetupEditState { Priority = SitePriority.Normal });

        Assert.Equal(expected, result.State.Priority);
        Assert.Equal([$"Priority → {ai}"], result.Changes);
    }

    [Theory]
    [InlineData("Normal")]
    [InlineData("top-tier")]
    [InlineData(null)]
    public void Same_or_unknown_priority_changes_nothing(string? ai)
    {
        var result = Merge(new AiSetupResult { Priority = ai }, new AiSetupEditState { Priority = SitePriority.Normal });

        Assert.Equal(SitePriority.Normal, result.State.Priority);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Download_only_is_only_ever_switched_on()
    {
        Assert.True(Merge(new AiSetupResult { DownloadOnly = true }).State.DownloadOnly);
        Assert.True(Merge(new AiSetupResult { DownloadOnly = false }, new AiSetupEditState { DownloadOnly = true }).State.DownloadOnly);
        Assert.Empty(Merge(new AiSetupResult { DownloadOnly = true }, new AiSetupEditState { DownloadOnly = true }).Changes);
    }

    [Fact]
    public void Request_filler_is_enabled_from_a_detected_pattern_unless_already_on()
    {
        var ai = new AiSetupResult { RequestFillerPattern = "!request (?<release>\\S+)", RequestFillerChannel = "" };

        var off = Merge(ai, new AiSetupEditState { RequestFillerChannel = "#req" });
        Assert.True(off.State.RequestFillerEnabled);
        Assert.Equal("!request (?<release>\\S+)", off.State.RequestFillerPattern);
        Assert.Equal("#req", off.State.RequestFillerChannel);
        Assert.Equal(["Auto request filler enabled"], off.Changes);

        var on = Merge(ai, new AiSetupEditState { RequestFillerEnabled = true, RequestFillerPattern = "mine" });
        Assert.Equal("mine", on.State.RequestFillerPattern);
        Assert.Empty(on.Changes);
    }

    // --- purity and layering ------------------------------------------------------------

    [Fact]
    public void Input_state_is_never_mutated()
    {
        var mapping = Mapping("TV", "TV_HD");
        var sectionRule = new SkiplistRule { Pattern = "*GERMAN*", Section = "0DAY" };
        var state = new AiSetupEditState { SectionMappings = [mapping], Skiplist = [sectionRule], Affils = "A" };
        var ai = new AiSetupResult
        {
            Affils = ["B"],
            SkiplistRules = [new() { Pattern = "*FRENCH*" }],
            SuggestedSections = new() { ["TV_HD"] = "/tv" },
            SectionMappings = [new() { IrcSection = "TV", RemoteSection = "TV_HD", Trigger = "x" }],
        };

        var result = Merge(ai, state);

        Assert.Equal((".*", ""), (mapping.TriggerRegex, mapping.Path));
        Assert.Equal([sectionRule], state.Skiplist);
        Assert.Equal("A", state.Affils);
        Assert.NotSame(mapping, result.State.SectionMappings[0]);
        Assert.Equal(("x", "/tv"), (result.State.SectionMappings[0].TriggerRegex, result.State.SectionMappings[0].Path));
    }

    [Fact]
    public void The_agent_pipeline_never_calls_the_dialog_merger()
    {
        // Validators own AppConfig mutation in the agent loop; the merger is for the
        // user-reviewed dialog only.
        var agentDir = Path.Combine(RepoRoot(), "src", "GlDrive", "AiAgent");
        var offenders = Directory.EnumerateFiles(agentDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains(nameof(AiSetupMerger), StringComparison.Ordinal))
            .ToList();
        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(".*", true)]
    [InlineData(" .* ", true)]
    [InlineData(".+", false)]
    [InlineData(@"(?i).*\.1080p\..*", false)]
    public void Default_trigger_helper(string? trigger, bool expected) =>
        Assert.Equal(expected, SectionMapping.IsDefaultTrigger(trigger));

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "src", "GlDrive", "App.xaml.cs"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
