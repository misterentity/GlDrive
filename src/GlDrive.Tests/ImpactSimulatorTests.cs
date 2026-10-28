using System.IO;
using System.Text.Json;
using GlDrive.AiAgent;
using GlDrive.Config;
using Xunit;

namespace GlDrive.Tests;

public sealed class ImpactSimulatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-impact-" + Guid.NewGuid().ToString("N"));

    public ImpactSimulatorTests() => Directory.CreateDirectory(_root);

    private static AppConfig Config()
    {
        var config = new AppConfig();
        config.Servers.Add(new ServerConfig
        {
            Id = "alpha", Name = "Alpha",
            Irc = new() { Channels = [new() { Name = "#announces" }] },
            SpreadSite = new()
            {
                Sections = new() { ["TV"] = "/tv" },
                SectionMappings = [new() { IrcSection = "TV-HD", RemoteSection = "TV", TriggerRegex = ".*" }]
            }
        });
        return config;
    }

    private static AppConfig Clone(AppConfig config) => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;
    private static AgentChange Change(string category = AgentCategories.Skiplist, object? after = null) => new()
    {
        Category = category,
        Target = category switch
        {
            AgentCategories.AnnounceRule => "/servers/alpha/irc/announceRules/-",
            AgentCategories.ExcludedCategories => "/servers/alpha/notifications/excludedCategories",
            _ => "/servers/alpha/spread/skiplistRules/-"
        },
        After = after ?? new SkiplistRule { Pattern = "*", Action = SkiplistAction.Deny },
        Confidence = 1
    };

    private static ReplayEvidence ReleaseEvidence(string source = "race", bool protectedRelease = true) => new(
        [new("alpha", "TV-HD", "Synthetic.Show.S01E01.1080p.WEB-GRP", source, protectedRelease)], []);

    private static ReplayMessage Message(string text) => new("alpha", "#announces", "announcer", text);
    private static ReplayMessage[] Gains() =>
    [Message("TV First.Show.S01E01.1080p.WEB-GRP"), Message("TV Second.Show.S01E01.1080p.WEB-GRP"),
        Message("TV Third.Show.S01E01.1080p.WEB-GRP")];
    private static ReplayRelease[] KnownGains() => Gains().Select(message =>
        new ReplayRelease("alpha", "TV", message.Text[3..], "wishlist", true)).ToArray();

    private static void AddLooseAnnounceRule(AppConfig config) => config.Servers[0].Irc.AnnounceRules.Add(new()
    {
        Channel = "#announces", Enabled = true, AutoRace = true,
        Pattern = @"^(?<section>\S+)\s+(?<release>\S+)$"
    });

    [Fact]
    public void Three_valid_gains_do_not_hide_new_implausible_chat_captures()
    {
        var before = Config();
        var after = Clone(before);
        AddLooseAnnounceRule(after);
        var summary = ImpactSimulator.Evaluate(Change(AgentCategories.AnnounceRule), before, after,
            new([], [.. Gains(), Message("hello everyone")]));
        Assert.Equal(3, summary.Changed);
        Assert.Equal(1, summary.Implausible);
        Assert.Equal("impact-too-broad", summary.RejectionReason);
    }

    [Fact]
    public void Three_distinct_valid_gains_with_no_losses_are_eligible()
    {
        var before = Config();
        var after = Clone(before);
        AddLooseAnnounceRule(after);
        var summary = ImpactSimulator.Evaluate(Change(AgentCategories.AnnounceRule), before, after, new(KnownGains(), Gains()));
        Assert.Null(summary.RejectionReason);
        Assert.Equal(3, summary.Changed);
        Assert.Equal(0, summary.Lost);
    }

    [Fact]
    public void Duplicate_announces_do_not_prove_three_distinct_new_matches()
    {
        var before = Config();
        var after = Clone(before);
        AddLooseAnnounceRule(after);
        var message = Gains()[0];
        var summary = ImpactSimulator.Evaluate(Change(AgentCategories.AnnounceRule), before, after,
            new(KnownGains(), [message, message, message]));
        Assert.Equal("impact-insufficient-new-matches", summary.RejectionReason);
    }

    [Fact]
    public void Removing_a_previously_valid_announce_is_a_loss()
    {
        var before = Config();
        AddLooseAnnounceRule(before);
        var after = Clone(before);
        after.Servers[0].Irc.AnnounceRules.Clear();
        var summary = ImpactSimulator.Evaluate(Change(AgentCategories.AnnounceRule), before, after,
            new(KnownGains().Select(r => r with { Source = "announce" }).ToArray(), Gains()));
        Assert.Equal(3, summary.Lost);
        Assert.Equal("impact-destructive", summary.RejectionReason);
    }

    [Fact]
    public void Capitalized_prose_is_not_independent_release_evidence()
    {
        var before = Config();
        var after = Clone(before);
        AddLooseAnnounceRule(after);
        after.Servers[0].Irc.AnnounceRules[0].Pattern = @"^(?<section>\S+)\s+(?<release>.+)$";
        var summary = ImpactSimulator.Evaluate(Change(AgentCategories.AnnounceRule), before, after,
            new([], [Message("TV Friendly Greeting To Everyone"), Message("TV Another Ordinary Chat Message"),
                Message("TV More Conversation About The Weather")]));
        Assert.Equal(3, summary.Changed);
        Assert.Equal(0, summary.Implausible);
        Assert.Equal("impact-unverified-announces", summary.RejectionReason);
    }

    [Fact]
    public void Gains_use_all_servers_case_insensitive_sections_and_bounded_unknown_share()
    {
        var before = Config();
        var after = Clone(before);
        AddLooseAnnounceRule(after);
        var known = KnownGains().Select(r => r with { ServerId = "other", Section = "tv" }).ToArray();
        var unknown = Message("TV Fourth.Show.S01E01.1080p.WEB-GRP");
        Assert.Null(ImpactSimulator.Evaluate(Change(AgentCategories.AnnounceRule), before, after,
            new(known, [.. Gains(), unknown])).RejectionReason);
        var excess = Enumerable.Range(0, 4).Select(i => Message($"TV Unknown.Show.S01E0{i}.1080p.WEB-GRP"));
        Assert.Equal("impact-unverified-announces", ImpactSimulator.Evaluate(Change(AgentCategories.AnnounceRule), before, after,
            new(known, [.. Gains(), .. excess])).RejectionReason);
    }

    [Fact]
    public void Tightening_unaccepted_plausible_captures_does_not_claim_live_losses()
    {
        var before = Config();
        AddLooseAnnounceRule(before);
        var after = Clone(before);
        after.Servers[0].Irc.AnnounceRules.Clear();
        var summary = ImpactSimulator.Evaluate(Change(AgentCategories.AnnounceRule), before, after,
            new(KnownGains(), Gains())); // wishlist/race evidence is not proof of live acceptance here
        Assert.Equal(0, summary.Lost);
        Assert.Null(summary.RejectionReason);
    }

    [Fact]
    public void Tightened_mapping_can_fall_back_to_the_runtime_fuzzy_section()
    {
        var before = Config();
        before.Servers[0].SpreadSite.Sections["TV_1080"] = "/tv1080";
        before.Servers[0].SpreadSite.SectionMappings[0].RemoteSection = "TV_1080";
        var after = Clone(before);
        after.Servers[0].SpreadSite.SectionMappings[0].TriggerRegex = @"\.1080p\.";
        var evidence = new ReplayEvidence([new("alpha", "TV-HD", "Synthetic.Show.S01E01.720p.WEB-GRP", "race", true)], []);
        var summary = ImpactSimulator.Evaluate(Change(AgentCategories.SectionMapping), before, after, evidence);
        Assert.Null(summary.RejectionReason);
        Assert.Equal(1, summary.Changed);
        Assert.Equal(0, summary.Lost);
        after.Servers[0].SpreadSite.Sections.Remove("TV");
        Assert.Equal("impact-destructive", ImpactSimulator.Evaluate(Change(AgentCategories.SectionMapping), before, after, evidence).RejectionReason);
    }

    [Fact]
    public void Malformed_local_irc_evidence_is_marked_incomplete()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var logs = Path.Combine(_root, "irc-logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, $"alpha-{now.ToLocalTime():yyyy-MM-dd}.log"), "malformed synthetic log line\n");
        var evidence = ImpactSimulator.Read(_root, Config(), now);
        Assert.False(evidence.Complete);
    }

    [Theory]
    [InlineData("race")]
    [InlineData("wishlist")]
    public void Admission_replay_uses_mapped_section_and_preserves_protected_releases(string source)
    {
        var before = Config();
        var after = Clone(before);
        after.Servers[0].SpreadSite.Skiplist.Add(new() { Pattern = "*", Section = "TV", Action = SkiplistAction.Deny });
        var summary = ImpactSimulator.Evaluate(Change(), before, after, ReleaseEvidence(source));
        Assert.Equal(1, summary.Changed);
        Assert.Equal(1, summary.Lost);
        Assert.Equal("impact-destructive", summary.RejectionReason);
        Assert.Empty(before.Servers[0].SpreadSite.Skiplist);
    }

    [Fact]
    public void In_race_only_rules_do_not_change_directory_admission()
    {
        var before = Config();
        var after = Clone(before);
        after.Servers[0].SpreadSite.Skiplist.Add(new()
            { Pattern = "*", Action = SkiplistAction.Deny, Scope = SkiplistScope.InRace });
        var summary = ImpactSimulator.Evaluate(Change(), before, after, ReleaseEvidence());
        Assert.Equal(0, summary.Changed);
        Assert.Equal(0, summary.Lost);
        Assert.Equal("impact-no-effect", summary.RejectionReason);
    }

    [Fact]
    public void File_only_rules_do_not_change_directory_admission()
    {
        var before = Config();
        var after = Clone(before);
        after.Servers[0].SpreadSite.Skiplist.Add(new()
            { Pattern = "*", Action = SkiplistAction.Deny, MatchDirectories = false, MatchFiles = true });
        var summary = ImpactSimulator.Evaluate(Change(), before, after, ReleaseEvidence());
        Assert.Equal("impact-no-effect", summary.RejectionReason);
        Assert.Equal(0, summary.Lost);
    }

    [Fact]
    public void Unprotected_observed_releases_can_be_deliberately_filtered()
    {
        var before = Config();
        var after = Clone(before);
        after.Servers[0].SpreadSite.Skiplist.Add(new() { Pattern = "*", Action = SkiplistAction.Deny });
        var summary = ImpactSimulator.Evaluate(Change(), before, after, ReleaseEvidence("announce", false));
        Assert.Null(summary.RejectionReason);
        Assert.Equal(1, summary.Changed);
        Assert.Equal(0, summary.Lost);
    }

    [Theory]
    [InlineData(false, "impact-evidence-unavailable")]
    [InlineData(true, "impact-no-samples")]
    public void Missing_evidence_refuses_only_the_proposal(bool complete, string reason)
    {
        var before = Config();
        var serialized = JsonSerializer.Serialize(before);
        var summary = ImpactSimulator.Evaluate(Change(), before, Clone(before), new([], [], complete));
        Assert.Equal(reason, summary.RejectionReason);
        Assert.Equal(serialized, JsonSerializer.Serialize(before));
    }

    [Fact]
    public void Counterfactual_with_unchanged_decisions_is_a_no_effect_proposal()
    {
        var before = Config();
        var summary = ImpactSimulator.Evaluate(Change(), before, Clone(before), ReleaseEvidence());
        Assert.Equal("impact-no-effect", summary.RejectionReason);
    }

    [Fact]
    public void Listing_exclusions_do_not_claim_to_block_irc_delivery()
    {
        var before = Config();
        var after = Clone(before);
        after.Servers[0].Notifications.ExcludedCategories.Add("TV-HD");
        var summary = ImpactSimulator.Evaluate(Change(AgentCategories.ExcludedCategories), before, after,
            ReleaseEvidence("announce", true));
        Assert.Equal(1, summary.Changed);
        Assert.Equal(0, summary.Lost);
        Assert.Null(summary.RejectionReason);
    }

    [Fact]
    public void Listing_exclusions_preserve_a_known_wanted_category_without_delivery_proof()
    {
        var before = Config();
        var after = Clone(before);
        after.Servers[0].Notifications.ExcludedCategories.Add("TV-HD");
        var summary = ImpactSimulator.Evaluate(Change(AgentCategories.ExcludedCategories), before, after,
            ReleaseEvidence("wishlist", true));
        Assert.Equal("impact-destructive", summary.RejectionReason);
    }

    [Fact]
    public void Default_replay_without_provider_rejects_before_mutating_live_config()
    {
        var config = Config();
        var applier = new ChangeApplier([new SkiplistValidator()], new FreezeStore(_root), new AuditTrail(_root));
        var rows = new List<AuditRow>();
        var report = applier.Apply([Change()], config, config.Agent, "synthetic-run", false, rows.Add);
        Assert.Equal(0, report.Applied);
        Assert.Equal(1, report.Rejected);
        Assert.Empty(config.Servers[0].SpreadSite.Skiplist);
        Assert.Equal("impact-evidence-unavailable", Assert.Single(rows).RejectionReason);
    }

    [Theory]
    [InlineData(AgentCategories.WishlistPrune)]
    [InlineData(AgentCategories.ErrorReport)]
    public void Nonconfig_dry_run_never_invokes_external_mutation(string category)
    {
        var calls = 0;
        var validator = new ExternalValidator(category, () => calls++);
        var config = Config();
        var applier = new ChangeApplier([validator], new FreezeStore(_root), new AuditTrail(_root));
        var report = applier.Apply([new AgentChange
            { Category = category, Target = "/external/item", Confidence = 1, After = "synthetic" }],
            config, config.Agent, "synthetic-run", dryRun: true);
        Assert.Equal(1, report.Applied);
        Assert.Equal(0, calls);
    }

    private sealed class ExternalValidator(string category, Action action) : IChangeValidator
    {
        public string Category => category;
        public ValidationResult Validate(AgentChange change, AppConfig config) => new(true, null, _ => action());
    }

    public void Dispose() => Directory.Delete(_root, true);
}
