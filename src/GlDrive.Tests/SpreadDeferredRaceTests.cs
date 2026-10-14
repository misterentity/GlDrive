using System.IO;
using System.Reflection;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

[Collection("Config read-only mode")]
public sealed class SpreadDeferredRaceTests
{
    private static PersistedRace Request(DateTime now) =>
        new("TV", "Slow.Release", ["source", "destination"], SpreadMode.Race,
            "source", "/TV/Slow.Release", now);

    [Fact]
    public void Incomplete_release_wait_is_durable_bounded_and_does_not_consume_crash_resumes()
    {
        var now = DateTime.UtcNow;
        var request = Request(now) with { Resumes = RaceQueueStore.MaxResumes };
        for (var attempt = 0; attempt < 12; attempt++)
        {
            request = request.Defer(now, "Waiting for remaining release files", incomplete: true, madeProgress: false);
            Assert.InRange((request.NextProbeAtUtc!.Value - now).TotalSeconds, 30, 300);
            Assert.False(request.IsDue(now));
            Assert.True(request.IsDue(request.NextProbeAtUtc.Value));
            var restored = RaceQueueStore.Restore([request], now.AddDays(2));
            request = Assert.Single(restored.Races);
            Assert.Equal(RaceQueueStore.MaxResumes, request.Resumes);
            Assert.Equal(0, restored.DroppedResumeCap);
            Assert.Equal(0, restored.DroppedStale);
            Assert.Equal(0, restored.Resumed);
        }
        var progress = request.Defer(now, "Waiting for remaining release files", true, true);
        Assert.Equal(TimeSpan.FromSeconds(30), progress.NextProbeAtUtc - now);
        var restartedDuringProbe = RaceQueueStore.Restore([progress with { WasActive = true }], now.AddDays(2));
        Assert.Equal(RaceQueueStore.MaxResumes, Assert.Single(restartedDuringProbe.Races).Resumes);
        Assert.Equal(0, restartedDuringProbe.DroppedResumeCap);
        Assert.Equal(0, restartedDuringProbe.Resumed);
    }

    [Fact]
    public void Reconnection_wait_does_not_turn_an_ordinary_stale_request_immortal()
    {
        var now = DateTime.UtcNow;
        var request = Request(now).Defer(now, "Waiting for server reconnection", false, false);
        Assert.True(request.IsExpired(now.AddDays(2)));
        Assert.Empty(RaceQueueStore.Restore([request], now.AddDays(2)).Races);
    }

    [Fact]
    public void Stop_overrides_waiting_before_manager_can_requeue()
    {
        using var job = new SpreadJob("TV", "Slow.Release", SpreadMode.Race,
            new SpreadConfig(), new(), new(), new(), new SpeedTracker(), new SkiplistEvaluator());
        typeof(SpreadJob).GetProperty(nameof(SpreadJob.State))!.SetValue(job, SpreadJobState.Waiting);
        job.Stop();
        Assert.Equal(SpreadJobState.Stopped, job.State);
    }

    [Fact]
    public void Cancel_wait_removes_persisted_request_and_admission_reservation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gldrive-deferred-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var oldReadOnly = ConfigManager.ReadOnly;
        ConfigManager.ReadOnly = false;
        try
        {
            var store = new RaceQueueStore(Path.Combine(dir, "race-queue.json"));
            var request = Request(DateTime.UtcNow).Defer(DateTime.UtcNow, "Waiting for remaining release files", true, false);
            store.Save(() => [request]);
            using (var manager = new SpreadManager(new AppConfig(), dir))
            {
                Assert.Single(manager.QueuedRaces);
                Assert.True(manager.CancelRaceRequest(request.Section, request.ReleaseName));
                Assert.Empty(manager.QueuedRaces);
                Assert.Empty(store.Load());

                // Model the timer having selected a request while main-pool lookup
                // is in progress. Cancellation must invalidate that reservation too.
                Field<List<PersistedRace>>(manager, "_startingRequests").Add(request);
                Assert.True(manager.CancelRaceRequest(request.Section, request.ReleaseName));
                Assert.Empty(Field<List<PersistedRace>>(manager, "_startingRequests"));
                Assert.Empty(store.Load());

                using var active = new SpreadJob(request.Section, request.ReleaseName, SpreadMode.Race,
                    new SpreadConfig(), new(), new(), new(), new SpeedTracker(), new SkiplistEvaluator());
                Field<List<SpreadJob>>(manager, "_activeJobs").Add(active);
                Field<Dictionary<SpreadJob, PersistedRace>>(manager, "_activeRequests")[active] = request;
                Assert.True(manager.CancelRaceRequest(request.Section, request.ReleaseName));
                Assert.Equal(SpreadJobState.Stopped, active.State);
                // Even shutdown before the worker's finally must not persist a
                // cancelled probe as WasActive and resurrect it on restart.
                Assert.Empty(store.Load());
                Field<List<SpreadJob>>(manager, "_activeJobs").Remove(active);
            }
            using var restarted = new SpreadManager(new AppConfig(), dir);
            Assert.Empty(restarted.QueuedRaces);
        }
        finally
        {
            ConfigManager.ReadOnly = oldReadOnly;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Future_wait_does_not_block_a_ready_release_behind_it()
    {
        var now = DateTime.UtcNow;
        WithManager([
            Request(now).Defer(now, "Waiting for remaining release files", true, false),
            Request(now) with { ReleaseName = "Ready.Release" }
        ], manager =>
        {
            string? admitted = null;
            manager._getMainPool = _ =>
            {
                admitted = Assert.Single(Field<List<PersistedRace>>(manager, "_startingRequests")).ReleaseName;
                throw new InvalidOperationException("Stop fixture before FTP creation");
            };
            InvokeDequeue(manager);
            Assert.Equal("Ready.Release", admitted);
            Assert.Contains(manager.QueuedRaces, r => r.ReleaseName == "Slow.Release" && r.NextProbeAtUtc > now);
            Assert.Empty(manager.ActiveJobs);
        });
    }

    [Fact]
    public async Task Due_timer_wakes_a_parked_request_without_a_new_announce()
    {
        var now = DateTime.UtcNow;
        var dir = Path.Combine(Path.GetTempPath(), "gldrive-race-wakeup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var oldReadOnly = ConfigManager.ReadOnly;
        ConfigManager.ReadOnly = false;
        try
        {
            var store = new RaceQueueStore(Path.Combine(dir, "race-queue.json"));
            store.Save(() => [Request(now).Defer(now, "Waiting", true, false) with { NextProbeAtUtc = now.AddMilliseconds(500) }]);
            using var manager = new SpreadManager(TestConfig(), dir);
            InstallDisconnectedFixturePools(manager);
            var awakened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            manager._getMainPool = _ =>
            {
                awakened.TrySetResult();
                throw new InvalidOperationException("Stop fixture before FTP creation");
            };
            InvokeDequeue(manager);
            await awakened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            ConfigManager.ReadOnly = oldReadOnly;
            Directory.Delete(dir, true);
        }
    }

    private static void WithManager(List<PersistedRace> requests, Action<SpreadManager> verify)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gldrive-race-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var oldReadOnly = ConfigManager.ReadOnly;
        ConfigManager.ReadOnly = false;
        try
        {
            new RaceQueueStore(Path.Combine(dir, "race-queue.json")).Save(() => requests);
            using var manager = new SpreadManager(TestConfig(), dir);
            InstallDisconnectedFixturePools(manager);
            verify(manager);
        }
        finally
        {
            ConfigManager.ReadOnly = oldReadOnly;
            Directory.Delete(dir, true);
        }
    }

    private static AppConfig TestConfig() => new()
    {
        Servers = [new() { Id = "source" }, new() { Id = "destination" }]
    };

    private static void InstallDisconnectedFixturePools(SpreadManager manager)
    {
        var pools = Field<Dictionary<string, FtpConnectionPool>>(manager, "_spreadPools");
        pools["source"] = new(null!, 1);
        pools["destination"] = new(null!, 1);
    }

    private static void InvokeDequeue(SpreadManager manager) => typeof(SpreadManager)
        .GetMethod("DequeueNextRace", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, null);

    private static T Field<T>(object value, string name) =>
        (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
}
