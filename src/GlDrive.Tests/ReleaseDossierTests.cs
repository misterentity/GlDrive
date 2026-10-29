using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services.Operations;
using GlDrive.Services.Statistics;
using Xunit;
using Fixture = GlDrive.Tests.BoundedTelemetryReaderTests.TelemetryFixture;

namespace GlDrive.Tests;

public sealed class ReleaseDossierTests
{
    private const string Release = "Example.Movie.2026.1080p.WEB.H264-TEST";

    [Fact]
    public async Task Noisy_telemetry_cannot_starve_other_streams_or_local_and_journal_evidence()
    {
        using var files = new Fixture(); var now = DateTimeOffset.UtcNow; var ts = now.AddMinutes(-1);
        files.Raw("races", JsonSerializer.Serialize(new { ts, release = Release, raceId = "race-one", result = "complete" }));
        files.Raw("wishlist-attempts", string.Join("\n", Enumerable.Repeat(JsonSerializer.Serialize(new
            { ts, release = Release, matched = false, missReason = new string('x', 200) }), 200)));
        files.Raw("downloads", JsonSerializer.Serialize(new { ts, remotePath = "/movies/" + Release, result = "complete", bytes = 100 }));
        files.Raw("nukes", JsonSerializer.Serialize(new { ts, release = Release, reason = "fixture" }));
        files.Raw("transfers", JsonSerializer.Serialize(new { ts, raceId = "race-one", file = "video.mkv" }));
        var service = new ReleaseDossierService(files.Root, () => new(),
            _ => new([new(now.AddHours(-1), "local-download", "source", "Retained local evidence")], [], false, false),
            _ => [new(now.AddHours(-2), "health-decision", "Warning", "source", Release, "Retained journal evidence", "application", "")],
            now: () => now, limits: new(MaxBytes: 8192, MaxRows: 100, MaxMilliseconds: 30_000));
        var result = await service.ReadAsync(Release);
        foreach (var stage in new[] { "download-outcome", "nuke-observation", "transfer-outcome", "local-download", "health-decision" })
            Assert.Contains(result.Timeline, row => row.Stage == stage);
        Assert.True(Assert.Single(result.Coverage.Streams, stream => stream.Stream == "wishlist-attempts").Partial);
        Assert.False(Assert.Single(result.Coverage.Streams, stream => stream.Stream == "downloads").Partial);
        Assert.InRange(result.Coverage.BytesRead, 1, 8192);
    }

    [Fact]
    public async Task Result_trimming_reserves_a_place_for_older_independent_evidence()
    {
        using var files = new Fixture(); var now = DateTimeOffset.UtcNow;
        files.Raw("matched-announces", string.Join("\n", Enumerable.Range(0, 700).Select(index =>
            JsonSerializer.Serialize(new { ts = now.AddSeconds(-index), release = Release, section = "movies" }))));
        var service = new ReleaseDossierService(files.Root, () => new(),
            _ => new([new(now.AddHours(-1), "local-download", "source", "Observed locally")], [], false, false),
            _ => [new(now.AddHours(-2), "health-decision", "Warning", "source", Release, "Decision", "application", "")], now: () => now);
        var result = await service.ReadAsync(Release);
        Assert.Equal(500, result.Timeline.Count);
        Assert.Contains(result.Timeline, row => row.Stage == "local-download");
        Assert.Contains(result.Timeline, row => row.Stage == "health-decision");
        Assert.Contains("dossier-result-limit", result.Coverage.Limits);
    }

    [Fact]
    public async Task Timeline_joins_existing_sources_and_preserves_exact_release_identity()
    {
        using var files = new Fixture(); var now = DateTimeOffset.UtcNow; var ts = now.AddMinutes(-1).ToString("O");
        files.Raw("races", JsonSerializer.Serialize(new { ts, release = Release, raceId = "race-one", result = "complete", queueWaitSeconds = 12 }));
        files.Raw("matched-announces", JsonSerializer.Serialize(new { ts, release = Release.ToUpperInvariant(), serverId = "source", section = "movies", ruleSource = "custom" }));
        files.Raw("wishlist-attempts", JsonSerializer.Serialize(new { ts, release = Release, matched = "malformed", wishlistItemId = "wanted" }));
        files.Raw("downloads", JsonSerializer.Serialize(new { ts, remotePath = "/movies/" + Release + "/video.mkv", result = "completed", bytes = 2048 }));
        files.Raw("nukes", JsonSerializer.Serialize(new { ts, release = Release, reason = "fixture" }));
        files.Raw("transfers", JsonSerializer.Serialize(new { ts, raceId = "race-one", file = "/different/path", srcServer = "source", dstServer = "destination" }));
        var service = new ReleaseDossierService(files.Root, () => new(),
            _ => new([new(now.AddMinutes(-2), "recent-sighting", "source", "Observed")], [], false, false),
            _ => [new(now.AddMinutes(-1), "auto-race-verdict", "Information", "", Release, "Queued", "application", "")], now: () => now);
        var report = await service.ReadAsync(Release, "movies");
        Assert.Equal(8, report.Timeline.Count);
        Assert.Contains(report.Timeline, row => row.Stage == "wishlist-verdict" && row.Detail.Contains("unknown"));
        Assert.Contains(report.Timeline, row => row.Stage == "nuke-observation" && row.Detail.Contains("Approximate"));
        Assert.Contains(report.Timeline, row => row.Stage == "race-outcome" && row.Detail.Contains("12.0 s"));
        Assert.Equal(report.Timeline.OrderBy(row => row.Time), report.Timeline);
        Assert.True(ReleaseDossierService.PathContainsRelease("/section/" + Release + "/file", Release));
        Assert.False(ReleaseDossierService.PathContainsRelease("/section/" + Release + ".extra/file", Release));
        Assert.DoesNotContain((await service.ReadAsync("Example.Movie.2026", "movies")).Timeline, row => row.Stage is "download-outcome" or "transfer-outcome");
    }

    [Fact]
    public async Task Incomplete_download_evidence_does_not_disable_an_intact_wishlist_prediction()
    {
        using var files = new Fixture(); var config = new AppConfig(); config.Downloads.AutoDownloadWishlist = true;
        var item = new WishlistItem { Title = "Example Movie", Year = 2026, Type = MediaType.Movie };
        var service = new ReleaseDossierService(files.Root, () => config, _ => new([], [item], true, false));
        var report = await service.ReadAsync(Release, "movies");
        Assert.True(report.Coverage.Partial);
        Assert.Contains(report.Predictions, row => row.Stage == "wishlist" && row.Verdict == "conditional");
        Assert.DoesNotContain(report.Coverage.Limits, reason => reason == "wishlist-snapshot-incomplete");
    }

    [Fact]
    public async Task Journal_failure_retains_other_evidence_and_marks_incomplete()
    {
        using var files = new Fixture(); var now = DateTimeOffset.UtcNow;
        var service = new ReleaseDossierService(files.Root, () => new(),
            _ => new([new(now.AddMinutes(-1), "recent-sighting", "source", "Observed")], [], false, false),
            _ => throw new IOException("fixture"), now: () => now);
        var report = await service.ReadAsync(Release);
        Assert.Single(report.Timeline);
        Assert.Contains("ops-journal-unavailable", report.Coverage.Limits);
    }

    [Fact]
    public async Task Result_cap_keeps_newest_rows_and_reports_the_gap()
    {
        using var files = new Fixture(); var now = DateTimeOffset.UtcNow;
        var service = new ReleaseDossierService(files.Root, () => new(), _ => new(Enumerable.Range(1, 600)
            .Select(i => new DossierEvent(now.AddSeconds(-i), "fixture", "", i.ToString())).ToArray(), [], false, false), now: () => now);
        var report = await service.ReadAsync(Release);
        Assert.Equal(500, report.Timeline.Count); Assert.Contains("dossier-result-limit", report.Coverage.Limits);
        Assert.Equal("1", report.Timeline[^1].Detail); Assert.Equal("500", report.Timeline[0].Detail);
    }

    [Fact]
    public async Task Cancelled_report_never_reaches_snapshot_providers()
    {
        using var files = new Fixture(); var calls = 0;
        var service = new ReleaseDossierService(files.Root, () => { calls++; return new(); }, _ => { calls++; return new([], [], false); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(Release, cancellationToken: new(true)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Notification_snapshot_omits_oversized_fields_before_composing_detail()
    {
        var store = new NotificationStore(false);
        store.Add(new NotificationItem { ReleaseName = Release, Category = new string('x', 100_000), ServerId = "source" });
        var snapshot = store.ReadDossierEvents(Release);
        Assert.True(snapshot.Partial); Assert.InRange(Assert.Single(snapshot.Events).Detail.Length, 0, 128);
    }
}
