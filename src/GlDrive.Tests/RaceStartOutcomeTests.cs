using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

[Collection("Config read-only mode")]
public sealed class RaceStartOutcomeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gldrive-start-result-" + Guid.NewGuid().ToString("N"));
    private readonly bool _wasReadOnly = ConfigManager.ReadOnly;
    private readonly DateTime _now = DateTime.UtcNow;

    public RaceStartOutcomeTests()
    {
        Directory.CreateDirectory(_directory);
        ConfigManager.ReadOnly = false;
    }

    public void Dispose()
    {
        ConfigManager.ReadOnly = _wasReadOnly;
        Directory.Delete(_directory, recursive: true);
    }

    private SpreadManager Manager(bool connected = true)
    {
        var config = new AppConfig { Servers = [new() { Id = "src" }, new() { Id = "dst" }] };
        // Admitted workers stop at pure skiplist evaluation before any FTP command.
        config.Spread.GlobalSkiplist.Add(new() { Pattern = "*", Action = SkiplistAction.Deny });
        var manager = new SpreadManager(config, _directory) { AdmissionUtcNow = () => _now };
        if (connected)
        {
            var pools = Field<Dictionary<string, FtpConnectionPool>>(manager, "_spreadPools");
            pools.Add("src", new FtpConnectionPool(null!));
            pools.Add("dst", new FtpConnectionPool(null!));
        }
        return manager;
    }

    private PersistedRace Request(string release) => new("video", release, ["src", "dst"], SpreadMode.Race,
        "src", "/section/" + release, _now);
    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private List<PersistedRace> Persisted() => JsonSerializer.Deserialize<List<PersistedRace>>(
        File.ReadAllText(Path.Combine(_directory, "race-queue.json")))!;
    private static RaceStartResult Start(SpreadManager manager, string release, string section = "video") =>
        manager.StartRaceDetailed(section, release, ["src", "dst"], SpreadMode.Race);

    [Fact]
    public void Suppression_reports_exact_remaining_time_and_reason_across_section_and_case()
    {
        using var manager = Manager();
        Field<ConcurrentDictionary<(string, string), (DateTime, string)>>(manager, "_recentlyDeadRaces")
            [("", "example.release")] = (_now.AddMinutes(7).AddSeconds(3), "release-not-found");
        var result = Start(manager, "EXAMPLE.RELEASE", "another-section");
        Assert.Equal(RaceStartOutcome.Suppressed, result.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(7) + TimeSpan.FromSeconds(3), result.SuppressedFor);
        Assert.Equal("release-not-found", result.Reason);
        Assert.Null(result.Job);
        Assert.Null(result.QueuePosition);
        Assert.Empty(Field<List<PersistedRace>>(manager, "_raceQueue"));
        Assert.Null(manager.StartRace("video", "Example.Release", ["src", "dst"], SpreadMode.Race));
    }

    [Fact]
    public void Suppression_expires_at_its_exact_boundary()
    {
        using var manager = Manager(connected: false);
        var dead = Field<ConcurrentDictionary<(string, string), (DateTime, string)>>(manager, "_recentlyDeadRaces");
        dead[("", "example.release")] = (_now, "release-not-found");
        var result = Start(manager, "Example.Release");
        Assert.Equal(RaceStartOutcome.Unavailable, result.Outcome);
        Assert.Null(result.SuppressedFor);
        Assert.Empty(dead);
        Assert.Empty(Field<List<PersistedRace>>(manager, "_startingRequests"));
        Assert.Empty(Persisted());
    }

    [Fact]
    public void Queued_position_matches_eligible_cleanup_fresh_resume_priority_and_duplicate_does_not_add_work()
    {
        using var manager = Manager();
        Field<List<PersistedRace>>(manager, "_startingRequests").Add(Request("Reserved"));
        var queue = Field<List<PersistedRace>>(manager, "_raceQueue");
        queue.Add(Request("Resume") with { ResumeCount = 1 });
        queue.Add(Request("Delayed") with { NextProbeAtUtc = _now.AddMinutes(5) });
        queue.Add(Request("Disconnected") with { ServerIds = ["offline"] });
        queue.Add(Request("Cleanup") with { CleanupOnly = true });
        queue.Add(Request("Fresh"));

        var result = Start(manager, "Next");
        Assert.Equal(RaceStartOutcome.Queued, result.Outcome);
        Assert.Equal(3, result.QueuePosition); // Cleanup, Fresh, Next; ordinary resumes come afterward.
        Assert.Null(result.Job);
        Assert.Equal(7, Persisted().Count); // Reserved admission plus six queued requests.
        var duplicate = Start(manager, "NEXT", "different-section");
        Assert.Equal(RaceStartOutcome.Duplicate, duplicate.Outcome);
        Assert.Equal(3, duplicate.QueuePosition);
        Assert.Equal(6, queue.Count);
        Assert.Null(manager.StartRace("video", "Next", ["src", "dst"], SpreadMode.Race));
    }

    [Fact]
    public void Expired_partial_cleanup_outranks_fresh_even_if_its_old_probe_was_deferred()
    {
        using var manager = Manager();
        Field<List<PersistedRace>>(manager, "_startingRequests").Add(Request("Reserved"));
        Field<List<PersistedRace>>(manager, "_raceQueue").Add(Request("Partial") with
        {
            NextProbeAtUtc = _now.AddHours(1), ResumeCount = 1,
            Partial = new() { FirstUploadAtUtc = _now.AddHours(-11), HasLandedPayload = true,
                Directories = [new() { ServerId = "dst", RemotePath = "/video/Partial" }] }
        });
        Assert.Equal(2, Start(manager, "Fresh").QueuePosition);
    }

    [Fact]
    public void Deferred_duplicate_and_disconnected_new_request_do_not_invent_eligible_positions()
    {
        using var manager = Manager(connected: false);
        Field<List<PersistedRace>>(manager, "_startingRequests").Add(Request("Reserved"));
        Field<List<PersistedRace>>(manager, "_raceQueue").Add(Request("Waiting") with
        { NextProbeAtUtc = _now.AddMinutes(1), WaitReason = "Waiting for source completion" });
        var duplicate = Start(manager, "Waiting");
        Assert.Equal(RaceStartOutcome.Duplicate, duplicate.Outcome);
        Assert.Null(duplicate.QueuePosition);
        Assert.Equal("Waiting for source completion", duplicate.Reason);
        var queued = Start(manager, "New");
        Assert.Equal(RaceStartOutcome.Queued, queued.Outcome);
        Assert.Null(queued.QueuePosition);
        Assert.Equal("Waiting for connected spread pools", queued.Reason);
    }

    [Fact]
    public async Task Reserved_admission_reports_already_active_and_reserves_capacity_before_callbacks_finish()
    {
        using var manager = Manager();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager._getMainPool = _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Admission was not released");
            throw new IOException("Controlled callback failure");
        };
        var starting = Task.Run(() => Record.Exception(() => Start(manager, "First")));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var same = Start(manager, "FIRST", "another-section");
            Assert.Equal(RaceStartOutcome.AlreadyActive, same.Outcome);
            Assert.Null(same.Job); // Reservation exists; the job has not been registered yet.
            Assert.Null(manager.StartRace("video", "First", ["src", "dst"], SpreadMode.Race));
            var next = Start(manager, "Second");
            Assert.Equal(RaceStartOutcome.Queued, next.Outcome);
            Assert.Equal(1, next.QueuePosition);
        }
        finally { release.Set(); }
        Assert.IsType<IOException>(await starting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Second", Assert.Single(Persisted()).ReleaseName);
    }

    [Fact]
    public async Task Active_result_identifies_the_job_while_legacy_duplicate_still_returns_null()
    {
        using var manager = Manager();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.JobStarted += _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Admission was not released");
        };
        var starting = Task.Run(() => Start(manager, "First"));
        RaceStartResult? existing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            existing = Start(manager, "FIRST");
            Assert.Equal(RaceStartOutcome.AlreadyActive, existing.Outcome);
            Assert.NotNull(existing.Job);
            Assert.Null(manager.StartRace("video", "First", ["src", "dst"], SpreadMode.Race));
        }
        finally { release.Set(); }
        var accepted = await starting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RaceStartOutcome.Started, accepted.Outcome);
        Assert.Same(existing!.Job, accepted.Job);
    }

    [Fact]
    public void Pool_disappearing_during_detailed_admission_reports_unavailable_and_clears_reservation()
    {
        using var manager = Manager();
        manager._getMainPool = _ => { manager.DisposePool("dst").GetAwaiter().GetResult(); return null; };
        var result = Start(manager, "Example.Release");
        Assert.Equal(RaceStartOutcome.Unavailable, result.Outcome);
        Assert.Contains("pools changed", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(manager.ActiveJobs);
        Assert.Empty(Field<List<PersistedRace>>(manager, "_startingRequests"));
        Assert.Empty(Persisted());
    }

    [Fact]
    public void Cancellation_during_admission_reports_unavailable_without_resurrecting_the_request()
    {
        using var manager = Manager();
        manager._getMainPool = _ => { manager.CancelRaceRequest("video", "Example.Release"); return null; };
        var result = Start(manager, "Example.Release");
        Assert.Equal(RaceStartOutcome.Unavailable, result.Outcome);
        Assert.Contains("cancelled", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(manager.ActiveJobs);
        Assert.Empty(Field<List<PersistedRace>>(manager, "_startingRequests"));
        Assert.Empty(Persisted());
    }

    [Fact]
    public void Legacy_unavailable_admission_keeps_its_exception_contract()
    {
        using var manager = Manager(connected: false);
        Assert.Throws<InvalidOperationException>(() => manager.StartRace("video", "Example", ["src", "dst"], SpreadMode.Race));
    }

    [Fact]
    public void Detailed_admission_preserves_sanitizing_and_participant_deduplication()
    {
        using var manager = Manager();
        Field<List<PersistedRace>>(manager, "_startingRequests").Add(Request("Reserved"));
        var result = manager.StartRaceDetailed("vi\r\ndeo", "Example\0.Release", ["src", "src", "dst"], SpreadMode.Race);
        Assert.Equal(RaceStartOutcome.Queued, result.Outcome);
        Assert.Equal("video", result.Section);
        Assert.Equal("Example.Release", result.ReleaseName);
        Assert.Equal(1, result.QueuePosition);
        var queued = Assert.Single(Field<List<PersistedRace>>(manager, "_raceQueue"));
        Assert.Equal(new[] { "src", "dst" }, queued.ServerIds);
        Assert.Equal(_now, queued.QueuedAtUtc);
    }

    [Fact]
    public void Disposed_engine_reports_unavailable_without_creating_work()
    {
        using var manager = Manager();
        manager.Dispose();
        Assert.Equal(RaceStartOutcome.Unavailable, Start(manager, "Example").Outcome);
        Assert.Null(manager.StartRace("video", "Example", ["src", "dst"], SpreadMode.Race));
        Assert.Empty(Field<List<PersistedRace>>(manager, "_startingRequests"));
    }
}
