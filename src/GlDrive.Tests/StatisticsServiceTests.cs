using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using GlDrive.Services.Automation;
using GlDrive.Services.Statistics;
using GlDrive.Spread;
using Xunit;
using Fixture = GlDrive.Tests.BoundedTelemetryReaderTests.TelemetryFixture;

namespace GlDrive.Tests;

public sealed class StatisticsServiceTests
{
    [Fact]
    public async Task Large_streams_cannot_starve_download_and_health_evidence_and_report_own_coverage()
    {
        using var files = new Fixture();
        files.Raw("races", string.Join("\n", Enumerable.Range(0, 100).Select(index => Row(new
            { raceId = "race-" + index, release = "Synthetic.Release", section = "movies", result = "complete" }))));
        files.Raw("transfers", string.Join("\n", Enumerable.Repeat(Row(new
            { srcServer = "source", dstServer = "destination", bytes = 10L, transferMode = "Relay" }), 100)));
        files.Raw("downloads", Row(new { downloadId = "download", serverId = "source", result = "complete", bytes = 100L }));
        files.Raw("site-health", Row(new { serverId = "source", windowStart = Now.AddHours(-1), windowEnd = Now.AddMinutes(-5) }));
        var result = await Service(files, limits: new(MaxBytes: 4096, MaxRows: 100, MaxMilliseconds: 30_000)).ReadAsync(90);
        Assert.Equal(1, result.Downloads.Completed);
        Assert.False(result.Downloads.Partial);
        Assert.Equal(100, Assert.Single(result.Traffic, row => row.ServerId == "source").DownloadBytes);
        Assert.Single(result.Health);
        Assert.True(Assert.Single(result.Coverage.Streams, row => row.Stream == "transfers").Partial);
        Assert.False(Assert.Single(result.Coverage.Streams, row => row.Stream == "downloads").Partial);
        Assert.InRange(result.Coverage.BytesRead, 1, 4096);
    }

    [Fact]
    public async Task Truncated_download_window_marks_totals_unknown_instead_of_reporting_a_success_rate()
    {
        using var files = new Fixture();
        files.Raw("downloads", string.Join("\n", Enumerable.Range(0, 100).Select(index => Row(new
            { downloadId = "download-" + index, serverId = "source", result = "complete", bytes = 100L }))));
        var result = await Service(files, limits: new(MaxBytes: 2048, MaxMilliseconds: 30_000)).ReadAsync(90);
        Assert.True(result.Downloads.Partial);
        Assert.Null(result.Downloads.SuccessRate);
        Assert.True(Assert.Single(result.Coverage.Streams, row => row.Stream == "downloads").Partial);
    }

    [Fact]
    public void History_snapshot_is_bounded_even_when_loaded_legacy_file_is_over_capacity()
    {
        using var files = new Fixture();
        var path = Path.Combine(files.Root, "history.json");
        File.WriteAllText(path, JsonSerializer.Serialize(Enumerable.Range(0, 1200).Select(i => new RaceHistoryItem { Id = i.ToString() })));
        var store = new RaceHistoryStore(path); store.Load();
        var rows = store.ReadSnapshot(11);
        Assert.Equal(11, rows.Count); Assert.Equal("0", rows[0].Id); Assert.Equal("10", rows[^1].Id);
        Assert.Equal(1200, JsonSerializer.Deserialize<RaceHistoryItem[]>(File.ReadAllText(path))!.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadSnapshot(10_002));
    }

    [Fact]
    public void Median_and_percentiles_interpolate_bounded_samples()
    {
        Assert.Null(StatisticsService.Percentile([], .5));
        Assert.Equal(30, StatisticsService.Percentile([30], .95));
        Assert.Equal(60, StatisticsService.Percentile([30, 90], .5));
        Assert.Equal(87, StatisticsService.Percentile([30, 90], .95));
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static string Row(object values)
    {
        var node = JsonSerializer.SerializeToNode(values)!.AsObject();
        node["ts"] ??= Now.AddMinutes(-5).ToString("O");
        return node.ToJsonString();
    }
    private static StatisticsService Service(Fixture files, IReadOnlyList<RaceHistoryItem>? history = null,
        TelemetryReadLimits? limits = null, Func<Func<string, string>>? redact = null)
        => new(files.Root, () => history ?? [], redact, () => Now, limits ?? new(MaxMilliseconds: 30_000));

    [Fact]
    public async Task Completed_endpoint_bytes_include_relay_and_keep_legacy_transport_unknown()
    {
        using var files = new Fixture();
        files.Raw("transfers", Row(new { srcServer = "source", dstServer = "destination", bytes = 100L, elapsedMs = 1000, transferMode = "Relay" }) + "\n" +
            Row(new { srcServer = "source", dstServer = "destination", bytes = 50L, elapsedMs = 1000, fxpMode = "Race" }) + "\n" +
            Row(new { srcServer = "source", dstServer = "destination", bytes = 900L, elapsedMs = 1, abortReason = "failed" }));
        var result = await Service(files).ReadAsync(1);
        var source = Assert.Single(result.Traffic, x => x.ServerId == "source");
        var dest = Assert.Single(result.Traffic, x => x.ServerId == "destination");
        Assert.Equal(150, source.SentBytes); Assert.Equal(0, source.ReceivedBytes);
        Assert.Equal(150, dest.ReceivedBytes); Assert.Equal(100, dest.RelayReceivedBytes);
        Assert.Equal(50, source.UnknownTransportSentBytes);
        Assert.Equal(2, Assert.Single(result.Routes).CompletedTransfers);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("17")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task Malformed_abort_reason_never_proves_expected_bytes_were_delivered(string abort)
    {
        using var files = new Fixture();
        var node = JsonNode.Parse(Row(new { srcServer = "a", dstServer = "b", bytes = 999 }))!.AsObject();
        node["abortReason"] = JsonNode.Parse(abort); files.Raw("transfers", node.ToJsonString());
        var result = await Service(files).ReadAsync(1);
        Assert.Empty(result.Traffic); Assert.Empty(result.Routes); Assert.True(result.Coverage.Partial);
    }

    [Fact]
    public async Task Optional_duration_and_mode_errors_keep_valid_completed_byte_evidence()
    {
        using var files = new Fixture();
        files.Raw("transfers", Row(new { srcServer = "a", dstServer = "b", bytes = 1024, elapsedMs = new { bad = true }, transferMode = new[] { "Relay" } }));
        var result = await Service(files).ReadAsync(1);
        Assert.Equal(1024, Assert.Single(result.Traffic, x => x.ServerId == "a").SentBytes);
        var route = Assert.Single(result.Routes); Assert.Equal(0, route.TimedTransfers); Assert.Null(route.MiBPerSecond);
    }

    [Fact]
    public async Task Route_speed_uses_total_completed_bytes_over_total_measured_time()
    {
        using var files = new Fixture();
        files.Raw("transfers", Row(new { srcServer = "a", dstServer = "b", bytes = 1_048_576, elapsedMs = 1000 }) + "\n" +
            Row(new { srcServer = "a", dstServer = "b", bytes = 3_145_728, elapsedMs = 3000 }));
        var route = Assert.Single((await Service(files).ReadAsync(1)).Routes);
        Assert.Equal(1d, route.MiBPerSecond); Assert.Equal(2, route.TimedTransfers);
    }

    [Fact]
    public async Task Download_success_uses_latest_terminal_outcome_per_server_and_id()
    {
        using var files = new Fixture();
        files.Raw("downloads", Row(new { ts = Now.AddHours(-2).ToString("O"), serverId = "a", downloadId = "same", result = "failed", bytes = 700 }) + "\n" +
            Row(new { serverId = "a", downloadId = "same", result = "complete", bytes = 200 }) + "\n" +
            Row(new { serverId = "b", downloadId = "same", result = "failed", bytes = 600 }) + "\n" +
            Row(new { serverId = "a", downloadId = "retry", result = "retried", bytes = 800 }));
        var result = await Service(files).ReadAsync(1);
        Assert.Equal(1, result.Downloads.Completed); Assert.Equal(1, result.Downloads.Failed);
        Assert.Equal(.5, result.Downloads.SuccessRate);
        Assert.Equal(200, Assert.Single(result.Traffic).DownloadBytes);
    }

    [Fact]
    public async Task Queue_wait_and_announce_lag_keep_sample_counts_and_section_identity()
    {
        using var files = new Fixture(); var started = Now.AddHours(-1);
        files.Raw("races", Row(new { raceId = "r1", release = "Synthetic.Release", section = "movies", startedAt = started.ToString("O"), result = "complete", queueWaitSeconds = 30 }) + "\n" +
            Row(new { raceId = "r2", release = "Other.Release", section = "movies", startedAt = started.ToString("O"), result = "aborted", queueWaitSeconds = "bad" }));
        files.Raw("matched-announces", Row(new { ts = started.AddSeconds(-60).ToString("O"), release = "synthetic.release", section = "MOVIES" }) + "\n" +
            Row(new { ts = started.AddSeconds(-1).ToString("O"), release = "Synthetic.Release", section = "audio" }) + "\n" +
            Row(new { ts = started.AddSeconds(1).ToString("O"), release = "Synthetic.Release", section = "movies" }));
        var result = await Service(files).ReadAsync(1);
        Assert.Equal(2, result.Races.Observed); Assert.Equal(1, result.Races.Completed);
        Assert.Equal(1, result.Races.QueueWaitSamples); Assert.Equal(30, result.Races.QueueWaitMedianSeconds);
        Assert.Equal(1, result.Races.AnnounceLagSamples); Assert.Equal(60, result.Races.AnnounceLagMedianSeconds);
        Assert.Null(result.Races.CleanRate);
    }

    [Fact]
    public async Task Clean_rate_is_history_evidence_and_does_not_equate_completed_telemetry_with_clean()
    {
        using var files = new Fixture();
        files.Raw("races", Row(new { raceId = "r1", result = "complete" }) + "\n" + Row(new { raceId = "r2", result = "complete" }));
        var history = new[]
        {
            new RaceHistoryItem { Id = "r1", CompletedAt = Now.AddMinutes(-5).UtcDateTime, Result = SpreadJobState.Completed, FilesTotal = 10, FilesDelivered = 4, CleanComplete = false },
            new RaceHistoryItem { Id = "r2", CompletedAt = Now.AddMinutes(-4).UtcDateTime, Result = SpreadJobState.Completed, CleanComplete = true }
        };
        var result = await Service(files, history).ReadAsync(1);
        Assert.Equal(2, result.Races.Completed); Assert.Equal(2, result.Races.CleanEvidenceSamples);
        Assert.Equal(1, result.Races.Clean); Assert.Equal(.5, result.Races.CleanRate);
    }

    [Fact]
    public async Task Legacy_history_without_inventory_is_not_an_observed_unclean_completion()
    {
        using var files = new Fixture();
        var result = await Service(files, [new RaceHistoryItem { Id = "legacy", CompletedAt = Now.AddMinutes(-5).UtcDateTime,
            Result = SpreadJobState.Completed }]).ReadAsync(1);
        Assert.Equal(0, result.Races.CleanEvidenceSamples); Assert.Null(result.Races.CleanRate);
    }

    [Fact]
    public async Task Known_duplicate_skip_does_not_count_expected_payload_or_route_speed()
    {
        using var files = new Fixture();
        files.Raw("transfers", Row(new { srcServer = "a", dstServer = "b", bytes = 999, elapsedMs = 1, duplicateSkipped = true }));
        var result = await Service(files).ReadAsync(1);
        Assert.Empty(result.Traffic); Assert.Empty(result.Routes);
    }

    [Fact]
    public async Task Health_unions_overlapping_sample_windows_without_inventing_uptime()
    {
        using var files = new Fixture();
        files.Raw("site-health", Row(new { serverId = "site", windowStart = Now.AddHours(-2).ToString("O"), windowEnd = Now.AddHours(-1).ToString("O") }) + "\n" +
            Row(new { serverId = "site", windowStart = Now.AddMinutes(-90).ToString("O"), windowEnd = Now.AddMinutes(-30).ToString("O") }));
        var site = Assert.Single((await Service(files).ReadAsync(1)).Health);
        Assert.Equal(2, site.Samples); Assert.Equal(1.5, site.ObservedWindowHours, 5);
        Assert.Equal(6.25, site.WindowCoveragePercent, 5); Assert.Null(site.UptimePercent);
    }

    [Fact]
    public async Task Route_cardinality_is_bounded_and_reports_partial_coverage()
    {
        using var files = new Fixture();
        files.Raw("transfers", string.Join('\n', Enumerable.Range(0, 300).Select(i => Row(new { srcServer = "source-" + i, dstServer = "destination", bytes = 1 }))));
        var result = await Service(files).ReadAsync(1);
        Assert.Equal(StatisticsService.MaxRoutes, result.Routes.Count);
        Assert.Contains("route-limit", result.Coverage.Limits);
    }

    [Fact]
    public async Task Exposed_identifiers_use_injected_current_secret_redactor()
    {
        using var files = new Fixture(); files.Raw("transfers", Row(new { srcServer = "a-secret-value", dstServer = "destination", bytes = 1 }));
        var result = await Service(files, redact: () => text => text.Replace("secret-value", "[redacted]", StringComparison.Ordinal)).ReadAsync(1);
        var json = JsonSerializer.Serialize(result); Assert.DoesNotContain("secret-value", json);
        Assert.Contains("[redacted]", result.Routes[0].Source);
    }

    [Fact]
    public async Task Cancelled_report_does_not_read_history()
    {
        using var files = new Fixture(); var reads = 0;
        var service = new StatisticsService(files.Root, () => { reads++; return []; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(7, new CancellationToken(true)));
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData("{\"days\":0}")]
    [InlineData("{\"days\":91}")]
    [InlineData("{\"days\":\"7\"}")]
    [InlineData("{\"days\":null}")]
    [InlineData("{\"days\":1.5}")]
    public async Task Mcp_rejects_invalid_window_before_service_access(string json)
    {
        var calls = 0; var module = new StatisticsAutomation(() => { calls++; throw new InvalidOperationException(); });
        using var arguments = JsonDocument.Parse(json);
        await Assert.ThrowsAsync<ArgumentException>(() => module.ExecuteAsync("stats.summary", arguments.RootElement, default));
        Assert.Equal(0, calls); Assert.True(Assert.Single(module.Tools).ReadOnly);
    }

    [Fact]
    public async Task Mcp_reads_the_same_passive_summary_as_the_local_ui()
    {
        using var files = new Fixture(); var service = Service(files);
        var module = new StatisticsAutomation(() => service); using var arguments = JsonDocument.Parse("{\"days\":7}");
        var result = Assert.IsType<StatisticsSummary>(await module.ExecuteAsync("stats.summary", arguments.RootElement, default));
        Assert.Empty(result.Traffic); Assert.Equal(Now.AddDays(-7), result.FromUtc); Assert.Null(result.Downloads.SuccessRate);
    }
}
