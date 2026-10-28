using System.IO;
using System.Reflection;
using GlDrive.Config;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

[Collection("Config read-only mode")]
public sealed class RacePartialPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gldrive-partial-" + Guid.NewGuid().ToString("N"));
    private readonly bool _readOnly = ConfigManager.ReadOnly;
    private readonly DateTime _now = DateTime.UtcNow;
    public RacePartialPersistenceTests() { Directory.CreateDirectory(_directory); ConfigManager.ReadOnly = false; }
    public void Dispose() { ConfigManager.ReadOnly = _readOnly; Directory.Delete(_directory, true); }

    private PersistedRace Request(bool landed = true) => RaceLifetimePolicyTests.Request() with
    {
        QueuedAtUtc = _now,
        Partial = new RacePartialState
        {
            FirstUploadAtUtc = _now.AddHours(-1), HasLandedPayload = landed, ExpectedCount = 2,
            ExpectedFiles = new() { ["a.rar"] = 100, ["b.rar"] = 100 },
            Directories = [new()
            {
                ServerId = "dest", RemotePath = "/video/Release", AccountIdentity = "saved-digest",
                CreatedAtUtc = _now.AddHours(-1), AttemptedFiles = new() { ["a.rar"] = 100 },
                DeliveredFiles = landed ? new() { ["a.rar"] = 100 } : new()
            }]
        }
    };

    private static SpreadJob Job(PersistedRace request, RaceStopReason reason)
    {
        var job = new SpreadJob(request.Section, request.ReleaseName, request.Mode, new SpreadConfig(),
            new(), new(), new(), new SpeedTracker(), new SkiplistEvaluator());
        job.RestorePartial(request);
        job.Stop(reason);
        return job;
    }

    [Fact]
    public void State_roundtrip_keeps_provenance_inventory_deadline_and_distinct_budgets()
    {
        var store = new RaceQueueStore(Path.Combine(_directory, "state.json"));
        var request = Request() with { Resumes = 1, ResumeCount = 2, CleanupOnly = true, CleanupAttempts = 1, StopReason = "Stall" };
        Assert.True(store.Save(() => [request]));
        var loaded = Assert.Single(store.Load());
        Assert.Equal(request.FirstUploadAtUtc, loaded.FirstUploadAtUtc);
        Assert.Equal(1, loaded.Resumes);
        Assert.Equal(2, loaded.ResumeCount);
        Assert.Equal(100, Assert.Single(loaded.Partial!.Directories).DeliveredFiles["a.rar"]);
        Assert.Equal("saved-digest", loaded.Partial.Directories[0].AccountIdentity);
        Assert.Equal(2, loaded.Partial.ExpectedCount);
        Assert.Equal("Stall", loaded.StopReason);
    }

    [Fact]
    public void Snapshot_is_detached_from_later_upload_mutation()
    {
        var original = Request();
        using var job = Job(original, RaceStopReason.Stall);
        var snapshot = job.CapturePartialState();
        snapshot.Directories[0].DeliveredFiles["other.rar"] = 10;
        snapshot.ExpectedFiles["other.rar"] = 10;
        Assert.DoesNotContain("other.rar", job.CapturePartialState().Directories[0].DeliveredFiles.Keys);
        Assert.DoesNotContain("other.rar", original.Partial!.ExpectedFiles.Keys);
    }

    [Fact]
    public void Crash_budget_and_staleness_never_drop_owned_partial()
    {
        var request = Request() with { WasActive = true, Resumes = RaceQueueStore.MaxResumes, QueuedAtUtc = _now.AddDays(-3) };
        var restored = Assert.Single(RaceQueueStore.Restore([request], _now).Races);
        Assert.True(restored.CleanupOnly);
        Assert.Equal(request.FirstUploadAtUtc, restored.FirstUploadAtUtc);
        Assert.Single(restored.Partial!.Directories);
    }

    [Fact]
    public void Stall_resumes_landed_data_but_cleans_empty_attempt_and_spent_budget()
    {
        using var manager = new SpreadManager(new AppConfig(), _directory);
        var request = Request();
        using var job = Job(request, RaceStopReason.Stall);
        var resumed = manager.NextRequest(request, job, _now, false, true)!;
        Assert.Equal(1, resumed.ResumeCount);
        Assert.Equal(0, resumed.Resumes);
        Assert.False(resumed.CleanupOnly);
        Assert.Equal(_now.AddSeconds(30), resumed.NextProbeAtUtc);
        Assert.True(manager.NextRequest(request with { ResumeCount = 3 }, job, _now, false, true)!.CleanupOnly);
        var empty = Request(false);
        using var emptyJob = Job(empty, RaceStopReason.Stall);
        Assert.True(manager.NextRequest(empty, emptyJob, _now, false, true)!.CleanupOnly);
    }

    [Fact]
    public void Source_gone_and_expired_deadline_cleanup_user_stop_waits_until_deadline()
    {
        using var manager = new SpreadManager(new AppConfig(), _directory);
        var request = Request();
        using var gone = Job(request, RaceStopReason.SourceGone);
        Assert.True(manager.NextRequest(request, gone, _now, false, true)!.CleanupOnly);
        using var stopped = Job(request, RaceStopReason.UserStop);
        var cleanup = manager.NextRequest(request, stopped, _now, false, false)!;
        Assert.True(cleanup.CleanupOnly);
        Assert.Equal(request.FirstUploadAtUtc!.Value.AddHours(10), cleanup.NextProbeAtUtc);
        using var stalled = Job(request, RaceStopReason.Stall);
        Assert.True(manager.NextRequest(request, stalled, _now.AddHours(10), false, true)!.CleanupOnly);
    }

    [Fact]
    public void Failed_store_reports_failure_instead_of_claiming_durable_ownership()
    {
        var path = Path.Combine(_directory, "blocked.json");
        Directory.CreateDirectory(path);
        Assert.False(new RaceQueueStore(path).Save(() => [Request()]));
    }

    [Fact]
    public void Cancelling_admission_keeps_durable_cleanup_provenance()
    {
        using var manager = new SpreadManager(new AppConfig(), _directory);
        var request = Request() with { ResumeCount = 1 };
        Field<List<PersistedRace>>(manager, "_startingRequests").Add(request);
        Assert.True(manager.CancelRaceRequest(request.Section, request.ReleaseName));
        Assert.Empty(Field<List<PersistedRace>>(manager, "_startingRequests"));
        var saved = Assert.Single(new RaceQueueStore(Path.Combine(_directory, "race-queue.json")).Load());
        Assert.True(saved.CleanupOnly);
        Assert.Equal(request.FirstUploadAtUtc, saved.FirstUploadAtUtc);
        Assert.Single(saved.Partial!.Directories);
    }

    [Fact]
    public void Cancelling_active_race_saves_cleanup_before_worker_finally()
    {
        using var manager = new SpreadManager(new AppConfig(), _directory);
        var request = Request();
        using var job = Job(request, RaceStopReason.Stall);
        Field<List<SpreadJob>>(manager, "_activeJobs").Add(job);
        Field<Dictionary<SpreadJob, PersistedRace>>(manager, "_activeRequests")[job] = request;
        Assert.True(manager.CancelRaceRequest(request.Section, request.ReleaseName));
        var saved = Assert.Single(new RaceQueueStore(Path.Combine(_directory, "race-queue.json")).Load());
        Assert.True(saved.CleanupOnly);
        Assert.Single(saved.Partial!.Directories);
        Assert.Empty(Field<Dictionary<SpreadJob, PersistedRace>>(manager, "_activeRequests"));
        Field<List<SpreadJob>>(manager, "_activeJobs").Remove(job);
    }

    private static T Field<T>(object value, string name) => (T)value.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
}
