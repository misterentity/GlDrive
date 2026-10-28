using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GlDrive.AiAgent;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class RaceReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static RaceOutcomeEvent Race(string id, string? outcome = null, bool verified = true,
        bool solo = false, string site = "destination", double? wait = null) => new()
    {
        RaceId = id, Section = "tv-hd", Release = "Example.Show.S01E01.1080p.WEB.H264-GROUP",
        StartedAt = Now.AddHours(-2).ToString("O"), EndedAt = Now.AddHours(-1).ToString("O"),
        Result = "complete", QueueWaitSeconds = wait,
        Participants = [new("source", "src", 0, 10, 0, null), new(site, "dst", 1024, 10, 0, null)],
        DestinationOutcomes = outcome == null ? [] : [new()
        {
            ServerId = site, Outcome = outcome, WinnerUser = outcome == "lost" ? "other-user" : "our-user",
            WinnerObservedAtUtc = Now.AddHours(-1).UtcDateTime, CompletionVerified = verified, Solo = solo
        }]
    };

    [Fact]
    public void Unknown_winners_are_not_losses_and_payload_never_infers_a_win()
    {
        var report = RaceReportBuilder.Build([Race("unknown"), Race("won", "won"), Race("lost", "lost"),
            Race("solo", "solo", solo: true), Race("hint", "won", verified: false)], Now.AddDays(-1), Now);
        Assert.Equal(5, report.Summary.RaceAttempts);
        Assert.Equal(5, report.Summary.DestinationSamples);
        Assert.Equal(3, report.Summary.ObservedWinnerSamples);
        Assert.Equal(2, report.Summary.Wins);
        Assert.Equal(1, report.Summary.Losses);
        Assert.Equal(1, report.Summary.SoloWins);
        Assert.Equal(2, report.Summary.UnknownWinners);
        Assert.Equal(2.0 / 3, report.Summary.WinRate!.Value, 6);
        Assert.Equal(0.6, report.Summary.WinnerCoverage);
        Assert.Equal(5120, report.Summary.RecordedBytesReceived);
        Assert.Null(Assert.Single(report.BySite, b => b.Key == "source").Totals.WinRate);
        var unknown = RaceReportBuilder.Build([Race("unknown")], Now.AddDays(-1), Now);
        Assert.Null(unknown.Summary.WinRate);
        Assert.Equal(0, unknown.Summary.Losses);
    }

    [Fact]
    public void Aggregates_days_sections_sites_partials_and_measured_queue_waits()
    {
        var a = Race("a", "lost", solo: true, wait: 10);
        var b = Race("b", wait: 30) with { Result = "aborted" };
        var c = Race("c", "won", site: "second", wait: 50) with
        {
            Section = "movies", EndedAt = Now.AddDays(-1).ToString("O")
        };
        var report = RaceReportBuilder.Build([a, b, c], Now.AddDays(-2), Now);
        Assert.Equal(2, report.ByDay.Count);
        Assert.Equal(2, report.BySection.Count);
        Assert.Equal(1, report.Summary.PartialAttempts);
        Assert.Equal(1, report.Summary.SoloLosses);
        Assert.Equal(30, report.Summary.MedianQueueWaitSeconds);
        Assert.Equal(46, report.Summary.P90QueueWaitSeconds);
        Assert.Equal(3, report.Summary.QueueWaitSamples);
        Assert.Equal(2, Assert.Single(report.BySite, b => b.Key == "destination").Totals.RaceAttempts);
        Assert.Equal(1, Assert.Single(report.BySection, b => b.Key == "movies").Totals.Wins);
    }

    [Fact]
    public void Rejects_malformed_rows_filters_timestamps_and_deduplicates_attempts()
    {
        var race = Race("a");
        var report = RaceReportBuilder.Build([race, race, Race("outside") with { EndedAt = Now.AddDays(-3).ToString("O") },
            Race("bad") with { Participants = null! }, Race("bad-time") with { EndedAt = "not a date" },
            Race("future") with { EndedAt = Now.AddSeconds(1).ToString("O") }], Now.AddDays(-1), Now);
        Assert.Equal(1, report.Summary.RaceAttempts);
        Assert.Equal(1, report.Coverage.DuplicateRows);
        Assert.Equal(2, report.Coverage.InvalidRows);
        Assert.True(report.Coverage.IsPartial);
        Assert.Equal(Now.AddHours(-1), report.Coverage.FirstSampleUtc);
        Assert.Null(report.Summary.MedianQueueWaitSeconds);
    }

    [Fact]
    public void Duplicate_destination_outcomes_cannot_manufacture_two_wins()
    {
        var race = Race("a", "won");
        var report = RaceReportBuilder.Build([race with { DestinationOutcomes = [race.DestinationOutcomes[0], race.DestinationOutcomes[0]] }],
            Now.AddDays(-1), Now);
        Assert.Equal(0, report.Summary.Wins);
        Assert.Equal(1, report.Coverage.InvalidRows);
    }

    [Fact]
    public void Counter_overflow_saturates_and_invalid_wait_samples_stay_absent()
    {
        var race = Race("a", wait: double.NaN) with { Participants = [new("destination", "dst", long.MaxValue, 1, 0, null)] };
        var report = RaceReportBuilder.Build([race, race with { RaceId = "b", QueueWaitSeconds = -1 }], Now.AddDays(-1), Now);
        Assert.Equal(long.MaxValue, report.Summary.RecordedBytesReceived);
        Assert.Equal(0, report.Summary.QueueWaitSamples);
        Assert.Null(report.Summary.MedianQueueWaitSeconds);
    }

    [Fact]
    public void Reader_reports_malformed_lines_reads_gzip_and_counts_duplicate_archives_once()
    {
        using var root = new TempRoot();
        var json = JsonSerializer.Serialize(Race("a", "won"));
        File.WriteAllText(root.File(), json + "\ninvalid\nnull\n{\"raceId\":\"missing-time\"}\n");
        using (var stream = new GZipStream(File.Create(root.File() + ".gz"), CompressionMode.Compress))
        using (var writer = new StreamWriter(stream)) writer.WriteLine(json);
        var report = new RaceReportReader(root.Path).Build(nowOverride: Now);
        Assert.Equal(1, report.Summary.RaceAttempts);
        Assert.Equal(3, report.Coverage.ParseSkips);
        Assert.Equal(1, report.Coverage.DuplicateRows);
        Assert.Equal(2, report.Coverage.FilesRead);
        Assert.True(report.Coverage.IsPartial);
    }

    [Fact]
    public void Reader_keeps_good_rows_after_oversized_line_and_reports_read_failure()
    {
        using var root = new TempRoot();
        File.WriteAllText(root.File(), new string('x', 3000) + "\n" + JsonSerializer.Serialize(Race("a")));
        File.WriteAllText(root.File() + ".gz", "not gzip");
        var report = new RaceReportReader(root.Path, new(MaxLineCharacters: 2000)).Build(nowOverride: Now);
        Assert.Equal(1, report.Summary.RaceAttempts);
        Assert.Equal(1, report.Coverage.ParseSkips);
        Assert.Equal(1, report.Coverage.ReadErrors);
        Assert.False(report.Coverage.Truncated);
    }

    [Fact]
    public void Reader_bounds_rows_and_decompressed_input()
    {
        using var root = new TempRoot();
        File.WriteAllLines(root.File(), [JsonSerializer.Serialize(Race("a")), JsonSerializer.Serialize(Race("b"))]);
        var limitedRows = new RaceReportReader(root.Path, new(MaxRows: 1)).Build(nowOverride: Now);
        Assert.Equal(1, limitedRows.Summary.RaceAttempts);
        Assert.True(limitedRows.Coverage.Truncated);
        var limitedCharacters = new RaceReportReader(root.Path, new(MaxCharacters: 100)).Build(nowOverride: Now);
        Assert.Equal(0, limitedCharacters.Summary.RaceAttempts);
        Assert.True(limitedCharacters.Coverage.Truncated);
    }

    [Fact]
    public void History_fallback_is_explicit_bounded_and_keeps_unattributed_bytes_separate()
    {
        using var root = new TempRoot();
        var history = Enumerable.Range(0, 501).Select(i => new RaceHistoryItem
        {
            Id = "history-" + i, Section = "movies", StartedAt = Now.AddHours(-2).UtcDateTime,
            CompletedAt = Now.AddHours(-1).UtcDateTime, Result = SpreadJobState.Completed, CleanComplete = true,
            BytesTransferred = 1000
        }).ToArray();
        var report = new RaceReportReader(root.Path).Build(historyFallback: history, nowOverride: Now);
        Assert.Equal("history-fallback", report.Coverage.Source);
        Assert.True(report.Coverage.IsPartial);
        Assert.True(report.Coverage.Truncated);
        Assert.Equal(500, report.Summary.RaceAttempts);
        Assert.Equal(500, report.Summary.AttemptsWithoutDestinationEvidence);
        Assert.Equal(500000, report.Summary.UnattributedTransferredBytes);
        Assert.Equal(0, report.Summary.RecordedBytesReceived);
        Assert.Null(report.Summary.WinRate);
        File.WriteAllText(root.File(), JsonSerializer.Serialize(Race("telemetry")));
        Assert.Equal("telemetry", new RaceReportReader(root.Path).Build(historyFallback: history, nowOverride: Now).Coverage.Source);
    }

    [Fact]
    public void Reader_rejects_unbounded_period_and_honors_cancellation()
    {
        using var root = new TempRoot();
        var reader = new RaceReportReader(root.Path);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Build(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Build(91));
        Assert.Throws<OperationCanceledException>(() => reader.Build(nowOverride: Now, cancellationToken: new(true)));
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "race-report-" + Guid.NewGuid().ToString("N"));
        public TempRoot() => Directory.CreateDirectory(Path);
        public string File() => System.IO.Path.Combine(Path, $"races-{Now.LocalDateTime:yyyyMMdd}.jsonl");
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
