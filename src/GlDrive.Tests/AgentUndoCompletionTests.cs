using System.IO;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class AgentUndoCompletionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-undo-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("frozen")]
    [InlineData("before-mismatch")]
    public void Rejected_inverse_keeps_original_audit_and_does_not_save_or_emit_override(string rejection)
    {
        var config = new AppConfig();
        config.Spread.SpreadPoolSize = 4;
        var audit = new AuditTrail(_root);
        audit.Append(new AuditRow
        {
            RunId = "original", Category = AgentCategories.PoolSizing,
            Target = "/spread/spreadPoolSize", Before = 3, After = 4, Applied = true
        });
        var freeze = new FreezeStore(_root);
        if (rejection == "frozen") freeze.Freeze("/spread/spreadPoolSize");
        var applier = new ChangeApplier(new[] { new PoolSizingValidator() }, freeze, audit);
        var report = applier.Apply(new[] { new AgentChange
        {
            Category = AgentCategories.PoolSizing, Target = "/spread/spreadPoolSize",
            Before = rejection == "before-mismatch" ? 5 : 4, After = 3, Confidence = 1.0
        } }, config, config.Agent, "undo", dryRun: false);
        var effects = new List<string>();

        var completed = AgentUndoCompletion.TryComplete(report,
            () => effects.Add("save"),
            () => audit.MarkUndone("original", "/spread/spreadPoolSize", "user-click"),
            () => effects.Add("override"), out var failure);

        Assert.False(completed);
        Assert.Contains(rejection, failure);
        Assert.Empty(effects);
        Assert.False(audit.ReadAll().Single(r => r.RunId == "original").Undone);
        Assert.Equal(4, config.Spread.SpreadPoolSize);
    }

    [Fact]
    public void Successful_inverse_persists_before_marking_and_reporting_override()
    {
        var effects = new List<string>();
        var completed = AgentUndoCompletion.TryComplete(new() { Applied = 1 },
            () => effects.Add("save"), () => effects.Add("mark"),
            () => effects.Add("override"), out var failure);

        Assert.True(completed);
        Assert.Equal(new[] { "save", "mark", "override" }, effects);
        Assert.Empty(failure);
    }

    [Fact]
    public void Save_failure_does_not_mark_original_or_emit_override()
    {
        var effects = new List<string>();
        Assert.Throws<IOException>(() => AgentUndoCompletion.TryComplete(new() { Applied = 1 },
            () => throw new IOException("Synthetic write failure"), () => effects.Add("mark"),
            () => effects.Add("override"), out _));
        Assert.Empty(effects);
    }

    [Fact]
    public void Missing_change_service_does_not_report_success()
    {
        var effects = new List<string>();
        Assert.False(AgentUndoCompletion.TryComplete(null, () => effects.Add("save"),
            () => effects.Add("mark"), () => effects.Add("override"), out var failure));
        Assert.Contains("unavailable", failure);
        Assert.Empty(effects);
    }

    [Fact]
    public void Undo_of_deleted_rule_preserves_shifted_neighbor_and_original_audit()
    {
        var config = new AppConfig();
        var site = new ServerConfig { Id = "site-a" };
        site.SpreadSite.Skiplist.Add(new SkiplistRule { Pattern = "neighbor" });
        config.Servers.Add(site);
        var audit = new AuditTrail(_root);
        var row = new AuditRow
        {
            RunId = "deleted-rule", Category = AgentCategories.Skiplist,
            Target = "/servers/site-a/spread/skiplistRules/0", Before = new { pattern = "removed" },
            After = null, Applied = true
        };
        audit.Append(row);
        var applier = new ChangeApplier(new[] { new SkiplistValidator() }, new FreezeStore(_root), audit);
        var mutationAttempted = false;
        var report = AgentUndoCompletion.ApplyInverse(row, () =>
        {
            mutationAttempted = true;
            return applier.Apply(new[] { new AgentChange
            {
                Category = row.Category, Target = row.Target, Before = row.After,
                After = row.Before, Confidence = 1.0
            } }, config, config.Agent, "undo", dryRun: false);
        });
        var saved = false;
        var completed = AgentUndoCompletion.TryComplete(report, () => saved = true,
            () => audit.MarkUndone(row.RunId, row.Target, "user-click"), null, out var failure);

        Assert.False(completed);
        Assert.False(mutationAttempted);
        Assert.False(saved);
        Assert.Contains("removed rules require manual restoration", failure);
        Assert.Equal("neighbor", Assert.Single(site.SpreadSite.Skiplist).Pattern);
        Assert.False(Assert.Single(audit.ReadAll()).Undone);
    }

    [Fact]
    public void Undo_of_append_is_rejected_before_attempting_inverse_mutation()
    {
        var attempted = false;
        var row = new AuditRow
        {
            RunId = "append", Category = AgentCategories.Skiplist,
            Target = "/servers/site-a/spread/skiplistRules/-", Applied = true,
            After = new { pattern = "new" }
        };
        var report = AgentUndoCompletion.ApplyInverse(row, () =>
        {
            attempted = true;
            return new() { Applied = 1 };
        });
        Assert.False(attempted);
        Assert.Equal(1, report!.Rejected);
        Assert.Contains("appended entries require manual removal", Assert.Single(report.RejectionByReason).Key);
    }
}
