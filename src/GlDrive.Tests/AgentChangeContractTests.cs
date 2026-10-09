using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GlDrive.AiAgent;
using GlDrive.Config;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// The AI agent's proposals and the validators disagreed about what a change looks like, and the
/// audit trail recorded the disagreement as success.
///
/// 2026-09-30 sweep of ai-audit.jsonl:
///   1. maxConcurrentRaces 1 → 2 was "applied" on 50 consecutive days. Every server runs
///      loginCap 4 / headroom 2, so the login ceiling is 1: the validator clamped the proposal back
///      to 1, wrote 1 over 1, and the audit row said Applied=true, After=2. The model's memo then
///      recorded the raise, it saw 1 the next day, and proposed it again — spending the
///      poolSizing budget on a no-op every run.
///   2. The model addresses the REAL config keys (/servers/{id}/spreadSite/sectionMappings,
///      .../spreadSite/skiplist, .../spreadSite/priority) while the validators accepted only an
///      invented alias (/servers/{id}/spread/sectionMappings, .../spread/skiplistRules,
///      .../spread/sitePriority). Not one sectionMapping change applied in September.
///   3. A trigger patch at .../sectionMappings/11/triggerRegex (a string) could never parse —
///      the validator only understood whole-object patches at .../sectionMappings/11.
/// </summary>
public class AgentChangeContractTests : IDisposable
{
    private readonly string _dir;

    public AgentChangeContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gldrive-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static AppConfig Config(int maxConcurrent = 1, int loginCap = 4, int headroom = 2)
    {
        var cfg = new AppConfig();
        cfg.Spread.MaxConcurrentRaces = maxConcurrent;
        var site = new ServerConfig
        {
            Id = "bb90928a",
            Name = "Site1",
            Pool = new PoolConfig { LoginCap = loginCap, LoginHeadroom = headroom },
        };
        site.SpreadSite.Sections["TV_HD"] = "/incoming/tv-hd";
        site.SpreadSite.SectionMappings.Add(new SectionMapping { IrcSection = "tv-hd", RemoteSection = "TV_HD", TriggerRegex = ".*" });
        cfg.Servers.Add(site);
        return cfg;
    }

    private (ChangeApplier applier, List<AuditRow> rows) Applier()
    {
        var validators = new IChangeValidator[]
        {
            new PoolSizingValidator(), new SectionMappingValidator(), new SkiplistValidator(),
            new PriorityValidator(), new DownloadOnlyValidator(),
        };
        return (new ChangeApplier(validators, new FreezeStore(_dir), new AuditTrail(_dir)), new List<AuditRow>());
    }

    private static AgentChange Change(string category, string target, object? after, object? before = null) => new()
    {
        Category = category, Target = target, After = after, Before = before, Confidence = 0.95,
    };

    // ---- 1. A clamp that collapses the change to the current value is not an apply ----

    [Fact]
    public void MaxConcurrentRaces_at_login_ceiling_is_rejected_not_applied()
    {
        // loginCap 4, headroom 2 → usable 2 → ceiling 1. Config already at 1.
        var cfg = Config(maxConcurrent: 1);
        var result = new PoolSizingValidator().Validate(Change(AgentCategories.PoolSizing, "/spread/maxConcurrentRaces", 2), cfg);

        Assert.False(result.Ok);
        Assert.Equal("at-login-ceiling", result.RejectionReason);
    }

    [Fact]
    public void Applier_records_a_mutation_that_changes_nothing_as_rejected()
    {
        // A whole-object patch against a USER-EDITED trigger passes validation (the validator cannot
        // see the edit guard) but the mutation preserves the edit — a no-op. It must not be audited
        // as applied or consume budget.
        var cfg = Config();
        cfg.Servers[0].SpreadSite.SectionMappings[0].TriggerRegex = "(?i)user-edited";
        var (applier, rows) = Applier();

        var report = applier.Apply(new[]
        {
            Change(AgentCategories.SectionMapping, "/servers/bb90928a/spread/sectionMappings/0",
                new { ircSection = "tv-hd", remoteSection = "TV_HD", triggerRegex = "(?i)\\.1080p\\." }),
        }, cfg, cfg.Agent, "run1", dryRun: false, rows.Add, configOnly: true);

        Assert.Equal(0, report.Applied);
        var row = Assert.Single(rows);
        Assert.False(row.Applied);
        Assert.Equal("no-effect", row.RejectionReason);
        Assert.Equal("(?i)user-edited", cfg.Servers[0].SpreadSite.SectionMappings[0].TriggerRegex);
    }

    [Fact]
    public void Applier_still_applies_a_mutation_that_changes_config()
    {
        var cfg = Config(maxConcurrent: 1, loginCap: 6, headroom: 1); // ceiling 4
        var (applier, rows) = Applier();

        var report = applier.Apply(new[] { Change(AgentCategories.PoolSizing, "/spread/maxConcurrentRaces", 2, before: 1) },
            cfg, cfg.Agent, "run1", dryRun: false, rows.Add, configOnly: true);

        Assert.Equal(1, report.Applied);
        Assert.True(Assert.Single(rows).Applied);
        Assert.Equal(2, cfg.Spread.MaxConcurrentRaces);
    }

    // ---- 2. Real config paths address the same fields as the validator aliases ----

    [Theory]
    [InlineData("/servers/bb90928a/spreadSite/sectionMappings/-", "/servers/bb90928a/spread/sectionMappings/-")]
    [InlineData("/servers/bb90928a/spreadSite/skiplist/-", "/servers/bb90928a/spread/skiplistRules/-")]
    [InlineData("/servers/bb90928a/spreadSite/skiplist/3", "/servers/bb90928a/spread/skiplistRules/3")]
    [InlineData("/servers/bb90928a/spreadSite/priority", "/servers/bb90928a/spread/sitePriority")]
    [InlineData("/servers/bb90928a/spreadSite/downloadOnly", "/servers/bb90928a/spread/downloadOnly")]
    [InlineData("/servers/bb90928a/spreadSite/maxUploadSlots", "/servers/bb90928a/spread/maxUploadSlots")]
    [InlineData("/servers/bb90928a/spread/skiplist/-", "/servers/bb90928a/spread/skiplistRules/-")]
    [InlineData("/servers/bb90928a/spread/priority", "/servers/bb90928a/spread/sitePriority")]
    [InlineData("/servers/0/spreadSite/affils/-", "/servers/bb90928a/spread/affils/-")]
    // Already canonical, or not server-scoped: untouched.
    [InlineData("/servers/bb90928a/spread/skiplistRules/-", "/servers/bb90928a/spread/skiplistRules/-")]
    [InlineData("/servers/bb90928a/irc/announceRules/-", "/servers/bb90928a/irc/announceRules/-")]
    [InlineData("/spread/maxConcurrentRaces", "/spread/maxConcurrentRaces")]
    [InlineData("/servers/7/spreadSite/priority", "/servers/7/spread/sitePriority")] // index out of range: id stays as given
    public void Canonicalize_maps_real_config_paths_to_validator_paths(string input, string expected)
    {
        Assert.Equal(expected, ChangeApplier.CanonicalizeTarget(input, Config()));
    }

    [Fact]
    public void Real_path_section_mapping_append_is_applied()
    {
        var cfg = Config();
        cfg.Servers[0].SpreadSite.Sections["TV_2160"] = "/incoming/tv-2160";
        var (applier, rows) = Applier();

        applier.Apply(new[]
        {
            Change(AgentCategories.SectionMapping, "/servers/bb90928a/spreadSite/sectionMappings/-",
                new { ircSection = "tv-hd", remoteSection = "TV_2160", triggerRegex = "(?i)\\.2160p\\." }),
        }, cfg, cfg.Agent, "run1", dryRun: false, rows.Add, configOnly: true);

        Assert.True(Assert.Single(rows).Applied);
        Assert.Contains(cfg.Servers[0].SpreadSite.SectionMappings, m => m.RemoteSection == "TV_2160");
    }

    [Fact]
    public void Freeze_in_alias_form_still_blocks_a_real_path_proposal()
    {
        // The UI stores freezes as /servers/{id}/spread/sitePriority. Accepting the real-path form
        // must not become a way around a user's freeze.
        var cfg = Config();
        var freeze = new FreezeStore(_dir);
        freeze.Freeze("/servers/bb90928a/spread/sitePriority");
        var rows = new List<AuditRow>();
        var applier = new ChangeApplier(new IChangeValidator[] { new PriorityValidator() }, freeze, new AuditTrail(_dir));

        applier.Apply(new[] { Change(AgentCategories.Priority, "/servers/bb90928a/spreadSite/priority", "High") },
            cfg, cfg.Agent, "run1", dryRun: false, rows.Add, configOnly: true);

        Assert.Equal("frozen", Assert.Single(rows).RejectionReason);
    }

    // 2026-10-05: gpt-oss returned an all-empty change. An empty Target is the JSON Pointer ROOT,
    // an ancestor of every freeze, so the audit called it "frozen" — blaming the user's freeze
    // list for the model's malformed output.
    [Theory]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData(AgentCategories.Priority, "")]
    [InlineData("", "/servers/bb90928a/spread/sitePriority")]
    public void Change_missing_category_or_target_is_rejected_as_malformed_not_frozen(string category, string target)
    {
        var cfg = Config();
        var freeze = new FreezeStore(_dir);
        freeze.Freeze("/servers/bb90928a/spread/sectionMappings");
        var rows = new List<AuditRow>();
        var applier = new ChangeApplier(new IChangeValidator[] { new PriorityValidator() }, freeze, new AuditTrail(_dir));

        var report = applier.Apply(new[] { Change(category, target, "High") },
            cfg, cfg.Agent, "run1", dryRun: false, rows.Add, configOnly: true);

        Assert.Equal("malformed", Assert.Single(rows).RejectionReason);
        Assert.Equal(1, report.RejectionByReason.GetValueOrDefault("malformed"));
    }

    [Fact]
    public void Null_category_and_target_are_rejected_as_malformed()
    {
        var cfg = Config();
        var (applier, rows) = Applier();

        applier.Apply(new[] { new AgentChange { Category = null!, Target = null!, Confidence = 0.95 } },
            cfg, cfg.Agent, "run1", dryRun: false, rows.Add, configOnly: true);

        Assert.Equal("malformed", Assert.Single(rows).RejectionReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n")]
    public void Brief_without_model_markdown_says_so(string? markdown)
    {
        var brief = AgentRunner.ComposeBrief(markdown, "\n---\nfooter");

        Assert.StartsWith("# (model returned no brief)", brief);
        Assert.EndsWith("footer", brief);
    }

    [Fact]
    public void Brief_with_model_markdown_is_kept_verbatim()
    {
        Assert.Equal("## Summary\n- ok\n---\nf", AgentRunner.ComposeBrief("## Summary\n- ok", "\n---\nf"));
    }

    // ---- 3. A field-level trigger patch is the natural JSON Pointer shape ----

    [Fact]
    public void Trigger_field_patch_on_default_trigger_is_applied()
    {
        var cfg = Config();
        var (applier, rows) = Applier();

        applier.Apply(new[]
        {
            Change(AgentCategories.SectionMapping, "/servers/bb90928a/spreadSite/sectionMappings/0/triggerRegex",
                "(?i)\\.(1080p|2160p)\\.", before: ".*"),
        }, cfg, cfg.Agent, "run1", dryRun: false, rows.Add, configOnly: true);

        Assert.True(Assert.Single(rows).Applied);
        Assert.Equal("(?i)\\.(1080p|2160p)\\.", cfg.Servers[0].SpreadSite.SectionMappings[0].TriggerRegex);
    }

    [Fact]
    public void Trigger_field_patch_on_user_edited_trigger_is_rejected_by_name()
    {
        var cfg = Config();
        cfg.Servers[0].SpreadSite.SectionMappings[0].TriggerRegex = "(?i)mine";
        var result = new SectionMappingValidator().Validate(
            Change(AgentCategories.SectionMapping, "/servers/bb90928a/spread/sectionMappings/0/triggerRegex", "(?i)\\.1080p\\."), cfg);

        Assert.False(result.Ok);
        Assert.Equal("trigger-user-edited", result.RejectionReason);
    }

    [Fact]
    public void Trigger_field_patch_with_bad_regex_is_rejected()
    {
        var result = new SectionMappingValidator().Validate(
            Change(AgentCategories.SectionMapping, "/servers/bb90928a/spread/sectionMappings/0/triggerRegex", "(unclosed"), Config());

        Assert.False(result.Ok);
        Assert.Equal("trigger-bad-regex", result.RejectionReason);
    }

    // ---- 3b. Object values arrive in the config's camelCase with string enums ----

    private static object ModelJson(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void CamelCase_skiplist_rule_with_string_enum_is_read_faithfully()
    {
        // Verbatim shape of the prompt's own AgentChange example.
        var cfg = Config();
        var result = new SkiplistValidator().Validate(Change(AgentCategories.Skiplist, "/servers/bb90928a/spread/skiplistRules/-",
            ModelJson("""{ "pattern": "*DUBBED*", "isRegex": false, "action": "Deny", "matchDirectories": true }""")), cfg);

        Assert.True(result.Ok, result.RejectionReason);
        result.Mutate!(cfg);
        var rule = Assert.Single(cfg.Servers[0].SpreadSite.Skiplist);
        Assert.Equal("*DUBBED*", rule.Pattern);
        Assert.Equal(SkiplistAction.Deny, rule.Action);
    }

    [Fact]
    public void CamelCase_skiplist_rule_with_numeric_enum_is_read_faithfully()
    {
        // The config itself serializes enums as numbers; the model copies either form.
        var cfg = Config();
        var result = new SkiplistValidator().Validate(Change(AgentCategories.Skiplist, "/servers/bb90928a/spread/skiplistRules/-",
            ModelJson("""{ "pattern": "*tv-bluray*", "isRegex": false, "action": 1 }""")), cfg);

        Assert.True(result.Ok, result.RejectionReason);
        result.Mutate!(cfg);
        Assert.Equal("*tv-bluray*", Assert.Single(cfg.Servers[0].SpreadSite.Skiplist).Pattern);
    }

    [Fact]
    public void CamelCase_section_mapping_append_keeps_its_remote_section()
    {
        var cfg = Config();
        var result = new SectionMappingValidator().Validate(Change(AgentCategories.SectionMapping, "/servers/bb90928a/spread/sectionMappings/-",
            ModelJson("""{ "ircSection": "tv-hd", "remoteSection": "TV_HD", "triggerRegex": "(?i)\\.1080p\\." }""")), cfg);

        Assert.True(result.Ok, result.RejectionReason);
    }

    [Fact]
    public void CamelCase_announce_rule_is_read_faithfully()
    {
        var parsed = ChangeValueJson.Read<IrcAnnounceRule>(ModelJson("""{ "channel": "#ent", "pattern": "NEW RELEASE: (?<section>\\S+) (?<release>\\S+)" }"""));
        Assert.Equal("#ent", parsed!.Channel);
        Assert.StartsWith("NEW RELEASE", parsed.Pattern);
    }

    [Fact]
    public void Error_report_writes_outside_config_and_is_not_judged_no_effect()
    {
        // The manual suggestion-apply path (not configOnly) can reach errorReport; its effect is a
        // file, so a config diff must not overrule it.
        var cfg = Config();
        var rows = new List<AuditRow>();
        var applier = new ChangeApplier(new IChangeValidator[] { new ErrorReportValidator(_dir) }, new FreezeStore(_dir), new AuditTrail(_dir));

        applier.Apply(new[] { Change(AgentCategories.ErrorReport, "issues/pool-drift", "# report") },
            cfg, cfg.Agent, "run1", dryRun: false, rows.Add);

        Assert.True(Assert.Single(rows).Applied);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "ai-briefs", "issues")));
    }

    // ---- 4. The model is told WHICH changes were refused and why ----

    [Fact]
    public void Prompt_carries_rejection_detail_lines_not_just_three_count_lines()
    {
        var rows = Enumerable.Range(0, 3).SelectMany(run => new[]
        {
            new AuditRow { RunId = $"run{run}xxxxx", Category = "poolSizing", Target = "/spread/maxConcurrentRaces", After = 2, Applied = false, RejectionReason = "at-login-ceiling" },
            new AuditRow { RunId = $"run{run}xxxxx", Category = "sectionMapping", Target = $"/servers/s/spread/sectionMappings/{run}/triggerRegex", After = "x", Applied = false, RejectionReason = "trigger-user-edited" },
        });

        var prompt = new AgentPrompt().Compose(new DigestBundle(), "memo", [],
            System.Text.Json.Nodes.JsonNode.Parse("""{"a":1}""")!, AgentRunner.SummarizeRecentRuns(rows));

        // All three runs' rejections survive, including the OLDEST run's (listed last).
        Assert.Contains("/servers/s/spread/sectionMappings/0/triggerRegex", prompt);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(prompt, "at-login-ceiling").Count);
    }

    [Fact]
    public void Recent_run_summary_names_each_rejected_change_and_its_reason()
    {
        var rows = new[]
        {
            new AuditRow { RunId = "aaaaaaaa-1", Category = "poolSizing", Target = "/spread/maxConcurrentRaces", After = 2, Applied = false, RejectionReason = "at-login-ceiling" },
            new AuditRow { RunId = "aaaaaaaa-1", Category = "excludedCategories", Target = "/servers/x/notifications/excludedCategories/-", After = "XXX", Applied = true },
        };

        var lines = AgentRunner.SummarizeRecentRuns(rows);

        var text = string.Join("\n", lines);
        Assert.Contains("applied=1 rejected=1", text);
        Assert.Contains("/spread/maxConcurrentRaces", text);
        Assert.Contains("at-login-ceiling", text);
        Assert.DoesNotContain("excludedCategories", text); // applied rows need no explanation
    }
}
