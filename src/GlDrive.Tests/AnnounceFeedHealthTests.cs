using System.IO;
using System.Text.Json;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public sealed class AnnounceFeedHealthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-feed-health-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);
    private string StatePath => Path.Combine(_root, "feed.json");
    private static AnnounceFeedObservation Online(string server = "site-a", string channel = "#releases") => new(server, channel, true, true, true);
    private AnnounceFeedHealthMonitor Monitor() => new(StatePath, () => _now);

    private void Seed(params string[] channels)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(channels.Select(channel => new AnnounceFeedState
        {
            ServerId = "site-a", Channel = channel, Counts = Enumerable.Repeat(40d, 168).ToArray(),
            ExposureSeconds = Enumerable.Repeat(7200d, 168).ToArray(), LastAnnounceAtUtc = _now
        }).ToArray()));
    }

    private List<AnnounceFeedNotice> Minutes(AnnounceFeedHealthMonitor monitor, int minutes, params AnnounceFeedObservation[] inputs)
    {
        var notices = new List<AnnounceFeedNotice>();
        for (var i = 0; i < minutes; i++) { _now = _now.AddMinutes(1); notices.AddRange(monitor.Tick(inputs)); }
        return notices;
    }

    [Fact]
    public void ExpectedCountUsesEachUtcHourAcrossDayAndWeekBoundaries()
    {
        var counts = new double[168];
        var exposure = Enumerable.Repeat(7200d, 168).ToArray();
        var from = new DateTime(2026, 1, 10, 23, 30, 0, DateTimeKind.Utc);
        counts[167] = 20; // Saturday 23:00, ten per hour.
        counts[0] = 40; // Sunday 00:00, twenty per hour.
        Assert.Equal(15, AnnounceFeedHealthPolicy.Expected(counts, exposure, from, from.AddHours(1)), 8);
        Assert.Equal(0, AnnounceFeedHealthPolicy.Expected(counts, exposure, from.AddHours(2), from.AddHours(3)));
    }

    [Fact]
    public void ColdStartSampleRateAndOneHourFloorsAreIndependent()
    {
        var counts = Enumerable.Repeat(19d, 168).ToArray();
        var exposure = Enumerable.Repeat(7200d, 168).ToArray();
        Assert.Equal(0, AnnounceFeedHealthPolicy.Rate(counts, exposure, 0));
        counts[0] = 20;
        exposure[0] = 7199;
        Assert.Equal(0, AnnounceFeedHealthPolicy.Rate(counts, exposure, 0));
        exposure[0] = 86400;
        Assert.Equal(0, AnnounceFeedHealthPolicy.Rate(counts, exposure, 0));
        Assert.False(AnnounceFeedHealthPolicy.IsSilent(3599, 100));
        Assert.False(AnnounceFeedHealthPolicy.IsSilent(7200, 6.9));
        Assert.True(AnnounceFeedHealthPolicy.IsSilent(3600, 7));
    }

    [Fact]
    public void LearningCountsOnlyObservedJoinedExposureAndNeverSleepOrRestartGaps()
    {
        var monitor = Monitor();
        monitor.Tick([Online()]);
        _now = _now.AddSeconds(30);
        monitor.ObserveAnnounce("site-a", "#releases", _now);
        _now = _now.AddSeconds(30);
        monitor.Tick([Online()]);
        _now = _now.AddHours(12);
        monitor.Tick([Online()]);
        Assert.True(monitor.Save());
        monitor = Monitor();
        _now = _now.AddDays(2);
        monitor.Tick([Online() with { Connected = false, Joined = false }]);
        Minutes(monitor, 3, Online() with { Connected = false, Joined = false });
        Assert.True(monitor.Save());
        var state = Assert.Single(JsonSerializer.Deserialize<AnnounceFeedState[]>(File.ReadAllText(StatePath))!);
        Assert.Equal(60, state.ExposureSeconds.Sum(), 8);
        Assert.Equal(1, state.Counts.Sum());
        Assert.Equal(3.5, Assert.Single(monitor.Snapshot()).ObservedSilenceMinutes, 8);
    }

    [Fact]
    public void RemovingAChannelFromMonitoringPausesItsExposureEvenWithinOneTick()
    {
        var monitor = Monitor();
        monitor.Tick([Online()]);
        _now = _now.AddSeconds(30);
        monitor.Tick([]);
        _now = _now.AddSeconds(30);
        monitor.Tick([Online()]);
        monitor.Save();
        var state = Assert.Single(JsonSerializer.Deserialize<AnnounceFeedState[]>(File.ReadAllText(StatePath))!);
        Assert.Equal(0, state.ExposureSeconds.Sum());
        Assert.Equal(0, state.SilenceSeconds);
    }

    [Fact]
    public void OutageAndRecoveryNotifyOnceIncludingRestart()
    {
        Seed("#releases");
        var monitor = Monitor();
        monitor.Tick([Online()]);
        Assert.Empty(Minutes(monitor, 59, Online()));
        var down = Assert.Single(Minutes(monitor, 1, Online()));
        Assert.False(down.Recovered);
        Assert.True(down.ExpectedAnnounces > 6.9);
        Assert.Equal("No matched announces; cause unknown", down.Diagnosis);
        Assert.Empty(Minutes(monitor, 20, Online()));
        monitor = Monitor();
        _now = _now.AddHours(6);
        Assert.Empty(monitor.Tick([Online()]));
        _now = _now.AddSeconds(1);
        Assert.True(monitor.ObserveAnnounce("site-a", "#releases", _now)!.Recovered);
        Assert.Null(monitor.ObserveAnnounce("site-a", "#releases", _now));
        monitor = Monitor();
        Assert.Empty(monitor.Tick([Online()]));
        Assert.Equal("Healthy", Assert.Single(monitor.Snapshot()).State);
    }

    [Fact]
    public void AHealthyChannelDoesNotHideAnotherChannelOrAnotherServersSilence()
    {
        Seed("#quiet", "#busy");
        var monitor = Monitor();
        var inputs = new[] { Online(channel: "#quiet"), Online(channel: "#busy"), Online("site-b", "#quiet") };
        monitor.Tick(inputs);
        var notices = new List<AnnounceFeedNotice>();
        for (var minute = 0; minute < 60; minute++)
        {
            _now = _now.AddMinutes(1);
            monitor.ObserveAnnounce("site-a", "#busy", _now);
            notices.AddRange(monitor.Tick(inputs));
        }
        Assert.Equal("#quiet", Assert.Single(notices).Channel);
        Assert.Equal("site-a", notices[0].ServerId);
        Assert.Equal("Learning", monitor.Snapshot().Single(s => s.ServerId == "site-b").State);
        Assert.Equal("Healthy", monitor.Snapshot().Single(s => s.Channel == "#busy").State);
    }

    [Theory]
    [InlineData(false, false, null, 0, "IRC disconnected")]
    [InlineData(true, false, null, 0, "Channel not joined")]
    [InlineData(true, true, false, 0, "Configured sitebot absent from channel snapshot")]
    [InlineData(true, true, true, 3, "FiSH decryption failures observed")]
    [InlineData(true, true, null, 0, "No matched announces; cause unknown")]
    public void DiagnosisNamesOnlyObservedLocalEvidence(bool connected, bool joined, bool? bot, int failures, string expected) =>
        Assert.Equal(expected, AnnounceFeedHealthPolicy.Diagnose(new("site-a", "#releases", connected, joined, bot, failures)));

    [Fact]
    public void RecoveryBudgetIsSharedByServerAndPersistsAcrossRestart()
    {
        Seed("#one", "#two");
        var monitor = Monitor();
        var inputs = new[] { Online(channel: "#one") with { Joined = false }, Online(channel: "#two") with { Joined = false } };
        monitor.Tick(inputs);
        Assert.Equal(2, Minutes(monitor, 60, inputs).Count);
        Assert.True(monitor.TryBeginRecovery("site-a", "#one"));
        Assert.False(monitor.TryBeginRecovery("site-a", "#two"));
        monitor = Monitor();
        monitor.Tick(inputs);
        Assert.False(monitor.TryBeginRecovery("site-a", "#two"));
        _now = _now.AddMinutes(30);
        monitor.Tick(inputs);
        Assert.True(monitor.TryBeginRecovery("site-a", "#two"));
    }

    [Theory]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, true)]
    public void RecoveryDoesNotReplaceExistingIrcPolicy(bool connected, bool autoJoin, bool joined, bool pending) =>
        Assert.False(AnnounceFeedHealthPolicy.RecoveryAllowed(connected, autoJoin, joined, pending, _now, null));

    [Fact]
    public void RecoveryClockRollbackOrCooldownCannotRearmAnAttempt()
    {
        Assert.True(AnnounceFeedHealthPolicy.RecoveryAllowed(true, true, false, false, _now, null));
        Assert.False(AnnounceFeedHealthPolicy.RecoveryAllowed(true, true, false, false, _now, _now.AddMinutes(1)));
        Assert.False(AnnounceFeedHealthPolicy.RecoveryAllowed(true, true, false, false, _now, _now.AddMinutes(-29)));
        Assert.True(AnnounceFeedHealthPolicy.RecoveryAllowed(true, true, false, false, _now, _now.AddMinutes(-30)));
    }

    [Fact]
    public void CorruptStateIsPreservedWithoutRepeatedNotices()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(StatePath, "unreadable");
        var monitor = Monitor();
        monitor.Tick([Online()]);
        Assert.False(monitor.PersistenceHealthy);
        Assert.False(monitor.Save());
        Assert.Empty(Minutes(monitor, 120, Online()));
        Assert.Equal("unreadable", File.ReadAllText(StatePath));
    }

    [Fact]
    public void FailedTransitionSaveSuppressesNoticeUntilDurable()
    {
        Seed("#releases");
        var monitor = Monitor();
        File.Delete(StatePath);
        Directory.CreateDirectory(StatePath);
        monitor.Tick([Online()]);
        Assert.Empty(Minutes(monitor, 60, Online()));
        Assert.False(monitor.PersistenceHealthy);
        Directory.Delete(StatePath);
        Assert.Single(Minutes(monitor, 1, Online()));
        Assert.True(monitor.PersistenceHealthy);
        Assert.Empty(Minutes(monitor, 1, Online()));
    }

    [Fact]
    public void FailedRecoverySaveRetriesItsNoticeOnTheNextHealthyTick()
    {
        Seed("#releases");
        var monitor = Monitor();
        monitor.Tick([Online()]);
        Assert.Single(Minutes(monitor, 60, Online()));
        File.Delete(StatePath);
        Directory.CreateDirectory(StatePath);
        _now = _now.AddSeconds(1);
        Assert.Null(monitor.ObserveAnnounce("site-a", "#releases", _now));
        Directory.Delete(StatePath);
        Assert.True(Assert.Single(Minutes(monitor, 1, Online())).Recovered);
        Assert.Empty(Minutes(monitor, 1, Online()));
    }

    [Fact]
    public void ExposureCapPreservesRateWhileBoundingOldHistory()
    {
        var counts = Enumerable.Repeat(240d, 168).ToArray();
        var exposure = Enumerable.Repeat(86400d, 168).ToArray();
        AnnounceFeedHealthPolicy.AddExposure(counts, exposure, _now, _now.AddMinutes(1));
        var hour = AnnounceFeedHealthPolicy.HourOfWeek(_now);
        Assert.Equal(86400, exposure[hour]);
        Assert.Equal(240d / 86460, counts[hour] / exposure[hour], 10);
        Assert.Equal(240, counts[(hour + 1) % 168]);
    }

    [Fact]
    public void StaleFutureInvalidAndPrivateMessageObservationsDoNotCreateBaseline()
    {
        var monitor = Monitor();
        Assert.Null(monitor.ObserveAnnounce("site-a", "#releases", _now.AddMinutes(-3)));
        Assert.Null(monitor.ObserveAnnounce("site-a", "#releases", _now.AddMinutes(2)));
        Assert.Null(monitor.ObserveAnnounce("site-a", "private-nick", _now));
        Assert.Null(monitor.ObserveAnnounce("site-a", "#releases", DateTime.SpecifyKind(_now, DateTimeKind.Unspecified)));
        Assert.Empty(monitor.Snapshot());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
