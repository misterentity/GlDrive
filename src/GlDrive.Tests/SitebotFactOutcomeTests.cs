using System.IO;
using System.Text.Json;
using GlDrive.AiAgent;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class SitebotFactOutcomeTests
{
    private readonly DateTime _now = new(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);
    private const string Release = "Example.Show.S01E01.1080p.WEB.H264-GROUP";
    private SitebotEvent Complete(string? winner = "our-user", bool solo = true) => new()
    {
        ServerId = "dest", Release = Release, Channel = "#test", BotNick = "sitebot",
        ObservedAtUtc = _now, Kind = SitebotEventKind.Complete, Winner = winner, Solo = solo,
        Bytes = 1024 * 1024, Files = 10
    };
    private SitebotReleaseFacts Facts(SitebotEvent observation) => new()
    {
        ServerId = observation.ServerId, Release = observation.Release, Complete = observation, Latest = observation
    };
    private RaceDestinationOutcome Evaluate(SitebotEvent observation, bool confirmed = true, DateTime? listing = null) =>
        SitebotOutcomePolicy.Evaluate("dest", "our-user", Facts(observation), _now.AddMinutes(-1), _now.AddMinutes(1),
            listing ?? _now.AddSeconds(1), confirmed, 10, true);

    [Theory]
    [InlineData("our-user", true, "solo")]
    [InlineData("OUR-USER", false, "won")]
    [InlineData("competitor", true, "lost")]
    [InlineData("competitor", false, "lost")]
    [InlineData(null, false, "unknown")]
    public void FinalExplicitWinnersArePerDestinationAccountOutcomes(string? winner, bool solo, string expected)
    {
        var result = Evaluate(Complete(winner, solo));
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(expected != "unknown", SitebotOutcomePolicy.HasObservedWinner(result));
        Assert.Equal(expected is "won" or "solo", SitebotOutcomePolicy.IsOwnWin(result));
        Assert.Equal(10, result.AnnouncedFiles);
    }

    [Theory]
    [InlineData(SitebotEventKind.New)]
    [InlineData(SitebotEventKind.Halfway)]
    [InlineData(SitebotEventKind.Progress)]
    [InlineData(SitebotEventKind.Race)]
    public void ProgressAndLeaderNeverBecomeWinnerEvenWithCompletedInventory(SitebotEventKind kind)
    {
        var result = Evaluate(Complete() with { Kind = kind, Leader = "our-user" });
        Assert.Equal("unknown", result.Outcome);
        Assert.Null(result.WinnerUser);
    }

    [Fact]
    public void CompleteHintAloneCannotVerifyCompletionOrCreditDelivery()
    {
        var result = Evaluate(Complete(), confirmed: false);
        Assert.False(result.CompletionVerified);
        Assert.Equal("unknown", result.Outcome);
        Assert.False(SitebotOutcomePolicy.HasObservedWinner(result));
    }

    [Fact]
    public void WinnerNeedsScheduledSuccessfulListingStartedAfterHint()
    {
        Assert.Equal("unknown", Evaluate(Complete(), listing: _now.AddSeconds(-1)).Outcome);
        Assert.Equal("solo", Evaluate(Complete(), listing: _now.AddSeconds(1)).Outcome);
        var noListing = SitebotOutcomePolicy.Evaluate("dest", "our-user", Facts(Complete()),
            _now.AddMinutes(-1), _now.AddMinutes(1), null, true, 10);
        Assert.False(noListing.CompletionVerified);
        Assert.Equal("unknown", noListing.Outcome);
    }

    [Theory]
    [InlineData(-120)]
    [InlineData(120)]
    public void WinnerOutsideCurrentRaceObservationWindowStaysUnknown(int seconds)
        => Assert.Equal("unknown", Evaluate(Complete() with { ObservedAtUtc = _now.AddSeconds(seconds) }).Outcome);

    [Fact]
    public void AnnouncedFileCountComparisonIsAdvisoryAndOptIn()
    {
        var facts = Facts(Complete() with { Files = 12 });
        var off = SitebotOutcomePolicy.Evaluate("dest", "our-user", facts, _now.AddMinutes(-1), _now.AddMinutes(1),
            _now.AddSeconds(1), true, 10);
        var on = SitebotOutcomePolicy.Evaluate("dest", "our-user", facts, _now.AddMinutes(-1), _now.AddMinutes(1),
            _now.AddSeconds(1), true, 10, true);
        Assert.Null(off.InventoryMatchesAnnounce);
        Assert.False(on.InventoryMatchesAnnounce);
        Assert.True(on.CompletionVerified); // actual engine evidence is preserved
    }

    [Fact]
    public void FactsAreIsolatedByExactServerIdentityAndCaseInsensitiveRelease()
    {
        var store = new SitebotFactStore(() => _now, 10, TimeSpan.FromHours(1));
        Assert.True(store.Observe(Complete()));
        Assert.Equal(1024 * 1024, store.TryGet("dest", Release.ToLowerInvariant())!.AnnouncedBytes);
        Assert.Null(store.TryGet("DEST", Release));
        Assert.Null(store.TryGet("other", Release));
        Assert.Null(store.TryGet("dest", "Another.Release-GROUP"));
    }

    [Fact]
    public void FactsExpireIndividuallyAndBoundedCapacityEvictsOldest()
    {
        var now = _now;
        var store = new SitebotFactStore(() => now, 2, TimeSpan.FromMinutes(10));
        store.Observe(Complete());
        now = now.AddMinutes(9);
        store.Observe(Complete() with { Kind = SitebotEventKind.Metadata, ObservedAtUtc = now, ImdbId = "tt1234567" });
        now = now.AddMinutes(2);
        Assert.Null(store.TryGet("dest", Release)!.Complete);
        Assert.NotNull(store.TryGet("dest", Release)!.Metadata);
        store.Observe(Complete() with { Release = "Second.Release-GROUP", ObservedAtUtc = now });
        now = now.AddSeconds(1);
        store.Observe(Complete() with { Release = "Third.Release-GROUP", ObservedAtUtc = now });
        Assert.Equal(2, store.Snapshot().Count);
        Assert.Null(store.TryGet("dest", Release));
        now = now.AddMinutes(11);
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void RejectsStaleFutureAndOutOfOrderObservations()
    {
        var store = new SitebotFactStore(() => _now, 10, TimeSpan.FromMinutes(10));
        Assert.False(store.Observe(Complete() with { ObservedAtUtc = _now.AddMinutes(-11) }));
        Assert.False(store.Observe(Complete() with { ObservedAtUtc = _now.AddMinutes(2) }));
        Assert.False(store.Observe(Complete() with { ServerId = "" }));
        Assert.True(store.Observe(Complete()));
        Assert.False(store.Observe(Complete() with { ObservedAtUtc = _now.AddSeconds(-1), Winner = "other" }));
        Assert.Equal("our-user", store.TryGet("dest", Release)!.Complete!.Winner);
    }

    [Theory]
    [InlineData(SitebotEventKind.New)]
    [InlineData(SitebotEventKind.Nuke)]
    [InlineData(SitebotEventKind.Unnuke)]
    [InlineData(SitebotEventKind.Progress)]
    [InlineData(SitebotEventKind.Halfway)]
    [InlineData(SitebotEventKind.Race)]
    public void NewOrIncompleteEvidenceRevokesPriorCompletion(SitebotEventKind kind)
    {
        var store = new SitebotFactStore(() => _now, 10, TimeSpan.FromMinutes(10));
        store.Observe(Complete());
        store.Observe(Complete() with { Kind = kind });
        Assert.Null(store.TryGet("dest", Release)!.Complete);
    }

    [Fact]
    public void ConflictingExplicitWinnersFailClosedUntilNewLifecycle()
    {
        var store = new SitebotFactStore(() => _now, 10, TimeSpan.FromMinutes(10));
        store.Observe(Complete()); store.Observe(Complete("other"));
        var facts = store.TryGet("dest", Release)!;
        Assert.True(facts.ConflictingWinner);
        Assert.Equal("unknown", SitebotOutcomePolicy.Evaluate("dest", "our-user", facts, _now.AddMinutes(-1),
            _now.AddMinutes(1), _now.AddSeconds(1), true, 10).Outcome);
        store.Observe(Complete() with { Kind = SitebotEventKind.New });
        store.Observe(Complete());
        Assert.False(store.TryGet("dest", Release)!.ConflictingWinner);
    }

    [Fact]
    public void MetadataCannotMutateStoredCollectionsAndDoesNotEraseCompletionSize()
    {
        var genres = new List<string> { "Drama" };
        var store = new SitebotFactStore(() => _now, 10, TimeSpan.FromMinutes(10));
        store.Observe(Complete());
        store.Observe(Complete() with { Kind = SitebotEventKind.Metadata, Genres = genres });
        genres.Add("Comedy");
        Assert.Single(store.TryGet("dest", Release)!.Metadata!.Genres);
        Assert.Equal(10, store.TryGet("dest", Release)!.AnnouncedFiles);
        store.Observe(Complete() with { Files = null, Bytes = null });
        Assert.Equal(10, store.TryGet("dest", Release)!.AnnouncedFiles);
    }

    private RaceOutcomeEvent Race(params RaceDestinationOutcome[] outcomes) => new()
    {
        RaceId = "race", Section = "tv", Release = Release, Winner = null,
        Participants = [new("source", "src", 0, 10, 0, null), new("dest", "dst", 1024, 10, 0, null)],
        DestinationOutcomes = outcomes.ToList()
    };

    [Fact]
    public void DigestCountsVerifiedDestinationWinnersWithoutGivingSourceCreditOrCountingUnknownLosses()
    {
        var won = Evaluate(Complete());
        var lost = Evaluate(Complete("other"));
        var unknown = won with { Outcome = "unknown", WinnerUser = null };
        var digest = new RacesDigester().Build([Race(won), Race(lost), Race(unknown)]);
        Assert.Equal(2, digest.KnownWinnerRaces);
        Assert.Equal(2, digest.KnownWinnerSamplesByServer["dest"]);
        Assert.Equal(0.5, digest.WinRateByServer["dest"]);
        Assert.Equal(0, digest.KnownWinnerSamplesByServer["source"]);
        Assert.False(digest.WinRateByServer.ContainsKey("source"));
        Assert.Equal(2d / 3, digest.WinnerCoverageByServer["dest"]);
    }

    [Fact]
    public void DuplicateOrUnverifiedDestinationEvidenceCannotRaiseCoverage()
    {
        var won = Evaluate(Complete());
        Assert.False(RacesDigester.HasObservedWinner(Race(won, won)));
        Assert.False(RacesDigester.HasObservedWinner(Race(won with { CompletionVerified = false })));
        Assert.False(RacesDigester.HasObservedWinner(Race(won with { ServerId = "absent" })));
        Assert.False(RacesDigester.HasObservedWinner(Race(won with { ServerId = "source" })));
    }

    [Fact]
    public void HistoryAndTelemetryRoundtripKeepWinnerAccountSeparateFromLegacyWinner()
    {
        var outcome = Evaluate(Complete());
        var telemetry = JsonSerializer.Deserialize<RaceOutcomeEvent>(JsonSerializer.Serialize(Race(outcome)))!;
        Assert.Null(telemetry.Winner);
        Assert.Equal("our-user", Assert.Single(telemetry.DestinationOutcomes).WinnerUser);
        var path = Path.Combine(Path.GetTempPath(), "sitebot-history-" + Guid.NewGuid().ToString("N"), "history.json");
        try
        {
            var store = new RaceHistoryStore(path);
            store.Add(new() { Id = "race", DestinationOutcomes = [outcome], QueueWaitSeconds = 35 });
            var restored = new RaceHistoryStore(path); restored.Load();
            Assert.Equal(35, Assert.Single(restored.Items).QueueWaitSeconds);
            Assert.Equal(outcome, Assert.Single(restored.Items[0].DestinationOutcomes));
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
}
