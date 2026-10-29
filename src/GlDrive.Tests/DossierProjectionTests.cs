using System.Text.Json;
using GlDrive.Services.Operations;
using GlDrive.Spread;
using Xunit;
using Fixture = GlDrive.Tests.BoundedTelemetryReaderTests.TelemetryFixture;

namespace GlDrive.Tests;

public sealed class DossierProjectionTests
{
    [Fact]
    public async Task Real_destination_outcome_shape_survives_the_release_join()
    {
        using var files = new Fixture(); var now = DateTimeOffset.UtcNow;
        files.Raw("races", JsonSerializer.Serialize(new
        {
            ts = now.AddMinutes(-1).ToString("O"), raceId = "fixture-race", release = "Synthetic.Release", result = "complete",
            destinationOutcomes = new[] { new RaceDestinationOutcome { ServerId = "destination", Outcome = "solo", CompletionVerified = true } }
        }));
        var service = new ReleaseDossierService(files.Root, () => new(), _ => new([], [], false), now: () => now);
        var report = await service.ReadAsync("Synthetic.Release", "movies", 1);
        var row = Assert.Single(report.Timeline, entry => entry.Stage == "race-destination");
        Assert.Equal("destination", row.ServerId); Assert.Contains("solo", row.Detail);
        Assert.Contains(report.Timeline, entry => entry.Stage == "race-outcome");
    }

    [Fact]
    public async Task Malformed_optional_destination_rows_do_not_discard_the_valid_race_outcome()
    {
        using var files = new Fixture(); var now = DateTimeOffset.UtcNow;
        files.Raw("races", JsonSerializer.Serialize(new
        {
            ts = now.AddMinutes(-1).ToString("O"), raceId = "fixture-race", release = "Synthetic.Release", result = "aborted",
            destinationOutcomes = new object[] { false, new { serverId = "destination", outcome = "unknown" } }
        }));
        var service = new ReleaseDossierService(files.Root, () => new(), _ => new([], [], false), now: () => now);
        var report = await service.ReadAsync("Synthetic.Release", "movies", 1);
        Assert.Contains(report.Timeline, entry => entry.Stage == "race-outcome" && entry.Detail.Contains("aborted", StringComparison.Ordinal));
        Assert.Contains(report.Timeline, entry => entry.Stage == "race-destination" && entry.ServerId == "destination");
    }

    [Fact]
    public async Task Redactor_expansion_is_bounded_without_returning_unredacted_text()
    {
        using var files = new Fixture(); var now = DateTimeOffset.UtcNow;
        files.Raw("matched-announces", JsonSerializer.Serialize(new
        {
            ts = now.AddMinutes(-1).ToString("O"), serverId = "private-fixture-value", release = "Synthetic.Release", section = "movies"
        }));
        var service = new ReleaseDossierService(files.Root, () => new(), _ => new([], [], false),
            redactor: () => _ => new string('r', 20_000), now: () => now);
        var report = await service.ReadAsync("Synthetic.Release", "movies", 1);
        Assert.NotEmpty(report.Timeline);
        Assert.All(report.Timeline, row => { Assert.InRange(row.ServerId.Length, 0, 4096); Assert.InRange(row.Detail.Length, 0, 4096); });
        Assert.DoesNotContain("private-fixture-value", JsonSerializer.Serialize(report));
    }
}
