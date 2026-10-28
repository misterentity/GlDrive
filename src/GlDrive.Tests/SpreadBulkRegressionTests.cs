using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class SpreadBulkRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-spread-batch-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void Batch_admission_saves_once_outside_engine_lock_and_preserves_duplicate_and_hold_checks()
    {
        using var manager = new SpreadManager(new AppConfig(), _root);
        var writes = new List<string>();
        SetStore(manager, (_, text) =>
        {
            Assert.False(Field<Lock>(manager, "_lock").IsHeldByCurrentThread);
            writes.Add(text);
        });
        manager.IsReleaseNuked = (_, release) => release == "Held.Release";
        Field<ConcurrentDictionary<(string, string), (DateTime, string)>>(manager, "_recentlyDeadRaces")
            [("", "suppressed.release")] = (DateTime.UtcNow.AddHours(1), "recent failure");
        var result = manager.EnqueueRacesDetailed([Request("First.Release"), Request("Second.Release"),
            Request("First.Release"), Request("Held.Release"), Request("Suppressed.Release")]);
        Assert.Equal([RaceStartOutcome.Queued, RaceStartOutcome.Queued, RaceStartOutcome.Duplicate,
            RaceStartOutcome.Unavailable, RaceStartOutcome.Suppressed], result.Select(item => item.Outcome));
        Assert.Equal(2, manager.QueuedRaces.Count);
        var saved = JsonSerializer.Deserialize<List<PersistedRace>>(Assert.Single(writes))!;
        Assert.Equal(["First.Release", "Second.Release"], saved.Select(item => item.ReleaseName));
        Assert.All(saved, item => Assert.Equal(["source", "destination"], item.ServerIds));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mixed_batch_treats_active_and_starting_duplicates_without_looking_them_up_in_queue(bool active)
    {
        using var manager = new SpreadManager(new AppConfig(), _root);
        using var job = new SpreadJob("video", "Existing.Release", SpreadMode.Race, new(), new(), new(), new(), new SpeedTracker(), new SkiplistEvaluator());
        var request = RaceControlPolicyTests.Request() with { ReleaseName = job.ReleaseName };
        if (active)
        {
            Field<List<SpreadJob>>(manager, "_activeJobs").Add(job);
            Field<Dictionary<SpreadJob, PersistedRace>>(manager, "_activeRequests")[job] = request;
        }
        else Field<List<PersistedRace>>(manager, "_startingRequests").Add(request);
        var writes = 0; SetStore(manager, (_, _) => writes++);
        var results = manager.EnqueueRacesDetailed([Request("Existing.Release"), Request("New.Release")]);
        Assert.Equal([RaceStartOutcome.AlreadyActive, RaceStartOutcome.Queued], results.Select(result => result.Outcome));
        Assert.Equal("New.Release", Assert.Single(manager.QueuedRaces).ReleaseName);
        Assert.Equal(1, writes);
        Field<List<SpreadJob>>(manager, "_activeJobs").Clear();
        Field<Dictionary<SpreadJob, PersistedRace>>(manager, "_activeRequests").Clear();
        Field<List<PersistedRace>>(manager, "_startingRequests").Clear();
    }

    [Fact]
    public void Oversized_manual_batch_is_rejected_before_any_admission_or_write()
    {
        using var manager = new SpreadManager(new AppConfig(), _root);
        var writes = 0; SetStore(manager, (_, _) => writes++);
        Assert.Throws<ArgumentException>(() => manager.EnqueueRacesDetailed(Enumerable.Range(0, SpreadManager.MaxManualRaceBatch + 1)
            .Select(index => Request($"Release.{index}")).ToArray()));
        Assert.Empty(manager.QueuedRaces); Assert.Equal(0, writes);
    }

    [Fact]
    public void Batch_membership_edit_saves_once_and_rechecks_request_identity_and_busy_state()
    {
        using var manager = new SpreadManager(new AppConfig(), _root);
        var writes = new List<string>(); SetStore(manager, (_, text) => writes.Add(text));
        var queue = Field<List<PersistedRace>>(manager, "_raceQueue");
        var first = RaceControlPolicyTests.Request() with { RequestId = "first" };
        var second = first with { RequestId = "second", ReleaseName = "Second.Release" };
        var busy = first with { RequestId = "busy", ReleaseName = "Busy.Release" };
        var replacement = first with { RequestId = "replacement", ReleaseName = "Replaced.Release" };
        queue.AddRange([first, second, busy, replacement]);
        Field<HashSet<string>>(manager, "_controlRequestsInFlight").Add(busy.RequestId);
        var result = manager.EditQueuedRaceSites([first.RequestId, second.RequestId, first.RequestId, busy.RequestId, "stale"], "third", false);
        Assert.Equal(new BulkRaceSiteEditResult(2, 2, true), result);
        var saved = JsonSerializer.Deserialize<List<PersistedRace>>(Assert.Single(writes))!;
        Assert.All(saved.Where(item => item.RequestId is "first" or "second"), item => Assert.DoesNotContain("third", item.ServerIds));
        Assert.All(saved.Where(item => item.RequestId is "busy" or "replacement"), item => Assert.Contains("third", item.ServerIds));
        Assert.Equal(new BulkRaceSiteEditResult(0, 1, true), manager.EditQueuedRaceSites([first.RequestId], "src", false));
        Assert.Single(writes);
        Field<HashSet<string>>(manager, "_controlRequestsInFlight").Clear();
    }

    [Fact]
    public void Failed_batch_writes_keep_admission_and_membership_in_memory()
    {
        using var manager = new SpreadManager(new AppConfig(), _root);
        SetStore(manager, (_, _) => throw new IOException("synthetic write failure"));
        Assert.Equal(RaceStartOutcome.Queued, Assert.Single(manager.EnqueueRacesDetailed([Request("New.Release")])).Outcome);
        Assert.Single(manager.QueuedRaces);
        var request = RaceControlPolicyTests.Request();
        Field<List<PersistedRace>>(manager, "_raceQueue").Add(request);
        Assert.Equal(new BulkRaceSiteEditResult(1, 0, false), manager.EditQueuedRaceSites([request.RequestId], "third", false));
        Assert.DoesNotContain("third", manager.QueuedRaces.Single(item => item.RequestId == request.RequestId).ServerIds);
    }

    private static RaceBatchRequest Request(string release) => new("video", release, ["source", "destination", "source"], SpreadMode.Race);
    private void SetStore(SpreadManager manager, Action<string, string> write) => typeof(SpreadManager)
        .GetField("_queueStore", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(manager, new RaceQueueStore(Path.Combine(_root, "batch.json"), write));
    private static T Field<T>(object target, string name) => (T)typeof(SpreadManager)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
