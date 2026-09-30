using System.IO;
using System.Text.Json;
using GlDrive.AiAgent;
using GlDrive.Config;
using Xunit;

namespace GlDrive.Tests;

public sealed class AgentMutationIntegrityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gldrive-mutation-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static AppConfig Config()
    {
        var cfg = new AppConfig();
        cfg.Servers.Add(new ServerConfig { Id = "site-a", Pool = new PoolConfig { LoginCap = 4, LoginHeadroom = 1 } });
        return cfg;
    }

    private static AgentChange Change(string category, string target, object? before, object? after) => new()
    {
        Category = category, Target = target, Before = before, After = after, Confidence = 0.99,
    };

    private List<AuditRow> Apply(AppConfig cfg, AgentChange[] changes, bool configOnly = true, FreezeStore? freeze = null)
    {
        var rows = new List<AuditRow>();
        new ChangeApplier(new IChangeValidator[]
        {
            new PoolSizingValidator(), new PriorityValidator(), new RequestFillerValidator(), new SkiplistValidator(),
        }, freeze ?? new FreezeStore(_dir), new AuditTrail(_dir))
            .Apply(changes, cfg, cfg.Agent, "test", dryRun: false, rows.Add, configOnly);
        return rows;
    }

    [Theory]
    [InlineData("/servers/site-a/spread/maxUploadSlots")]
    [InlineData("/servers/site-a/spreadSite/maxUploadSlots")]
    [InlineData("/servers/0/spreadSite/maxUploadSlots")]
    public void Server_before_check_rejects_stale_values_for_every_target_spelling(string target)
    {
        var cfg = Config();
        var row = Assert.Single(Apply(cfg, [Change(AgentCategories.PoolSizing, target, 2, 4)]));
        Assert.Equal("before-mismatch", row.RejectionReason);
        Assert.False(row.Applied);
        Assert.Equal(3, cfg.Servers[0].SpreadSite.MaxUploadSlots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Successive_changes_compare_against_current_config(bool configOnly)
    {
        var cfg = Config();
        var rows = Apply(cfg,
        [
            Change(AgentCategories.PoolSizing, "/spread/spreadPoolSize", 3, 4),
            Change(AgentCategories.PoolSizing, "/spread/spreadPoolSize", 4, 3),
        ], configOnly);
        Assert.All(rows, r => Assert.True(r.Applied, r.RejectionReason));
        Assert.Equal(3, cfg.Spread.SpreadPoolSize);
    }

    [Theory]
    [InlineData("/servers/site-a/spreadSite/priority")]
    [InlineData("/servers/0/spreadSite/priority")]
    [InlineData("/servers/0")]
    public void Freeze_paths_are_normalized_together_with_proposals(string frozenPath)
    {
        var cfg = Config();
        var freeze = new FreezeStore(_dir);
        freeze.Freeze(frozenPath);
        var row = Assert.Single(Apply(cfg,
            [Change(AgentCategories.Priority, "/servers/site-a/spread/sitePriority", "Normal", "High")], freeze: freeze));
        Assert.Equal("frozen", row.RejectionReason);
        Assert.Equal(SitePriority.Normal, cfg.Servers[0].SpreadSite.Priority);
    }

    [Fact]
    public void Applied_audit_records_actual_clamped_values()
    {
        var cfg = Config();
        cfg.Spread.MaxConcurrentRaces = 4;
        var row = Assert.Single(Apply(cfg, [Change(AgentCategories.PoolSizing, "/spread/maxConcurrentRaces", 4, 5)]));
        Assert.True(row.Applied, row.RejectionReason);
        Assert.Equal(2, cfg.Spread.MaxConcurrentRaces);
        Assert.Equal(4, JsonSerializer.SerializeToElement(row.Before).GetInt32());
        Assert.Equal(2, JsonSerializer.SerializeToElement(row.After).GetInt32());
    }

    [Fact]
    public void Applied_audit_captures_missing_manual_before_and_supports_priority_inverse()
    {
        var cfg = Config();
        var row = Assert.Single(Apply(cfg,
            [Change(AgentCategories.Priority, "/servers/site-a/spread/sitePriority", null, "High")], configOnly: false));
        Assert.True(row.Applied, row.RejectionReason);
        Assert.NotNull(row.Before);
        var inverse = Assert.Single(Apply(cfg, [Change(row.Category, row.Target, row.After, row.Before)], configOnly: false));
        Assert.True(inverse.Applied, inverse.RejectionReason);
        Assert.Equal(SitePriority.Normal, cfg.Servers[0].SpreadSite.Priority);
    }

    [Fact]
    public void Race_concurrency_can_be_reduced_to_one_below_login_ceiling()
    {
        var cfg = Config();
        cfg.Spread.MaxConcurrentRaces = 2;
        var row = Assert.Single(Apply(cfg, [Change(AgentCategories.PoolSizing, "/spread/maxConcurrentRaces", 2, 1)]));
        Assert.True(row.Applied, row.RejectionReason);
        Assert.Equal(1, cfg.Spread.MaxConcurrentRaces);
    }

    [Theory]
    [InlineData("enabled", false, true)]
    [InlineData("pattern", "old", @"NEW (?<release>\S+)")]
    [InlineData("channel", "#old", "#new")]
    public void Request_filler_fields_accept_documented_paths(string field, object before, object after)
    {
        var cfg = Config();
        cfg.Servers[0].Irc.RequestFiller.Pattern = "old";
        cfg.Servers[0].Irc.RequestFiller.Channel = "#old";
        var row = Assert.Single(Apply(cfg,
            [Change(AgentCategories.RequestFiller, $"/servers/site-a/irc/requestFiller/{field}", before, after)]));
        Assert.True(row.Applied, row.RejectionReason);
        var value = JsonSerializer.SerializeToElement(cfg.Servers[0].Irc.RequestFiller,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }).GetProperty(field);
        Assert.Equal(JsonSerializer.SerializeToElement(after).ToString(), value.ToString());
    }

    [Fact]
    public void Before_comparison_decodes_json_strings_without_losing_regex_escapes()
    {
        var cfg = Config();
        cfg.Servers[0].Irc.RequestFiller.Pattern = @"OLD (?<release>\S+)";
        var row = Assert.Single(Apply(cfg,
            [Change(AgentCategories.RequestFiller, "/servers/site-a/irc/requestFiller/pattern",
                @"OLD (?<release>\S+)", @"NEW (?<release>\S+)")]));
        Assert.True(row.Applied, row.RejectionReason);
        Assert.Equal(@"NEW (?<release>\S+)", cfg.Servers[0].Irc.RequestFiller.Pattern);
    }

    [Fact]
    public void Null_skiplist_append_is_rejected_without_poisoning_config()
    {
        var cfg = Config();
        var row = Assert.Single(Apply(cfg,
            [Change(AgentCategories.Skiplist, "/servers/site-a/spread/skiplistRules/-", new { pattern = "old" }, null)]));
        Assert.False(row.Applied);
        Assert.Equal("after-null", row.RejectionReason);
        Assert.Empty(cfg.Servers[0].SpreadSite.Skiplist);
    }

    [Fact]
    public void Missing_target_separator_cannot_bypass_a_frozen_rule()
    {
        var cfg = Config();
        cfg.Servers[0].SpreadSite.Skiplist.Add(new SkiplistRule { Pattern = "old" });
        var freeze = new FreezeStore(_dir);
        freeze.Freeze("/servers/site-a/spread/skiplistRules/0");
        var row = Assert.Single(Apply(cfg,
            [Change(AgentCategories.Skiplist, "/servers/site-a/spread/skiplistRules0", null, new { pattern = "new" })], freeze: freeze));
        Assert.False(row.Applied);
        Assert.Equal("target-shape-unsupported", row.RejectionReason);
        Assert.Equal("old", cfg.Servers[0].SpreadSite.Skiplist[0].Pattern);
    }

    [Fact]
    public void Whole_rule_replacement_cannot_change_a_frozen_child_property()
    {
        var cfg = Config();
        cfg.Servers[0].SpreadSite.Skiplist.Add(new SkiplistRule { Pattern = "old" });
        var freeze = new FreezeStore(_dir);
        freeze.Freeze("/servers/0/spreadSite/skiplist/0/pattern");
        var row = Assert.Single(Apply(cfg,
            [Change(AgentCategories.Skiplist, "/servers/site-a/spread/skiplistRules/0", null, new { pattern = "new" })], freeze: freeze));
        Assert.Equal("frozen", row.RejectionReason);
        Assert.Equal("old", cfg.Servers[0].SpreadSite.Skiplist[0].Pattern);
    }

    [Fact]
    public void Named_priority_before_matches_persisted_numeric_tier()
    {
        var cfg = Config();
        var row = Assert.Single(Apply(cfg,
            [Change(AgentCategories.Priority, "/servers/site-a/spreadSite/priority", "Normal", "High")]));
        Assert.True(row.Applied, row.RejectionReason);
        Assert.Equal(SitePriority.High, cfg.Servers[0].SpreadSite.Priority);
    }

    [Fact]
    public void Append_audit_contains_actual_new_rule_and_delete_does_not_record_shifted_neighbor()
    {
        var cfg = Config();
        var append = Assert.Single(Apply(cfg,
            [Change(AgentCategories.Skiplist, "/servers/site-a/spread/skiplistRules/-", null, new { pattern = "new" })]));
        Assert.True(append.Applied, append.RejectionReason);
        Assert.Null(append.Before);
        Assert.Equal("new", JsonSerializer.SerializeToElement(append.After).GetProperty("pattern").GetString());
        cfg.Servers[0].SpreadSite.Skiplist.Add(new SkiplistRule { Pattern = "neighbor" });
        var delete = Assert.Single(Apply(cfg,
            [Change(AgentCategories.Skiplist, "/servers/site-a/spread/skiplistRules/0", append.After, null)]));
        Assert.True(delete.Applied, delete.RejectionReason);
        Assert.Equal("new", JsonSerializer.SerializeToElement(delete.Before).GetProperty("pattern").GetString());
        Assert.Null(delete.After);
        Assert.Equal("neighbor", Assert.Single(cfg.Servers[0].SpreadSite.Skiplist).Pattern);
    }

    [Theory]
    [InlineData("00")]
    [InlineData("+0")]
    public void Alternative_numeric_rule_index_cannot_evade_freeze(string index)
    {
        var cfg = Config();
        cfg.Servers[0].SpreadSite.Skiplist.Add(new SkiplistRule { Pattern = "old" });
        var freeze = new FreezeStore(_dir);
        freeze.Freeze("/servers/site-a/spread/skiplistRules/0");
        var row = Assert.Single(Apply(cfg,
            [Change(AgentCategories.Skiplist, $"/servers/site-a/spread/skiplistRules/{index}", null, new { pattern = "new" })], freeze: freeze));
        Assert.Equal("frozen", row.RejectionReason);
        Assert.Equal("old", cfg.Servers[0].SpreadSite.Skiplist[0].Pattern);
    }

    [Fact]
    public void Deletion_cannot_shift_a_later_frozen_rule_out_of_its_protected_index()
    {
        var cfg = Config();
        cfg.Servers[0].SpreadSite.Skiplist.Add(new SkiplistRule { Pattern = "old" });
        cfg.Servers[0].SpreadSite.Skiplist.Add(new SkiplistRule { Pattern = "protected" });
        var freeze = new FreezeStore(_dir);
        freeze.Freeze("/servers/0/spreadSite/skiplist/1/pattern");
        var row = Assert.Single(Apply(cfg,
            [Change(AgentCategories.Skiplist, "/servers/site-a/spread/skiplistRules/0", new { pattern = "old" }, null)], freeze: freeze));
        Assert.Equal("frozen", row.RejectionReason);
        Assert.Equal(2, cfg.Servers[0].SpreadSite.Skiplist.Count);
        Assert.Equal("protected", cfg.Servers[0].SpreadSite.Skiplist[1].Pattern);
    }

    [Fact]
    public void Null_channel_proposal_audits_actual_empty_string()
    {
        var cfg = Config();
        cfg.Servers[0].Irc.RequestFiller.Channel = "#old";
        var row = Assert.Single(Apply(cfg,
            [Change(AgentCategories.RequestFiller, "/servers/site-a/irc/requestFiller/channel", "#old", null)]));
        Assert.True(row.Applied, row.RejectionReason);
        Assert.Equal("", cfg.Servers[0].Irc.RequestFiller.Channel);
        Assert.Equal("", JsonSerializer.SerializeToElement(row.After).GetString());
    }
}
