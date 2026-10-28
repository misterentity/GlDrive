using System.IO;
using System.Text.Json;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Services.Automation;
using GlDrive.Util;
using Xunit;

namespace GlDrive.Tests;

public sealed class AgentReviewRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-review-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    public AgentReviewRegressionTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private static ExpectedEffect Effect() => new() { Metric = "queueWaitSeconds", Direction = "decrease", WindowHours = 24 };
    private static AuditRow Row(int hours = 48) => new()
    {
        Id = Guid.NewGuid().ToString("N"), RunId = "scheduled-run", Applied = true,
        Ts = Now.AddHours(-hours).ToString("O"), Category = AgentCategories.PoolSizing,
        Target = "/spread/spreadPoolSize", Before = 2, After = 3, ExpectedEffect = Effect(),
        Outcome = new("effective", 100, 50, 10, 10, Now, "observed-change-not-causal-proof")
    };

    [Theory]
    [InlineData("\"undeclared\"")]
    [InlineData("[]")]
    [InlineData("{\"metric\":\"queueWaitSeconds\",\"direction\":\"decrease\",\"windowHours\":\"24\"}")]
    [InlineData("{\"metric\":\"queueWaitSeconds\",\"direction\":\"decrease\",\"windowHours\":24.0}")]
    [InlineData("{\"metric\":[],\"direction\":true,\"windowHours\":24}")]
    [InlineData("{\"metric\":\"queueWaitSeconds\",\"direction\":\"decrease\",\"windowHours\":999999999999999999999999}")]
    public void Malformed_optional_effect_preserves_changes_suggestions_and_memo(string effect)
    {
        var json = "{\"memo_update\":\"retained memo\",\"changes\":[{\"category\":\"poolSizing\",\"expected_effect\":" + effect
            + "}],\"suggestions\":[{\"category\":\"poolSizing\",\"expected_effect\":" + effect + "}]}";
        var result = JsonSerializer.Deserialize<AgentRunResult>(json)!;
        Assert.Equal("retained memo", result.MemoUpdate);
        Assert.Null(Assert.Single(result.Changes).ExpectedEffect);
        Assert.Null(Assert.Single(result.Suggestions).ExpectedEffect);
    }

    [Fact]
    public void Feedback_has_identity_and_only_recent_scored_agent_applies_newest_first()
    {
        var rows = Enumerable.Range(1, 60).Select(i => Row(i)).ToList();
        var preview = Row(); preview.DryRun = true;
        var suggestion = Row(); suggestion.Applied = false;
        var manual = Row(); manual.RunId = "manual-fixture";
        var mcp = Row(); mcp.RunId = "mcp-apply-fixture";
        var unscored = Row(); unscored.Outcome = unscored.Outcome! with { Status = "unscored" };
        var invalid = Row(); invalid.ExpectedEffect = null;
        var feedback = AccountabilityReview.Feedback([.. rows, preview, suggestion, manual, mcp, unscored, invalid, Row(24 * 31)], Now);
        Assert.Equal(50, feedback.Count);
        Assert.Equal(rows[0].Id, feedback[0].Id);
        Assert.Equal(rows[49].Id, feedback[^1].Id);
        Assert.Equal(rows[0].RunId, feedback[0].RunId);
        Assert.Equal(rows[0].Category, feedback[0].Category);
        Assert.Equal(rows[0].Target, feedback[0].Target);
        Assert.Equal("queueWaitSeconds", feedback[0].Metric);
        Assert.Equal("decrease", feedback[0].Direction);
        Assert.Equal(24, feedback[0].WindowHours);
    }

    [Fact]
    public void Accepted_previews_do_not_get_outcomes()
    {
        var config = new AppConfig();
        var rows = new List<AuditRow>();
        var applier = new ChangeApplier([new PoolSizingValidator()], new FreezeStore(_root), new AuditTrail(_root));
        applier.Apply([new() { Category = AgentCategories.PoolSizing, Target = "/spread/maxConcurrentRaces",
            Before = 1, After = 2, Confidence = 1 }], config, config.Agent, "run", true, rows.Add);
        Assert.True(Assert.Single(rows).Applied);
        Assert.Null(rows[0].Outcome);
    }

    [Theory]
    [InlineData("manual-fixture")]
    [InlineData("mcp-apply-fixture")]
    public void Operator_applies_are_never_auto_reverted(string runId)
    {
        var row = Row(); row.RunId = runId; row.Outcome = row.Outcome! with { Status = "regressed" };
        var config = new AppConfig { Agent = new() { AutoRevertRegressions = true }, Spread = new() { SpreadPoolSize = 3 } };
        var applier = new ChangeApplier([new PoolSizingValidator()], new FreezeStore(_root), new AuditTrail(_root));
        Assert.Empty(AccountabilityReview.RevertRegressions(applier, config, [row], "next"));
        Assert.Equal(3, config.Spread.SpreadPoolSize);
    }

    [Theory]
    [InlineData("settings-ui:agent-review")]
    [InlineData("mcp:agent_audit_action")]
    public void Endorsing_a_followup_suggestion_is_not_negative_feedback(string actor)
    {
        var row = Row();
        var edit = new ConfigOverrideEvent { Actor = actor, Ts = Now.ToString("O"), JsonPointer = row.Target,
            BeforeValue = "3", AfterValue = "4" };
        Assert.Empty(CorrectionDetector.Detect(new(), [row], [edit], Now));
    }

    private void WriteRace(DateTime time, bool broken = false)
    {
        var path = Path.Combine(_root, $"races-{time.ToLocalTime():yyyyMMdd}.jsonl");
        var row = new RaceOutcomeEvent { Ts = time.ToString("O"), QueueWaitSeconds = 10 };
        File.AppendAllText(path, broken ? "{broken\n" : JsonSerializer.Serialize(row) + "\n");
    }

    [Fact]
    public void Parse_failures_are_per_assessment_and_outcomes_are_batched_and_unchanged_results_not_rewritten()
    {
        var audit = new CountingAudit(_root);
        var bad = Row(48); bad.Outcome = null;
        var good = Row(24 * 12); good.Outcome = null;
        audit.Append(bad); audit.Append(good);
        foreach (var row in new[] { bad, good })
        {
            var applied = DateTime.Parse(row.Ts).ToUniversalTime();
            WriteRace(applied.AddMinutes(1), row == bad);
            WriteRace(applied.AddDays(-7).AddMinutes(1));
        }
        var config = new AppConfig { Agent = new() { OutcomeMinimumSamples = 1 } };
        var assessed = AccountabilityReview.Assess(audit, new LogDigester(_root), config, Now);
        Assert.Equal("incomplete-evidence", assessed[0].Outcome!.Reason);
        Assert.Equal("no-effect", assessed[1].Outcome!.Status);
        Assert.Equal(1, audit.Writes);
        Assert.Equal(2, audit.LastCount);
        AccountabilityReview.Assess(audit, new LogDigester(_root), config, Now.AddHours(1));
        Assert.Equal(1, audit.Writes);
    }

    [Fact]
    public void Aged_out_assessment_is_terminal()
    {
        var audit = new CountingAudit(_root);
        var row = Row(24 * 120); row.Outcome = null; audit.Append(row);
        AccountabilityReview.Assess(audit, new LogDigester(_root), new(), Now);
        Assert.Equal("window-aged-out", Assert.Single(audit.ReadAll()).Outcome!.Reason);
        AccountabilityReview.Assess(audit, new LogDigester(_root), new(), Now.AddDays(1));
        Assert.Equal(1, audit.Writes);
    }

    private sealed class CountingAudit(string root) : AuditTrail(root)
    {
        public int Writes { get; private set; }
        public int LastCount { get; private set; }
        public override void RecordOutcomes(IReadOnlyDictionary<string, AgentOutcome> outcomes)
        {
            if (outcomes.Count > 0) { Writes++; LastCount = outcomes.Count; }
            base.RecordOutcomes(outcomes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_apply_records_advisory_replay_even_when_evidence_is_missing_or_times_out(bool timeout)
    {
        var config = new AppConfig { Servers = [new() { Id = "site" }] };
        var applier = new ChangeApplier([new SkiplistValidator()], new FreezeStore(_root), new AuditTrail(_root),
            cancellableReplayEvidence: (_, token) => timeout ? throw new OperationCanceledException(token) : new([], [], false));
        var rows = new List<AuditRow>();
        var report = applier.Apply([new() { Category = AgentCategories.Skiplist, Target = "/servers/site/spread/skiplistRules/-",
            After = new SkiplistRule { Pattern = "*sample*", Action = SkiplistAction.Deny }, Confidence = 1 }],
            config, config.Agent, "manual-reviewed", false, rows.Add, explicitOperator: true);
        Assert.Equal(1, report.Applied);
        var row = Assert.Single(rows);
        Assert.True(row.WasApplied);
        Assert.False(row.IsAgentRun);
        Assert.Null(row.Outcome);
        Assert.Equal(timeout ? "impact-time-budget-exceeded" : "impact-evidence-unavailable", row.Impact!.RejectionReason);
    }

    [Fact]
    public async Task Mcp_explicit_apply_replays_in_background_without_holding_config_path_lock()
    {
        var config = new AppConfig { Servers = [new() { Id = "site" }] };
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, json));
        var aiRoot = Path.Combine(_root, "ai-data");
        var audit = new AuditTrail(aiRoot);
        var freeze = new FreezeStore(aiRoot);
        const string target = "/servers/site/spread/skiplistRules/-";
        audit.Append(new() { RunId = "original", Category = AgentCategories.Skiplist, Target = target,
            After = new SkiplistRule { Pattern = "*sample*", Action = SkiplistAction.Deny } });
        var replayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseReplay = new ManualResetEventSlim();
        var applier = new ChangeApplier([new SkiplistValidator()], freeze, audit, cancellableReplayEvidence: (_, ct) =>
        {
            replayEntered.SetResult();
            releaseReplay.Wait(ct);
            return new([], [], false);
        });
        var service = new AgentAutomation(config, () => null, () => freeze, () => audit, () => applier, _root,
            () => { }, path, candidate => File.WriteAllText(path, JsonSerializer.Serialize(candidate, json)));
        var apply = service.ExecuteAsync("agent_audit_action",
            JsonSerializer.SerializeToElement(new { action = "apply", runId = "original", target }), default);
        try
        {
            await replayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(apply.IsCompleted);
            // Another thread must acquire the path lock while the replay is still suspended.
            await Task.Run(() => SecureFile.WithPathLock(path, () => { })).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { releaseReplay.Set(); }
        var result = JsonSerializer.SerializeToElement(await apply);
        Assert.True(result.GetProperty("applied").GetBoolean());
        var applied = Assert.Single(audit.ReadAll(), r => r.WasApplied);
        Assert.Equal("impact-evidence-unavailable", applied.Impact!.RejectionReason);
        Assert.Equal("operator", applied.Origin);
    }

    [Fact]
    public void Telemetry_and_irc_readers_allow_concurrent_appends()
    {
        WriteRace(Now);
        var telemetryPath = Path.Combine(_root, $"races-{Now.ToLocalTime():yyyyMMdd}.jsonl");
        using var events = new LogDigester(_root).ReadStream<RaceOutcomeEvent>("races", Now.ToLocalTime(), Now.ToLocalTime()).GetEnumerator();
        Assert.True(events.MoveNext());
        File.AppendAllText(telemetryPath, "{}\n");
        var path = Path.Combine(_root, "irc.log");
        File.WriteAllText(path, "first\n");
        using var lines = ImpactSimulator.ReadSharedLines(path).GetEnumerator();
        Assert.True(lines.MoveNext());
        File.AppendAllText(path, "second\n");
    }

    [Fact]
    public async Task Sharing_violations_are_retried_but_other_write_errors_are_not()
    {
        var calls = 0;
        LogFileRetry.Append(() => { if (++calls < 3) throw new IOException("busy", unchecked((int)0x80070020)); });
        Assert.Equal(3, calls);
        calls = 0;
        await LogFileRetry.AppendAsync(() => { if (++calls < 3) throw new IOException("busy", unchecked((int)0x80070020)); return Task.CompletedTask; }, default);
        Assert.Equal(3, calls);
        calls = 0;
        Assert.Throws<IOException>(() => LogFileRetry.Append(() => { calls++; throw new IOException("full", unchecked((int)0x80070070)); }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Replay_cancellation_reaches_file_reads_and_individual_matchers()
    {
        WriteRace(Now);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new GlDrive.Spread.AnnounceMatcher([], false)
            .Match("#announces", "TV Synthetic.Show.S01E01.1080p.WEB-GRP", canceled.Token));
        Assert.Throws<OperationCanceledException>(() => ImpactSimulator.Read(_root, new(), Now, canceled.Token));
    }
}
