using GlDrive.Spread;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class QueuedRaceProjectionTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Waiting_race_keeps_release_reason_and_next_probe_visible()
    {
        var next = Now.AddMinutes(2);
        var race = Request() with { WaitReason = "Waiting for remaining release files", NextProbeAtUtc = next };
        var row = QueuedRaceVm.From(race, Now);
        Assert.Equal(race.Section, row.Section);
        Assert.Equal(race.ReleaseName, row.ReleaseName);
        Assert.Equal(race.WaitReason, row.StatusText);
        Assert.Equal($"Next check {next.ToLocalTime():HH:mm:ss}", row.RetryText);
    }

    [Fact]
    public void Ordinary_queued_race_shows_admission_reason_without_a_false_retry_time()
    {
        var row = QueuedRaceVm.From(Request(), Now);
        Assert.Equal("Queued for an available race slot", row.StatusText);
        Assert.Empty(row.RetryText);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void Due_probe_does_not_show_an_old_countdown(int offsetSeconds)
    {
        var row = QueuedRaceVm.From(Request() with { NextProbeAtUtc = Now.AddSeconds(offsetSeconds) }, Now);
        Assert.Equal("Ready to check", row.RetryText);
    }

    [Fact]
    public void Stable_waiting_rows_do_not_notify_or_change_identity_on_refresh()
    {
        var race = Request() with { WaitReason = "Waiting for server reconnection", NextProbeAtUtc = Now.AddMinutes(2) };
        var row = QueuedRaceVm.From(race, Now);
        var notices = 0; row.PropertyChanged += (_, _) => notices++;
        var hash = row.GetHashCode();
        Assert.False(row.UpdateFrom(QueuedRaceVm.From(race, Now.AddSeconds(2))));
        Assert.Equal(0, notices);
        Assert.True(row.UpdateFrom(QueuedRaceVm.From(race with { WaitReason = "Waiting for slot" }, Now)));
        Assert.Equal(1, notices);
        Assert.Equal(hash, row.GetHashCode());
    }

    private static PersistedRace Request() => new("TV", "Show.Release-GRP", ["a", "b"],
        SpreadMode.Race, "a", "/TV/Show.Release-GRP", Now);
}
