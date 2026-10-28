using System.IO;
using System.Text.Json;
using GlDrive.AiAgent;
using GlDrive.Config;
using Xunit;

namespace GlDrive.Tests;

public sealed class AgentAccountabilityIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-accountability-" + Guid.NewGuid().ToString("N"));
    public AgentAccountabilityIntegrationTests() => Directory.CreateDirectory(_root);

    private static AppConfig Config(bool revert = true) => new()
    {
        Agent = new() { AutoRevertRegressions = revert },
        Spread = new() { SpreadPoolSize = 3 },
        Servers = [new() { Id = "site", Pool = new() { LoginCap = 6, LoginHeadroom = 1 } }]
    };

    private static AuditRow Regressed() => new()
    {
        Id = "prior-id", RunId = "prior-run", Applied = true,
        Category = AgentCategories.PoolSizing, Target = "/spread/spreadPoolSize", Before = 2, After = 3,
        ExpectedEffect = new() { Metric = "raceCompletionRate", Direction = "increase", WindowHours = 24 },
        Outcome = new("regressed", 0.9, 0.5, 20, 20, DateTime.UtcNow, "observational")
    };

    private ChangeApplier Applier(FreezeStore? freeze = null, params IChangeValidator[] validators)
        => new(validators.Length == 0 ? [new PoolSizingValidator()] : validators,
            freeze ?? new FreezeStore(_root), new AuditTrail(_root), transferLimit: _ => 1);

    [Fact]
    public void Existing_empty_config_keeps_passive_and_proposal_guards_on_and_revert_off()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("{}")!;
        Assert.True(config.Agent.AccountabilityEnabled);
        Assert.True(config.Agent.NoOpRejectionEnabled);
        Assert.True(config.Agent.CounterfactualReplayEnabled);
        Assert.False(config.Agent.AutoRevertRegressions);
    }

    [Fact]
    public void Revert_requires_explicit_enable()
    {
        var cfg = Config(false);
        Assert.Empty(AccountabilityReview.RevertRegressions(Applier(), cfg, [Regressed()], "next"));
        Assert.Equal(3, cfg.Spread.SpreadPoolSize);
    }

    [Fact]
    public void Enabled_revert_uses_validator_and_preserves_original_until_commit()
    {
        var cfg = Config();
        var original = Regressed();
        var row = Assert.Single(AccountabilityReview.RevertRegressions(Applier(), cfg, [original], "next"));
        Assert.True(row.WasApplied);
        Assert.Equal(original.Id, row.AutomaticRevertOf);
        Assert.Equal(2, cfg.Spread.SpreadPoolSize);
        Assert.False(original.Undone);
    }

    [Fact]
    public void Newer_user_value_wins_over_regressed_audit()
    {
        var cfg = Config();
        cfg.Spread.SpreadPoolSize = 4;
        Assert.Empty(AccountabilityReview.RevertRegressions(Applier(), cfg, [Regressed()], "next"));
        Assert.Equal(4, cfg.Spread.SpreadPoolSize);
    }

    [Fact]
    public void Frozen_regressed_value_is_never_reverted()
    {
        var cfg = Config();
        var frozen = new FreezeStore(_root);
        frozen.Freeze("/spread");
        var row = Assert.Single(AccountabilityReview.RevertRegressions(Applier(frozen), cfg, [Regressed()], "next"));
        Assert.Equal("frozen", row.RejectionReason);
        Assert.Equal(3, cfg.Spread.SpreadPoolSize);
    }

    [Fact]
    public void Freeze_added_after_scoring_prevents_commit_of_detached_candidate()
    {
        var candidate = Config();
        var applier = Applier();
        var rows = AccountabilityReview.RevertRegressions(applier, candidate, [Regressed()], "next");
        new FreezeStore(_root).Freeze("/spread/spreadPoolSize");
        var committed = false;
        Assert.Throws<InvalidOperationException>(() => applier.GuardCommit(Path.Combine(_root, "config.json"), rows,
            candidate, () => committed = true));
        Assert.False(committed);
    }

    [Fact]
    public void Unreadable_freezes_preserve_status_quo_at_commit()
    {
        var applier = Applier();
        var cfg = Config();
        var rows = AccountabilityReview.RevertRegressions(applier, cfg, [Regressed()], "next");
        File.WriteAllText(Path.Combine(_root, "frozen.json"), "{torn");
        Assert.Throws<InvalidOperationException>(() => applier.GuardCommit(Path.Combine(_root, "config.json"), rows,
            cfg, () => Assert.Fail("Frozen uncertainty must not apply a proposal")));
    }

    [Fact]
    public void Revert_does_not_bypass_validator_constraints()
    {
        var cfg = Config();
        var row = Regressed();
        row.Before = 16; // exceeds the validator's allowed step
        var rejected = Assert.Single(AccountabilityReview.RevertRegressions(Applier(), cfg, [row], "next"));
        Assert.Equal("change-too-large", rejected.RejectionReason);
        Assert.Equal(3, cfg.Spread.SpreadPoolSize);
    }

    [Fact]
    public void Automatic_reverts_do_not_become_new_regression_loops()
    {
        var cfg = Config();
        var row = Regressed();
        row.AutomaticRevertOf = "older";
        Assert.Empty(AccountabilityReview.RevertRegressions(Applier(), cfg, [row], "next"));
    }

    [Fact]
    public void Actual_gate_capacity_rejects_nominal_slot_churn_before_mutation()
    {
        var cfg = Config();
        var rows = new List<AuditRow>();
        Applier().Apply([new() { Category = AgentCategories.PoolSizing, Target = "/servers/site/spread/maxUploadSlots",
            Before = 3, After = 4, Confidence = 1 }], cfg, cfg.Agent, "run", false, rows.Add);
        Assert.Equal("capacity-no-effect", Assert.Single(rows).RejectionReason);
        Assert.Equal(3, cfg.Servers[0].SpreadSite.MaxUploadSlots);
    }

    [Fact]
    public void Preview_throwing_captured_mutator_cannot_change_live_configuration()
    {
        var cfg = Config();
        var rows = new List<AuditRow>();
        Applier(null, new ThrowingValidator()).Apply([new() { Category = "projection", Target = "/spread/spreadPoolSize",
            Before = 3, After = 2, Confidence = 1 }], cfg, cfg.Agent, "run", false, rows.Add);
        Assert.Equal("impact-preview-unavailable", Assert.Single(rows).RejectionReason);
        Assert.Equal(3, cfg.Spread.SpreadPoolSize);
    }

    [Fact]
    public void Missing_effect_is_passive_unscored_and_does_not_reject_valid_change()
    {
        var cfg = Config();
        var rows = new List<AuditRow>();
        Applier().Apply([new() { Category = AgentCategories.PoolSizing, Target = "/spread/maxConcurrentRaces",
            Before = 1, After = 2, Confidence = 1 }], cfg, cfg.Agent, "run", false, rows.Add);
        var row = Assert.Single(rows);
        Assert.True(row.WasApplied);
        Assert.Equal("unscored", row.Outcome?.Status);
        Assert.Contains("missing/invalid expected effect", AccountabilityReview.Report(rows, []));
    }

    [Fact]
    public void Outcome_rewrite_preserves_torn_and_untouched_rows()
    {
        var audit = new AuditTrail(_root);
        var row = Regressed();
        row.Outcome = null;
        audit.Append(row);
        var path = Path.Combine(_root, "ai-audit.jsonl");
        File.AppendAllText(path, "{torn-verbatim\n{\"Id\":\"other\",\"UnknownFutureField\":true}\n");
        audit.RecordOutcome(row.Id!, Regressed().Outcome!);
        var text = File.ReadAllText(path);
        Assert.Contains("{torn-verbatim\n", text);
        Assert.Contains("{\"Id\":\"other\",\"UnknownFutureField\":true}\n", text);
        Assert.Equal("regressed", audit.ReadAll().First(r => r.Id == row.Id).Outcome?.Status);
    }

    [Fact]
    public void Failed_outcome_write_keeps_assessment_in_memory()
    {
        var audit = new AuditTrail(_root);
        var row = Regressed();
        row.Outcome = null;
        audit.Append(row);
        using var held = File.Open(Path.Combine(_root, "ai-audit.jsonl"), FileMode.Open, FileAccess.Read, FileShare.Read);
        audit.RecordOutcome(row.Id!, Regressed().Outcome!);
        Assert.Equal("regressed", Assert.Single(audit.ReadAll()).Outcome?.Status);
    }

    [Fact]
    public void Report_distinguishes_pending_declarations_from_legacy_unscored_rows()
    {
        var declared = Regressed();
        declared.Outcome = null;
        var legacy = Regressed();
        legacy.ExpectedEffect = null;
        legacy.Outcome = null;
        var report = AccountabilityReview.Report([], [declared, legacy]);
        Assert.Contains("pending=1", report);
        Assert.Contains("unscored=1", report);
    }

    private sealed class ThrowingValidator : IChangeValidator
    {
        public string Category => "projection";
        public ValidationResult Validate(AgentChange change, AppConfig config) => new(true, null, _ =>
        {
            config.Spread.SpreadPoolSize = 2;
            throw new IOException("synthetic projection failure");
        });
    }

    public void Dispose() => Directory.Delete(_root, true);
}
