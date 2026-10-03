using System.IO;
using System.Reflection;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

[Collection("Config read-only mode")]
public sealed class RaceQueuePersistenceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 3, 3, 0, 0, DateTimeKind.Utc);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gldrive-racequeue-" + Guid.NewGuid().ToString("N"));
    private readonly bool _wasReadOnly = ConfigManager.ReadOnly;

    public RaceQueuePersistenceTests()
    {
        Directory.CreateDirectory(_directory);
        ConfigManager.ReadOnly = false;
    }

    public void Dispose()
    {
        ConfigManager.ReadOnly = _wasReadOnly;
        Directory.Delete(_directory, recursive: true);
    }

    private string QueuePath => Path.Combine(_directory, "race-queue.json");

    private static PersistedRace Race(string release, TimeSpan age, bool active = false, int resumes = 0) =>
        new("tv-hd", release, ["src", "dst"], SpreadMode.Race, "src", "/incoming/tv-hd/" + release,
            Now - age, resumes, active);

    private static List<PersistedRace> ReadFile(string path) =>
        JsonSerializer.Deserialize<List<PersistedRace>>(File.ReadAllText(path))!;

    [Fact]
    public void Restore_PutsInFlightRacesFirstAndCountsTheResume()
    {
        var result = RaceQueueStore.Restore(
            [Race("Queued.A", TimeSpan.FromHours(1)), Race("Running.B", TimeSpan.FromHours(2), active: true)], Now);

        Assert.Equal(["Running.B", "Queued.A"], result.Races.Select(r => r.ReleaseName));
        Assert.Equal(1, result.Races[0].Resumes);
        Assert.Equal(0, result.Races[1].Resumes);
        Assert.All(result.Races, r => Assert.False(r.WasActive));
        Assert.Equal(1, result.Resumed);
    }

    [Fact]
    public void Restore_DropsRequestsOlderThanMaxAge()
    {
        var result = RaceQueueStore.Restore(
            [Race("Fresh", RaceQueueStore.MaxAge - TimeSpan.FromMinutes(1)),
             Race("Stale", RaceQueueStore.MaxAge + TimeSpan.FromMinutes(1))], Now);

        Assert.Equal(["Fresh"], result.Races.Select(r => r.ReleaseName));
        Assert.Equal(1, result.DroppedStale);
    }

    [Fact]
    public void Restore_StopsResumingARaceThatKeepsDyingWithTheProcess()
    {
        var result = RaceQueueStore.Restore(
            [Race("Crashy", TimeSpan.FromMinutes(5), active: true, resumes: RaceQueueStore.MaxResumes),
             Race("Once", TimeSpan.FromMinutes(5), active: true, resumes: RaceQueueStore.MaxResumes - 1)], Now);

        Assert.Equal(["Once"], result.Races.Select(r => r.ReleaseName));
        Assert.Equal(RaceQueueStore.MaxResumes, result.Races[0].Resumes);
        Assert.Equal(1, result.DroppedResumeCap);
    }

    [Fact]
    public void Restore_ResumeCapDoesNotApplyToRacesThatNeverStarted()
    {
        var result = RaceQueueStore.Restore([Race("Waiting", TimeSpan.FromMinutes(5), resumes: 99)], Now);
        Assert.Single(result.Races);
    }

    [Fact]
    public void Restore_DeduplicatesReleasesCaseInsensitively()
    {
        var result = RaceQueueStore.Restore(
            [Race("Show.S01E01", TimeSpan.FromMinutes(1)), Race("show.s01e01", TimeSpan.FromMinutes(2), active: true)], Now);

        var only = Assert.Single(result.Races);
        Assert.Equal("show.s01e01", only.ReleaseName); // the in-flight copy wins
    }

    [Fact]
    public void Store_RoundTripsAndSkipsNullSnapshots()
    {
        var store = new RaceQueueStore(QueuePath);
        var races = new List<PersistedRace> { Race("A", TimeSpan.Zero, active: true), Race("B", TimeSpan.Zero) };
        store.Save(() => races);
        store.Save(() => null);

        Assert.Equal(races, store.Load(), new PersistedRaceComparer());
    }

    [Fact]
    public void Store_CorruptFileLoadsEmptyAndKeepsEvidence()
    {
        File.WriteAllText(QueuePath, "{ not json");
        var store = new RaceQueueStore(QueuePath);

        Assert.Empty(store.Load());
        Assert.Equal("{ not json", File.ReadAllText(QueuePath + ".corrupt"));
    }

    [Fact]
    public void Manager_RestoresQueueAcrossRestartsAndDisposeDoesNotEraseIt()
    {
        new RaceQueueStore(QueuePath).Save(() =>
            [Race("Running.B", TimeSpan.FromMinutes(10), active: true) with { QueuedAtUtc = DateTime.UtcNow.AddMinutes(-10) },
             Race("Queued.A", TimeSpan.FromMinutes(5)) with { QueuedAtUtc = DateTime.UtcNow.AddMinutes(-5) }]);

        using (var manager = new SpreadManager(new AppConfig(), _directory))
        {
            // Restored entries take part in duplicate suppression: a re-announce of a
            // queued release must not throw on the (missing) pools, it must be skipped.
            Assert.Null(manager.StartRace("tv-hd", "Queued.A", ["src", "dst"], SpreadMode.Race));
        }

        var persisted = ReadFile(QueuePath);
        Assert.Equal(["Running.B", "Queued.A"], persisted.Select(r => r.ReleaseName));
        Assert.Equal(1, persisted[0].Resumes);
        Assert.All(persisted, r => Assert.False(r.WasActive));
    }

    [Fact]
    public void Manager_DequeueHoldsRacesWhosePoolsAreNotConnected()
    {
        new RaceQueueStore(QueuePath).Save(() =>
            [Race("Queued.A", TimeSpan.Zero) with { QueuedAtUtc = DateTime.UtcNow }]);

        using (var manager = new SpreadManager(new AppConfig(), _directory))
        {
            // No spread pools exist. The old dequeue removed the race, then threw
            // "Need at least 2 connected spread pools" and lost it.
            typeof(SpreadManager).GetMethod("DequeueNextRace", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(manager, null);
        }

        Assert.Equal(["Queued.A"], ReadFile(QueuePath).Select(r => r.ReleaseName));
    }

    [Fact]
    public void Manager_DequeueExpiresRequestsOlderThanMaxAge()
    {
        new RaceQueueStore(QueuePath).Save(() =>
            [Race("Old", TimeSpan.Zero) with { QueuedAtUtc = DateTime.UtcNow - RaceQueueStore.MaxAge + TimeSpan.FromMilliseconds(300) },
             Race("New", TimeSpan.Zero) with { QueuedAtUtc = DateTime.UtcNow }]);

        using (var manager = new SpreadManager(new AppConfig(), _directory))
        {
            Thread.Sleep(600);
            typeof(SpreadManager).GetMethod("DequeueNextRace", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(manager, null);
        }

        Assert.Equal(["New"], ReadFile(QueuePath).Select(r => r.ReleaseName));
    }

    [Fact]
    public void ReadOnlyManager_NeitherRestoresNorWritesTheQueue()
    {
        ConfigManager.ReadOnly = true;
        using (var manager = new SpreadManager(new AppConfig(), _directory)) { }
        Assert.False(File.Exists(QueuePath));
    }

    private sealed class PersistedRaceComparer : IEqualityComparer<PersistedRace>
    {
        private static readonly List<string> Shared = [];

        public bool Equals(PersistedRace? x, PersistedRace? y) =>
            x is not null && y is not null &&
            x with { ServerIds = Shared } == y with { ServerIds = Shared } &&
            x.ServerIds.SequenceEqual(y.ServerIds);

        public int GetHashCode(PersistedRace obj) => obj.ReleaseName.GetHashCode();
    }
}
