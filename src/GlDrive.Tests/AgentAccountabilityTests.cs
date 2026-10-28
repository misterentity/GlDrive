using System.Text.Json;
using GlDrive.AiAgent;
using GlDrive.Config;
using Xunit;

namespace GlDrive.Tests;

public sealed class AgentAccountabilityTests
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private static RaceOutcomeEvent Race(bool complete = true, long delivered = 100, double? queue = 100) => new()
    {
        Ts = Now.ToString("O"), Result = complete ? "complete" : "aborted", QueueWaitSeconds = queue,
        Participants = [new("source", "src", 100, 1, 100, null), new("site", "dst", delivered, delivered > 0 ? 1 : 0, 100, null)]
    };
    private static RaceOutcomeEvent[] Samples(double queue) => Enumerable.Range(0, 10).Select(_ => Race(queue: queue)).ToArray();
    private static ExpectedEffect Effect(string metric = "queueWaitSeconds", string direction = "decrease", int hours = 24) =>
        new() { Metric = metric, Direction = direction, WindowHours = hours };

    [Fact]
    public void Undeclared_effect_is_unscored_without_a_regression_claim()
    {
        var score = OutcomeEvaluator.Score(null, Samples(100), Samples(500), true, Now);
        Assert.Equal("unscored", score.Status);
        Assert.Null(score.Before);
        Assert.Null(score.After);
        Assert.Equal(Now, score.EvaluatedAtUtc);
    }

    [Theory]
    [InlineData("unknown", "increase", 24)]
    [InlineData("queueWaitSeconds", "lower", 24)]
    [InlineData("queueWaitSeconds", "increase", 0)]
    [InlineData("queueWaitSeconds", "increase", 169)]
    public void Unsupported_declarations_are_passively_unscored(string metric, string direction, int hours) =>
        Assert.Equal("unscored", OutcomeEvaluator.Score(Effect(metric, direction, hours), Samples(100), Samples(500), true, Now).Status);

    [Fact]
    public void Incomplete_evidence_cannot_score_a_regression()
    {
        var score = OutcomeEvaluator.Score(Effect(), Samples(100), Samples(500), false, Now);
        Assert.Equal("unscored", score.Status);
        Assert.Equal("incomplete-evidence", score.Reason);
    }

    [Fact]
    public void Both_windows_need_enough_observations()
    {
        Assert.Equal("unscored", OutcomeEvaluator.Score(Effect(), [], Samples(500), true, Now).Status);
        Assert.Equal("unscored", OutcomeEvaluator.Score(Effect(), Samples(100), Samples(500).Take(9).ToArray(), true, Now).Status);
        Assert.Equal("unscored", OutcomeEvaluator.Score(Effect(), Samples(100).Take(9).ToArray(), Samples(500), true, Now).Status);
    }

    [Theory]
    [InlineData("decrease", 50, "effective")]
    [InlineData("decrease", 150, "regressed")]
    [InlineData("increase", 50, "regressed")]
    [InlineData("increase", 150, "effective")]
    [InlineData("increase", 100, "no-effect")]
    [InlineData("increase", 110, "no-effect")]
    public void Scores_honor_direction_and_tolerance(string direction, double after, string status)
    {
        var score = OutcomeEvaluator.Score(Effect(direction: direction), Samples(100), Samples(after), true, Now);
        Assert.Equal(status, score.Status);
        Assert.Equal(100, score.Before);
        Assert.Equal(after, score.After);
        Assert.Equal(10, score.BaselineSamples);
        Assert.Equal(10, score.ObservedSamples);
        Assert.Equal("observed-change-not-causal-proof", score.Reason);
    }

    [Fact]
    public void Tolerance_is_configurable_and_zero_baseline_is_well_defined()
    {
        Assert.Equal("regressed", OutcomeEvaluator.Score(Effect(), Samples(100), Samples(105), true, Now, tolerancePercent: 0).Status);
        Assert.Equal("no-effect", OutcomeEvaluator.Score(Effect(), Samples(0), Samples(0), true, Now).Status);
        Assert.Equal("regressed", OutcomeEvaluator.Score(Effect(), Samples(0), Samples(1), true, Now).Status);
    }

    [Fact]
    public void Completion_rates_exclude_existing_files_and_source_only_activity()
    {
        var before = Enumerable.Range(0, 10).Select(i => Race(complete: i < 5)).ToArray();
        var after = Enumerable.Range(0, 10).Select(i => Race(complete: i < 8)).Concat([Race(delivered: 0)]).ToArray();
        var score = OutcomeEvaluator.Score(Effect("raceCompletionRate", "increase"), before, after, true, Now);
        Assert.Equal("effective", score.Status);
        Assert.Equal(.5, score.Before);
        Assert.Equal(.8, score.After);
        Assert.Equal(10, score.ObservedSamples);
        Assert.Equal("unscored", OutcomeEvaluator.Score(Effect("raceCompletionRate"),
            Enumerable.Repeat(Race(delivered: 0), 100).ToArray(), after, true, Now).Status);
    }

    [Fact]
    public void Delivery_rate_uses_declared_window_and_actual_destination_bytes()
    {
        var before = Enumerable.Range(0, 10).Select(i => Race(delivered: i < 5 ? 100 : 0)).ToArray();
        var score = OutcomeEvaluator.Score(Effect("deliveredRacesPerHour", "increase", 2), before, Samples(100), true, Now);
        Assert.Equal(2.5, score.Before);
        Assert.Equal(5, score.After);
        Assert.Equal("effective", score.Status);
    }

    [Fact]
    public void Unknown_negative_and_nonfinite_queue_values_are_not_measurements()
    {
        RaceOutcomeEvent[] invalid = [Race(queue: null), Race(queue: -1), Race(queue: double.NaN), Race(queue: double.PositiveInfinity)];
        var score = OutcomeEvaluator.Score(Effect(), Samples(100).Concat(invalid).ToArray(), Samples(50).Concat(invalid).ToArray(), true, Now);
        Assert.Equal("effective", score.Status);
        Assert.Equal(10, score.BaselineSamples);
        Assert.Equal(10, score.ObservedSamples);
        Assert.Equal("unscored", OutcomeEvaluator.Score(Effect(), invalid, Samples(50), true, Now).Status);
    }

    [Fact]
    public void Nonfinite_aggregates_never_become_automatic_regression_evidence()
    {
        var score = OutcomeEvaluator.Score(Effect(), Samples(double.MaxValue), Samples(double.MaxValue), true, Now);
        Assert.NotEqual("regressed", score.Status);
        Assert.True(score.Status == "unscored" || score.Before.HasValue && double.IsFinite(score.Before.Value) && score.After.HasValue && double.IsFinite(score.After.Value));
    }

    private static AppConfig Config() => new() { Servers = [new() { Id = "site" }] };
    private static AuditRow Applied() => new()
    {
        Id = "audit-fixture", RunId = "run-fixture", Applied = true,
        Ts = Now.AddHours(-2).ToString("O"), Target = "/servers/site/spread/sitePriority", After = 2, Before = 1
    };
    private static ConfigOverrideEvent Edit(string actor = "settings-ui", string before = "2", string after = "1") => new()
    {
        Actor = actor, Ts = Now.AddHours(-1).ToString("O"), JsonPointer = "/servers/0/spreadSite/priority",
        CanonicalPointer = "/servers/site/spread/sitePriority",
        BeforeValue = before, AfterValue = after, AiAuditRef = "run-fixture"
    };

    [Theory]
    [InlineData("settings-ui")]
    [InlineData("server-editor")]
    [InlineData("tray")]
    [InlineData("mcp:config_patch")]
    [InlineData("restore:history")]
    [InlineData("restore:settings-ui")]
    [InlineData("restore:snapshot")]
    public void Human_edits_of_applied_values_create_read_only_negative_signals(string actor)
    {
        var config = Config();
        var original = JsonSerializer.Serialize(config);
        var correction = Assert.Single(CorrectionDetector.Detect(config, [Applied()], [Edit(actor)]));
        Assert.Equal(Applied().Target, correction.Target);
        Assert.Equal("audit-fixture", correction.AuditId);
        Assert.Equal(original, JsonSerializer.Serialize(config));
    }

    [Theory]
    [InlineData("agent:run-fixture")]
    [InlineData("lease:fixture")]
    [InlineData("lease-revert:fixture")]
    [InlineData("system:plex-connection-refresh")]
    [InlineData("application")]
    [InlineData("unknown")]
    public void Nonhuman_saves_never_become_negative_feedback(string actor) =>
        Assert.Empty(CorrectionDetector.Detect(Config(), [Applied()], [Edit(actor)]));

    [Fact]
    public void Same_value_saves_and_edits_before_the_agent_change_are_not_corrections()
    {
        Assert.Empty(CorrectionDetector.Detect(Config(), [Applied()], [Edit(before: "2", after: "2")]));
        Assert.Empty(CorrectionDetector.Detect(Config(), [Applied()], [Edit() with { Ts = Now.AddHours(-3).ToString("O") }]));
        Assert.Empty(CorrectionDetector.Detect(Config(), [Applied()], [Edit() with { Ts = "torn timestamp" }]));
        Assert.Empty(CorrectionDetector.Detect(Config(), [Applied()], [Edit() with { Ts = Now.AddHours(1).ToString("O") }], Now));
        Assert.Empty(CorrectionDetector.Detect(Config(), [Applied()], [Edit(before: "9")]));
        var preview = Applied();
        preview.DryRun = true;
        Assert.Empty(CorrectionDetector.Detect(Config(), [preview], [Edit()]));
    }

    [Fact]
    public void Canonical_identity_survives_reordering_but_old_numeric_events_are_ambiguous()
    {
        var config = Config();
        config.Servers.Insert(0, new() { Id = "other" });
        Assert.Equal(Applied().Target, Assert.Single(CorrectionDetector.Detect(config, [Applied()], [Edit()])).Target);
        Assert.Empty(CorrectionDetector.Detect(config, [Applied()], [Edit() with { CanonicalPointer = null }]));
    }

    [Fact]
    public void Scalar_corrections_can_reverse_an_aliased_field_of_a_whole_object_change()
    {
        var row = Applied();
        row.Target = "/servers/site/spread";
        row.After = JsonSerializer.SerializeToElement(new { priority = 2, maxDownloadSlots = 3 });
        Assert.Equal("manual-edit-after-agent", Assert.Single(CorrectionDetector.Detect(Config(), [row], [Edit()])).Reason);
    }

    [Fact]
    public void Explicit_dismissal_is_feedback_but_automatic_undo_is_not()
    {
        var row = Applied();
        row.Applied = false;
        row.Undone = true;
        row.UndoneReason = "user-dismiss";
        Assert.Equal("manual-dismissal", Assert.Single(CorrectionDetector.Detect(Config(), [row], [])).Reason);
        row.UndoneReason = "automatic-regression";
        Assert.Empty(CorrectionDetector.Detect(Config(), [row], []));
    }
}
