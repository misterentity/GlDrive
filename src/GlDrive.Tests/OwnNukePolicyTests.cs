using System.Collections.Concurrent;
using System.IO;
using FluentFTP;
using GlDrive.AiAgent;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class OwnNukePolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-own-nuke-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);
    private const string Release = "Synthetic.Show.S01E01.1080p.WEB-GROUP";
    private string StatePath => Path.Combine(_root, "state.json");
    private OwnNukeAlertTracker Tracker() => new(StatePath, () => _now);
    private ParsedNuke Poll(string? nukee = "member", string release = Release, bool exactTime = false) =>
        new(_now.AddHours(-2), "operator", release, 3, "bad.pack", "TV", nukee, exactTime);
    private SitebotEvent Fact(SitebotEventKind kind = SitebotEventKind.Nuke, params string[] nukees) => new()
    {
        ServerId = "site-a", Release = Release, Kind = kind, ObservedAtUtc = _now,
        Actor = "operator", Nukees = nukees.Length > 0 ? nukees : ["member"], NukeFactor = 3, Reason = "bad.pack"
    };

    [Fact]
    public void BoxedTwoRowFormatSeparatesNukerNukeeFactorReasonAndAge()
    {
        var text = $"200-| Nuker | Nukee | Amount | Reason |\n200-| operator | member | 5x 24.4M | -Auto- Not completed for 12 hours |\n200-| Age: 2d 3h | Dir: /site/TV/{Release} |";
        var parsed = Assert.Single(NukeParser.Parse(text, observedAtUtc: _now));
        Assert.Equal("operator", parsed.Nuker);
        Assert.Equal("member", parsed.Nukee);
        Assert.Equal(5, parsed.Multiplier);
        Assert.Equal("-Auto- Not completed for 12 hours", parsed.Reason);
        Assert.Equal(_now.AddDays(-2).AddHours(-3), parsed.NukedAt);
        Assert.Equal(_now.AddDays(-2).AddHours(-4), parsed.EarliestNukedAtUtc);
        Assert.Equal(Release, parsed.Release);
        Assert.Equal("TV", parsed.Section);
        Assert.False(parsed.HasExactTimestamp);
        Assert.False(parsed.ReleaseTruncated);
    }

    [Fact]
    public void BoxedTruncationIsExplicitAndMalformedRowsCannotBorrowAnEarlierNukee()
    {
        var text = "| operator | member | 3x20 | reason |\n| Age: 1h | Dir: Synthetic.Show.S01... |\n" +
            "| Age: 2h | Dir: Another.Show.S01E01.1080p-GROUP |\n" +
            "| operator | member | not-an-amount | reason |\n| Age: 1h | Dir: Another.Show.S01E02.1080p-GROUP |";
        var parsed = Assert.Single(NukeParser.Parse(text, observedAtUtc: _now));
        Assert.True(parsed.ReleaseTruncated);
    }

    [Theory]
    [InlineData("2026-01-05 08:00:00 by operator: Synthetic.Show.S01E01.1080p.WEB-GROUP (3x) - bad.pack")]
    [InlineData("Nuke Synthetic.Show.S01E01.1080p.WEB-GROUP (3x) by operator at 2026-01-05 08:00 - bad.pack")]
    [InlineData("Synthetic.Show.S01E01.1080p.WEB-GROUP (3x) operator 2026-01-05 08:00 bad.pack")]
    public void LegacyTimestampFormatsRemainSupportedWithoutInventingANukee(string line)
    {
        var parsed = Assert.Single(NukeParser.Parse(line));
        Assert.Equal(Release, parsed.Release);
        Assert.Null(parsed.Nukee);
        Assert.True(parsed.HasExactTimestamp);
        Assert.Null(Tracker().ObservePoll("site-a", "operator", parsed));
    }

    [Theory]
    [InlineData("member", "member", true)]
    [InlineData("member", "MEMBER", true)]
    [InlineData("member", "othermember", false)]
    [InlineData("member", "member2", false)]
    [InlineData("member", "mem...", false)]
    [InlineData("member", "member*", false)]
    [InlineData("member", "member,other", false)]
    [InlineData("member", " member", false)]
    [InlineData("", "", false)]
    public void OwnAttributionRequiresOneExplicitExactAccount(string account, string nukee, bool expected) =>
        Assert.Equal(expected, OwnNukeAttribution.IsOwn(account, [nukee]));

    [Fact]
    public void PollAndIrcAlertOnlyOnceAcrossRestartAndChangingRelativeAges()
    {
        var tracker = Tracker();
        Assert.NotNull(tracker.ObservePoll("site-a", "member", Poll()));
        _now = _now.AddHours(6);
        Assert.Null(tracker.ObservePoll("site-a", "member", Poll()));
        Assert.Null(tracker.ObserveSitebot(Fact(), "member"));
        tracker = Tracker();
        Assert.Null(tracker.ObserveSitebot(Fact(), "member"));
        Assert.True(tracker.IsNuked("site-a", Release));
        Assert.True(Assert.Single(tracker.Snapshot()).AlertedOwn);
    }

    [Fact]
    public void OthersNukesBlockUnsafeWorkButOnlyExplicitNukeeAttributionAlerts()
    {
        var tracker = Tracker();
        Assert.Null(tracker.ObserveSitebot(Fact(SitebotEventKind.Nuke, "someone-else") with { Actor = "member" }, "member"));
        Assert.True(tracker.IsNuked("site-a", Release));
        Assert.NotNull(tracker.ObserveSitebot(Fact(SitebotEventKind.Nuke, "someone-else", "member"), "member"));
        Assert.Null(tracker.ObserveSitebot(Fact(), "member"));
        Assert.False(tracker.IsNuked("site-b", Release));
        Assert.NotNull(tracker.ObservePoll("site-b", "member", Poll()));
    }

    [Fact]
    public void TruncatedReleaseCanNameAnAlertButNeverBlocksOrClaimsAFullRelease()
    {
        var tracker = Tracker();
        var parsed = Poll(release: "Synthetic.Show.S01...") with { ReleaseTruncated = true };
        Assert.True(tracker.ObservePoll("site-a", "member", parsed)!.ReleaseTruncated);
        Assert.False(tracker.IsNuked("site-a", parsed.Release));
        Assert.False(tracker.IsNuked("site-a", Release));
        // Prefix-resolved to a known full name it stays approximate: SITE NUKES cuts Dir inside
        // the group, so another group's nuke of the same episode shares the raced prefix.
        tracker.ObservePoll("site-b", "member", Poll(release: Release) with { ReleaseTruncated = true });
        Assert.False(tracker.IsNuked("site-b", Release));
    }

    [Fact]
    public void UnnukeResetsOnlyForFreshExplicitNukeAndStaleRelativeRowsNeverReAlert()
    {
        var tracker = Tracker();
        var original = Fact();
        Assert.NotNull(tracker.ObserveSitebot(original, "member"));
        _now = _now.AddMinutes(1);
        tracker.ObserveSitebot(Fact(SitebotEventKind.Unnuke), "member");
        Assert.False(tracker.IsNuked("site-a", Release));
        Assert.Null(tracker.ObserveSitebot(original, "member"));
        _now = _now.AddMinutes(1);
        Assert.Null(tracker.ObservePoll("site-a", "member", Poll()));
        Assert.False(tracker.IsNuked("site-a", Release));
        Assert.NotNull(tracker.ObserveSitebot(Fact(), "member"));
        Assert.Null(tracker.ObserveSitebot(Fact(), "member"));
        Assert.Null(Tracker().ObserveSitebot(Fact(), "member"));
    }

    [Fact]
    public void RelativeRowStartsNewEpisodeOnlyWhenItsWholeAgeIntervalIsAfterUnnuke()
    {
        var tracker = Tracker();
        tracker.ObserveUnnuke("site-a", Release, _now);
        _now = _now.AddHours(1);
        var uncertain = Assert.Single(NukeParser.Parse($"| operator | member | 3x20 | bad.pack |\n| Age: 0h | Dir: {Release} |",
            observedAtUtc: _now));
        Assert.Null(tracker.ObservePoll("site-a", "member", uncertain));
        Assert.False(tracker.IsNuked("site-a", Release));
        _now = _now.AddHours(1);
        var fresh = Assert.Single(NukeParser.Parse($"| operator | member | 3x20 | bad.pack |\n| Age: 0h | Dir: {Release} |",
            observedAtUtc: _now));
        Assert.NotNull(tracker.ObservePoll("site-a", "member", fresh));
        Assert.True(tracker.IsNuked("site-a", Release));
        Assert.Null(tracker.ObservePoll("site-a", "member", fresh));
    }

    [Fact]
    public void UnnukeWithoutPreviousNukeStillSuppressesOlderExplicitObservation()
    {
        var tracker = Tracker();
        var old = Fact();
        _now = _now.AddMinutes(1);
        tracker.ObserveSitebot(Fact(SitebotEventKind.Unnuke), "member");
        Assert.Null(tracker.ObserveSitebot(old, "member"));
        Assert.False(tracker.IsNuked("site-a", Release));
    }

    // The v3.14 review: race admission read "state unavailable" as "nuked", so an unreadable
    // file or a full store (every nuke on every site is recorded) held every race.
    [Fact]
    public void CorruptStateIsQuarantinedAndNewEvidenceIsPersisted()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(StatePath, "not-json");
        var tracker = Tracker();
        Assert.False(tracker.PersistenceHealthy);
        Assert.False(tracker.StateAvailable);
        Assert.False(tracker.IsNuked("site-a", Release));
        Assert.NotNull(tracker.ObservePoll("site-a", "member", Poll()));
        Assert.True(tracker.IsNuked("site-a", Release));
        Assert.True(tracker.PersistenceHealthy);
        Assert.Equal("not-json", File.ReadAllText(StatePath + ".corrupt"));
        Assert.True(Tracker().IsNuked("site-a", Release));
    }

    [Fact]
    public void FullStoreEvictsTheOldestObservationsAndKeepsRecording()
    {
        var tracker = new OwnNukeAlertTracker(StatePath, () => _now, maximumEntries: 2);
        Assert.NotNull(tracker.ObservePoll("site-a", "member", Poll()));
        _now = _now.AddMinutes(1);
        Assert.NotNull(tracker.ObservePoll("site-b", "member", Poll()));
        _now = _now.AddMinutes(1);
        Assert.NotNull(tracker.ObservePoll("site-c", "member", Poll()));
        Assert.True(tracker.StateAvailable);
        Assert.False(tracker.IsNuked("site-a", Release)); // oldest row made room
        Assert.True(tracker.IsNuked("site-b", Release));
        Assert.True(tracker.IsNuked("site-c", Release));
        tracker = new OwnNukeAlertTracker(StatePath, () => _now, maximumEntries: 2);
        Assert.True(tracker.StateAvailable);
        Assert.True(tracker.IsNuked("site-c", Release));
    }

    [Fact]
    public void RelativeTelemetryIdentityIgnoresAgeAcrossRestart()
    {
        var cursor = new NukeCursorStore(_root);
        Assert.True(cursor.TryRecordRelative("site-a", Poll()));
        _now = _now.AddHours(6);
        Assert.False(cursor.TryRecordRelative("site-a", Poll()));
        Assert.False(new NukeCursorStore(_root).TryRecordRelative("site-a", Poll()));
        Assert.True(cursor.TryRecordRelative("site-b", Poll()));
        Assert.Equal(DateTime.MinValue, cursor.Get("site-a"));
    }

    // Boxed SITE NUKES rows are all age-only, so every row lands here; a hard cap or an
    // unreadable file used to stop nuke telemetry permanently.
    [Fact]
    public void RelativeTelemetryKeepsRecordingPastItsCapAndAfterACorruptFile()
    {
        var cursor = new NukeCursorStore(_root, maxRelativeRows: 10);
        for (var i = 0; i < 25; i++)
            Assert.True(cursor.TryRecordRelative("site-a", Poll(release: $"Synthetic.Show.S01E{i:00}.1080p.WEB-GROUP")));
        Assert.False(cursor.TryRecordRelative("site-a", Poll(release: "Synthetic.Show.S01E24.1080p.WEB-GROUP")));
        Assert.True(cursor.TryRecordRelative("site-a", Poll(release: "Synthetic.Show.S01E00.1080p.WEB-GROUP"))); // evicted

        File.WriteAllText(Path.Combine(_root, "nuke-relative-rows.json"), "not-json");
        var reopened = new NukeCursorStore(_root);
        Assert.True(reopened.TryRecordRelative("site-a", Poll()));
        Assert.True(File.Exists(Path.Combine(_root, "nuke-relative-rows.json.corrupt")));
    }

    [Fact]
    public async Task PollerPublishesEveryObservationButWritesRelativeTelemetryOnlyOnce()
    {
        var emitted = new ConcurrentQueue<string>();
        using var recorder = new TelemetryRecorder(_root, 64, (path, text, _) =>
        {
            if (Path.GetFileName(path).StartsWith("nukes-")) emitted.Enqueue(text);
            return Task.CompletedTask;
        });
        var observations = new List<ParsedNuke>();
        var calls = 0;
        using var poller = new NukePoller(recorder, () => [new("site-a", () => true, _ =>
        {
            calls++;
            return Task.FromResult(new FtpReply { Code = "200", InfoMessages =
                $"| operator | member | 3x20 | bad.pack |\n| Age: {calls}h | Dir: {Release} |" });
        })], new NukeCursorStore(_root), _root, 6, () => _now, startTimer: false);
        poller.NukeObserved += (_, _) => throw new InvalidOperationException("isolated observer");
        poller.NukeObserved += (_, nuke) => observations.Add(nuke);
        await poller.PollAllAsync();
        _now = _now.AddHours(6);
        await poller.PollAllAsync();
        recorder.Dispose();
        Assert.Equal(2, calls);
        Assert.Equal(2, observations.Count);
        Assert.Single(emitted);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
