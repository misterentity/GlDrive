using System.IO;
using System.Text.Json;
using GlDrive.AiAgent;
using GlDrive.Config;
using Xunit;

namespace GlDrive.Tests;

public sealed class AgentDownloadOnlyEvidenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gldrive-ai-evidence-" + Guid.NewGuid().ToString("N"));

    public AgentDownloadOnlyEvidenceTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static RaceOutcomeEvent Race(string? winner) => new()
    {
        Section = "tv", Winner = winner, Result = "complete",
        Participants = [new("source", "src", 0, 24, 0, null), new("destination", "dst", 500, 24, 10, null)]
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("not-a-participant")]
    public void Unobserved_winners_do_not_report_losses(string? winner)
    {
        var digest = new RacesDigester().Build([Race(winner)]);
        Assert.Empty(digest.WinRateByServer);
        Assert.Equal(1, digest.TotalRaces);
        Assert.Equal(1, digest.CompletionRateBySection["tv"]);
        Assert.Equal(0, digest.KnownWinnerSamplesByServer["destination"]);
        Assert.Equal(1, digest.DestinationRacesWithFilesByServer["destination"]);
        Assert.Equal(24, digest.DestinationFilesObservedByServer["destination"]);
        Assert.DoesNotContain("source", digest.DestinationFilesObservedByServer.Keys);
    }

    [Fact]
    public void Unknown_races_do_not_dilute_measured_win_rate()
    {
        var digest = new RacesDigester().Build([Race(null), Race("destination")]);
        Assert.Equal(1, digest.WinRateByServer["destination"]);
        Assert.Equal(0, digest.WinRateByServer["source"]);
        Assert.Equal(1, digest.KnownWinnerSamplesByServer["destination"]);
    }

    [Fact]
    public void Historical_section_activity_without_winner_observations_has_no_rate()
    {
        var row = Assert.Single(new SectionActivityDigester().Build([
            new SectionActivityEvent { ServerId = "destination", Section = "tv", OurRaces = 1155, OurWins = 0, FilesIn = 2400 }
        ]).PerServerSection);
        Assert.Null(row.OurWinRate);
        Assert.Equal(1155, row.OurRaces);
        Assert.Equal(2400, row.FilesIn);
        Assert.Equal(0, row.KnownWinnerRaces);
        Assert.Contains("\"ourWinRate\":null", JsonSerializer.Serialize(row));
    }

    [Fact]
    public void Mixed_section_history_uses_only_measured_winners_and_preserves_activity()
    {
        var row = Assert.Single(new SectionActivityDigester().Build([
            new SectionActivityEvent { ServerId = "destination", Section = "tv", OurRaces = 100, OurWins = 99, FilesIn = 2400 },
            new SectionActivityEvent { ServerId = "destination", Section = "tv", OurRaces = 10, OurWins = 3, KnownWinnerRaces = 5, FilesIn = 240 },
            new SectionActivityEvent { ServerId = "destination", Section = "tv", OurRaces = 20, OurWins = 0, KnownWinnerRaces = 0, FilesIn = 480 }
        ]).PerServerSection);
        Assert.Equal(0.6, row.OurWinRate);
        Assert.Equal(5, row.KnownWinnerRaces);
        Assert.Equal(130, row.OurRaces);
        Assert.Equal(3120, row.FilesIn);
    }

    [Fact]
    public void Real_rollup_persists_winner_denominator_before_digesting()
    {
        var day = new DateTime(2026, 10, 4);
        File.WriteAllLines(Path.Combine(_directory, "races-20261004.jsonl"),
            new[] { Race(null), Race("destination"), Race("unknown-site") }.Select(r => JsonSerializer.Serialize(r)));
        using var recorder = new TelemetryRecorder(_directory, 10);
        using var rollup = new SectionActivityRollup(recorder, _directory);
        rollup.RollUp(day);
        recorder.Dispose();
        var persisted = Directory.GetFiles(Path.Combine(_directory, "ai-data"), "section-activity-*.jsonl")
            .SelectMany(File.ReadLines).Select(line => JsonSerializer.Deserialize<SectionActivityEvent>(line)!).ToList();
        Assert.Equal(2, persisted.Count);
        Assert.All(persisted, r => Assert.Equal(1, r.KnownWinnerRaces));
        var destination = new SectionActivityDigester().Build(persisted).PerServerSection.Single(r => r.ServerId == "destination");
        Assert.Equal(3, destination.OurRaces);
        Assert.Equal(72, destination.FilesIn);
        Assert.Equal(1, destination.OurWinRate);
    }

    [Theory]
    [InlineData(false, "/servers/destination/spread/downloadOnly")]
    [InlineData(true, "/servers/destination/spread/downloadOnly")]
    [InlineData(false, "/servers/destination/spreadSite/downloadOnly")]
    [InlineData(true, "/servers/0/spreadSite/downloadOnly")]
    public void Automatic_role_changes_require_existing_manual_review(bool before, string target)
    {
        var (config, applier, proposal, rows) = Fixture(before, target);
        // The scheduled runner's actual Apply mode. Model confidence and alleged
        // evidence cannot turn an unverified routing proposal into a mutation.
        var report = applier.Apply([proposal], config, config.Agent, "scheduled-fixture", false, rows.Add, configOnly: true);
        Assert.Equal(0, report.Applied);
        Assert.Equal(before, config.Servers[0].SpreadSite.DownloadOnly);
        Assert.Equal("requires-manual-action", Assert.Single(rows).RejectionReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Suggestion_preview_and_explicit_manual_apply_remain_available(bool dryRun)
    {
        var (config, applier, proposal, rows) = Fixture(false, "/servers/destination/spread/downloadOnly");
        applier.Apply([proposal], config, config.Agent, "review-fixture", dryRun, rows.Add, configOnly: dryRun);
        Assert.True(Assert.Single(rows).Applied);
        Assert.Equal(!dryRun, config.Servers[0].SpreadSite.DownloadOnly);
    }

    [Fact]
    public void Alternate_category_casing_cannot_bypass_automatic_guard()
    {
        var (config, applier, proposal, rows) = Fixture(false, "/servers/destination/spread/downloadOnly");
        proposal.Category = "DownloadOnly";
        applier.Apply([proposal], config, config.Agent, "scheduled-fixture", false, rows.Add, configOnly: true);
        Assert.False(config.Servers[0].SpreadSite.DownloadOnly);
        Assert.Equal("unknown-category", Assert.Single(rows).RejectionReason);
    }

    private (AppConfig, ChangeApplier, AgentChange, List<AuditRow>) Fixture(bool before, string target)
    {
        var config = new AppConfig();
        config.Servers.Add(new ServerConfig { Id = "destination", SpreadSite = new() { DownloadOnly = before } });
        var applier = new ChangeApplier([new DownloadOnlyValidator()], new FreezeStore(_directory), new AuditTrail(_directory));
        var proposal = new AgentChange
        {
            Category = AgentCategories.DownloadOnly, Target = target, Before = before, After = !before,
            Confidence = 1, Reasoning = "Unverified model claim about zero win rate", EvidenceRef = "races-fixture.jsonl"
        };
        return (config, applier, proposal, []);
    }
}
