using System.IO;
using System.Text.Json;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class WinnerCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-winner-coverage-" + Guid.NewGuid().ToString("N"));
    public WinnerCoverageTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static AppConfig Config() => new() { Servers = [new() { Id = "target" }, new() { Id = "peer" }] };
    private static AgentChange Change(string category) => new()
    {
        Category = category, Target = "/servers/target/spread/" + (category == AgentCategories.Priority ? "sitePriority" : "downloadOnly"),
        Before = category == AgentCategories.Priority ? "Normal" : false,
        After = category == AgentCategories.Priority ? "Low" : true, Confidence = 1,
        Reasoning = "Claimed 100% winner coverage; tuning from throughput only", EvidenceRef = "model-supplied-coverage=1"
    };
    private static RaceOutcomeEvent Race(string? winner, params string[] participants) => new()
    {
        Winner = winner, Section = "video", Result = "complete",
        Participants = participants.Select(id => new RaceParticipant(id, "dst", 1000, 10, 100, null)).ToList()
    };
    private static RacesDigest Evidence(int known, int total) => new RacesDigester().Build(
        Enumerable.Range(0, total).Select(i => Race(i < known ? "peer" : null, "target", "peer")));
    private ChangeApplier Applier(Func<AppConfig, RacesDigest?>? provider = null) =>
        new([new PriorityValidator(), new DownloadOnlyValidator()], new FreezeStore(_root), new AuditTrail(_root), provider);

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 100, false)]
    [InlineData(49, 100, false)]
    [InlineData(50, 100, true)]
    [InlineData(1, 2, true)]
    [InlineData(1, 3, false)]
    [InlineData(100, 100, true)]
    [InlineData(101, 100, false)]
    [InlineData(-1, 100, false)]
    public void Coverage_boundary_is_measured_not_rounded(int known, int total, bool expected) =>
        Assert.Equal(expected, ChangeValidationContext.HasSufficientCoverage(known, total));

    [Theory]
    [InlineData(AgentCategories.Priority, 49, false)]
    [InlineData(AgentCategories.Priority, 50, true)]
    [InlineData(AgentCategories.DownloadOnly, 49, false)]
    [InlineData(AgentCategories.DownloadOnly, 50, true)]
    public void Both_validators_require_target_server_coverage_and_accept_real_losses(string category, int known, bool allowed)
    {
        IChangeValidator validator = category == AgentCategories.Priority ? new PriorityValidator() : new DownloadOnlyValidator();
        var cfg = Config();
        var evidence = Evidence(known, 100);
        Assert.Equal(0, evidence.WinRateByServer["target"]); // Real observed losses remain a valid measured rate.
        var result = validator.Validate(Change(category), cfg, new ChangeValidationContext(evidence));
        Assert.Equal(allowed, result.Ok);
        if (!allowed) Assert.Equal("insufficient-winner-coverage", result.RejectionReason);
    }

    [Fact]
    public void Global_coverage_cannot_substitute_for_target_server_coverage()
    {
        var races = Enumerable.Range(0, 100).Select(_ => Race("peer", "peer", "other"))
            .Append(Race(null, "target", "peer"));
        var digest = new RacesDigester().Build(races);
        Assert.True(digest.WinnerCoverage > 0.9);
        Assert.Equal(0, digest.WinnerCoverageByServer["target"]);
        Assert.False(new ChangeValidationContext(digest).AllowsPriorityOrRole("target"));
        Assert.False(new ChangeValidationContext(digest).AllowsPriorityOrRole("absent"));
    }

    [Fact]
    public void Duplicate_participant_rows_do_not_inflate_the_race_denominator()
    {
        var digest = new RacesDigester().Build([Race("peer", "target", "target", "peer"), Race(null, "target", "peer")]);
        Assert.Equal(2, digest.ParticipationByServer["target"]);
        Assert.Equal(1, digest.KnownWinnerSamplesByServer["target"]);
        Assert.Equal(0.5, digest.WinnerCoverageByServer["target"]);
    }

    [Theory]
    [InlineData(AgentCategories.Priority)]
    [InlineData(AgentCategories.DownloadOnly)]
    public void Direct_validator_without_trusted_context_fails_closed(string category)
    {
        IChangeValidator validator = category == AgentCategories.Priority ? new PriorityValidator() : new DownloadOnlyValidator();
        Assert.Equal("insufficient-winner-coverage", validator.Validate(Change(category), Config()).RejectionReason);
    }

    [Theory]
    [InlineData(AgentCategories.Priority, false)]
    [InlineData(AgentCategories.Priority, true)]
    [InlineData(AgentCategories.DownloadOnly, false)]
    [InlineData(AgentCategories.DownloadOnly, true)]
    public void Manual_apply_and_preview_cannot_use_model_claims_to_bypass_missing_evidence(string category, bool preview)
    {
        var cfg = Config(); var rows = new List<AuditRow>();
        var report = Applier().Apply([Change(category)], cfg, cfg.Agent, "manual", preview, rows.Add);
        Assert.Equal(0, report.Applied);
        Assert.Equal("insufficient-winner-coverage", Assert.Single(rows).RejectionReason);
        Assert.Equal(SitePriority.Normal, cfg.Servers[0].SpreadSite.Priority);
        Assert.False(cfg.Servers[0].SpreadSite.DownloadOnly);
    }

    [Theory]
    [InlineData(AgentCategories.Priority)]
    [InlineData(AgentCategories.DownloadOnly)]
    public void Manual_apply_uses_fresh_local_provider_and_rechecks_each_batch(string category)
    {
        var known = 1; var calls = 0;
        var applier = Applier(_ => { calls++; return Evidence(known, 2); });
        var first = Config();
        Assert.Equal(1, applier.Apply([Change(category)], first, first.Agent, "manual-one", false).Applied);
        known = 0;
        var second = Config();
        Assert.Equal(1, applier.Apply([Change(category)], second, second.Agent, "manual-two", false).Rejected);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Scheduled_priority_uses_its_built_digest_and_scheduled_role_changes_still_require_manual_review()
    {
        var cfg = Config(); var rows = new List<AuditRow>();
        var applier = Applier(_ => throw new Exception("Scheduled runs must use their own digest"));
        var report = applier.Apply([Change(AgentCategories.Priority), Change(AgentCategories.DownloadOnly)], cfg, cfg.Agent,
            "scheduled", false, rows.Add, configOnly: true, winnerEvidence: Evidence(1, 2));
        Assert.Equal(1, report.Applied);
        Assert.Equal(SitePriority.Low, cfg.Servers[0].SpreadSite.Priority);
        Assert.Equal("requires-manual-action", rows[1].RejectionReason);
        Assert.False(cfg.Servers[0].SpreadSite.DownloadOnly);
    }

    [Fact]
    public void Scheduled_suggestion_cannot_hide_low_coverage_behind_preview()
    {
        var cfg = Config(); var rows = new List<AuditRow>();
        Applier().Apply([Change(AgentCategories.DownloadOnly)], cfg, cfg.Agent, "suggestions", true, rows.Add,
            configOnly: true, winnerEvidence: Evidence(1, 3));
        Assert.Equal("insufficient-winner-coverage", Assert.Single(rows).RejectionReason);
    }

    [Theory]
    [InlineData(AgentCategories.Priority)]
    [InlineData(AgentCategories.DownloadOnly)]
    public void Evidence_read_failure_rejects_without_mutation(string category)
    {
        var cfg = Config(); var rows = new List<AuditRow>();
        var report = Applier(_ => throw new IOException("unreadable telemetry")).Apply(
            [Change(category)], cfg, cfg.Agent, "manual", false, rows.Add);
        Assert.Equal(1, report.Rejected);
        Assert.Equal("insufficient-winner-coverage", Assert.Single(rows).RejectionReason);
    }

    [Theory]
    [InlineData(AgentCategories.Priority)]
    [InlineData(AgentCategories.DownloadOnly)]
    public void Explicit_undo_does_not_need_winner_evidence_but_keeps_other_invariants(string category)
    {
        var cfg = Config(); var change = Change(category);
        var applier = Applier(_ => throw new Exception("Undo must not read telemetry"));
        Assert.Equal(1, applier.Apply([change], cfg, cfg.Agent, "explicit-undo", false, explicitUndo: true).Applied);
        var freeze = new FreezeStore(_root); freeze.Freeze(change.Target);
        var frozenApplier = new ChangeApplier([new PriorityValidator(), new DownloadOnlyValidator()], freeze, new AuditTrail(_root));
        cfg = Config();
        Assert.Equal(1, frozenApplier.Apply([change], cfg, cfg.Agent, "frozen-undo", false, explicitUndo: true)
            .RejectionByReason["frozen"]);
    }

    [Fact]
    public void Explicit_undo_can_restore_preexisting_manual_VeryHigh_priority()
    {
        var cfg = Config(); cfg.Servers[0].SpreadSite.Priority = SitePriority.High;
        var inverse = Change(AgentCategories.Priority); inverse.Before = "High"; inverse.After = "VeryHigh";
        var validator = new PriorityValidator();
        Assert.Equal("veryhigh-is-manual-only", validator.Validate(inverse, cfg,
            new ChangeValidationContext(Evidence(1, 1))).RejectionReason);
        var result = validator.Validate(inverse, cfg, new ChangeValidationContext(explicitUndo: true));
        Assert.True(result.Ok); result.Mutate!(cfg);
        Assert.Equal(SitePriority.VeryHigh, cfg.Servers[0].SpreadSite.Priority);
    }

    [Fact]
    public void MCP_review_preserves_original_evidence_and_uses_same_coverage_gate()
    {
        var row = new AuditRow { Category = AgentCategories.Priority, Target = Change(AgentCategories.Priority).Target,
            Before = "Normal", After = "Low", Reasoning = "No measured wins", EvidenceRef = "races-test.jsonl", RunId = "original" };
        var change = AgentAutomation.ReviewedChange(row, undo: false);
        Assert.StartsWith(row.Reasoning, change.Reasoning);
        Assert.Equal(row.EvidenceRef, change.EvidenceRef);
        var cfg = Config();
        Assert.Equal(1, Applier().Apply([change], cfg, cfg.Agent, "mcp-apply", false).Rejected);
        var undo = AgentAutomation.ReviewedChange(row, undo: true);
        Assert.Equal(row.After, undo.Before); Assert.Equal(row.Before, undo.After);
        Assert.Equal(row.RunId, undo.EvidenceRef);
    }

    [Fact]
    public void Local_evidence_reader_and_full_digest_reject_incomplete_telemetry()
    {
        var now = new DateTime(2026, 1, 10, 12, 0, 0);
        var file = Path.Combine(_root, "races-20260110.jsonl");
        File.WriteAllLines(file, [JsonSerializer.Serialize(Race("peer", "target", "peer")), "invalid json"]);
        var reader = new LogDigester(_root);
        foreach (var evidence in new[] { reader.BuildRaceEvidence(1, now), reader.Build(1, now).Races })
        {
            Assert.Equal(1, evidence.WinnerCoverage); // Readable rows alone would overstate coverage.
            Assert.False(evidence.WinnerEvidenceComplete);
            Assert.False(new ChangeValidationContext(evidence).AllowsPriorityOrRole("target"));
        }
        File.WriteAllLines(file, [JsonSerializer.Serialize(Race("peer", "target", "peer")), JsonSerializer.Serialize(Race(null, "target", "peer"))]);
        Assert.True(new ChangeValidationContext(reader.BuildRaceEvidence(1, now)).AllowsPriorityOrRole("target"));
    }

    [Fact]
    public void Digest_exposes_global_server_and_section_coverage_and_context_is_a_snapshot()
    {
        var races = Evidence(1, 2);
        var context = new ChangeValidationContext(races);
        races.KnownWinnerSamplesByServer["target"] = 0;
        Assert.True(context.AllowsPriorityOrRole("target"));
        Assert.False(new ChangeValidationContext(races).AllowsPriorityOrRole("target"));
        var section = new SectionActivityDigester().Build([
            new SectionActivityEvent { ServerId = "target", Section = "video", OurRaces = 3, KnownWinnerRaces = 1, OurWins = 0 }
        ]).PerServerSection.Single();
        Assert.Equal(1.0 / 3, section.WinnerCoverage);
        Assert.Equal(0, section.OurWinRate);
        var json = JsonSerializer.SerializeToElement(new DigestBundle { Races = races });
        Assert.Equal(0.5, json.GetProperty("races").GetProperty("winnerCoverage").GetDouble());
        Assert.Equal(0, json.GetProperty("races").GetProperty("winnerCoverageByServer").GetProperty("target").GetDouble());
        Assert.Contains("at least 0.5", AgentPrompt.SystemPrompt);
    }
}

internal static class MeasuredWinnerEvidence
{
    internal static RacesDigest ForConfig(AppConfig config) => new RacesDigester().Build(config.Servers.Select(s => new RaceOutcomeEvent
    {
        Winner = s.Id, Participants = [new(s.Id, "dst", 100, 1, 100, null)]
    }));
}
