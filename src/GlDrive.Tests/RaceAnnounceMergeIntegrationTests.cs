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
public sealed class RaceAnnounceMergeIntegrationTests : IDisposable
{
    private const string Release = "Example.Show.S01E01.1080p.WEB.H264-GRP";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gldrive-announce-merge-" + Guid.NewGuid().ToString("N"));
    private readonly bool _readOnly = ConfigManager.ReadOnly;
    private readonly DateTime _now = DateTime.UtcNow;

    public RaceAnnounceMergeIntegrationTests() { Directory.CreateDirectory(_directory); ConfigManager.ReadOnly = false; }
    public void Dispose() { ConfigManager.ReadOnly = _readOnly; Directory.Delete(_directory, true); }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static ServerConfig Site(string id, bool sourceOnly = false, bool destinationOnly = false) => new()
    {
        Id = id, Name = id, Enabled = true,
        SpreadSite = new() { DownloadOnly = sourceOnly, NeverSource = destinationOnly, Sections = new() { ["tv-hd"] = "/incoming/tv" } }
    };
    private static AppConfig Config(bool merge = true) => new()
    {
        Servers = [Site("src", true), Site("dst", destinationOnly: true), Site("peer")],
        Spread = new() { MergeRaceAnnounces = merge, SizeAwareRaceQueue = true }
    };
    private SpreadManager Manager(AppConfig config)
    {
        var manager = new SpreadManager(config, _directory) { AdmissionUtcNow = () => _now };
        var pools = Field<Dictionary<string, FtpConnectionPool>>(manager, "_spreadPools");
        foreach (var site in config.Servers) pools.Add(site.Id, new FtpConnectionPool(null!));
        return manager;
    }
    private PersistedRace Request(string release = Release) => new("-TV-", release, ["dst", "peer"], SpreadMode.Race,
        "dst", "/received/" + release, _now.AddMinutes(-30));
    private RaceStartResult Announce(SpreadManager manager) => manager.StartRaceDetailed("tv-hd", Release,
        ["src", "dst"], SpreadMode.Race, "src", "/incoming/tv/" + Release);
    private void ReserveSlot(SpreadManager manager) => Field<List<PersistedRace>>(manager, "_startingRequests").Add(Request("Other.Release-GRP"));
    private List<PersistedRace> Saved() => JsonSerializer.Deserialize<List<PersistedRace>>(File.ReadAllText(Path.Combine(_directory, "race-queue.json")))!;

    [Fact]
    public void Destination_first_queue_merges_source_alias_preserves_wait_and_survives_restart()
    {
        var config = Config();
        var original = Request() with { NextProbeAtUtc = _now.AddMinutes(8), WaitAttempts = 4, WaitReason = "Waiting for reconnection", ResumeCount = 2 };
        using (var manager = Manager(config))
        {
            ReserveSlot(manager);
            Field<List<PersistedRace>>(manager, "_raceQueue").Add(original);
            var result = Announce(manager);
            Assert.Equal(RaceStartOutcome.Duplicate, result.Outcome);
            Assert.True(result.HintsMerged);
            Assert.Null(result.QueuePosition); // merging does not wake a parked request
            var merged = Assert.Single(manager.QueuedRaces);
            Assert.Equal("tv-hd", merged.Section);
            Assert.Equal("src", merged.KnownSourceServerId);
            Assert.Equal(new[] { "dst", "peer", "src" }, merged.ServerIds);
            Assert.Equal(original.QueuedAtUtc, merged.QueuedAtUtc);
            Assert.Equal(original.NextProbeAtUtc, merged.NextProbeAtUtc);
            Assert.Equal(original.WaitAttempts, merged.WaitAttempts);
            Assert.Equal(original.ResumeCount, merged.ResumeCount);
            Assert.Equal(original.RequestId, merged.RequestId);
            Assert.False(Announce(manager).HintsMerged);
            merged.SourceHints.Clear(); // public snapshots must be detached
            Assert.Single(manager.QueuedRaces.Single().SourceHints);
        }
        using var restored = new SpreadManager(config, _directory);
        var resumed = Assert.Single(restored.QueuedRaces, r => r.ReleaseName == Release);
        Assert.Equal("src", resumed.KnownSourceServerId);
        Assert.Equal("tv-hd", Assert.Single(resumed.SourceHints).Section);
        Assert.Equal(original.NextProbeAtUtc, resumed.NextProbeAtUtc);
        Assert.Equal(original.QueuedAtUtc, resumed.QueuedAtUtc);
        Assert.True(resumed.PredictedSeconds > 0);
    }

    [Fact]
    public void Disabled_merge_keeps_old_duplicate_behavior_and_route()
    {
        using var manager = Manager(Config(false));
        ReserveSlot(manager);
        var original = Request();
        Field<List<PersistedRace>>(manager, "_raceQueue").Add(original);
        var result = Announce(manager);
        Assert.Equal(RaceStartOutcome.Duplicate, result.Outcome);
        Assert.False(result.HintsMerged);
        Assert.Same(original, Field<List<PersistedRace>>(manager, "_raceQueue").Single());
        Assert.Null(manager.StartRace("tv-hd", Release, ["src", "dst"], SpreadMode.Race, "src", "/incoming/tv/" + Release));
    }

    [Fact]
    public async Task Merge_during_pool_callbacks_is_applied_to_registered_job_and_reservation_is_removed()
    {
        var config = Config();
        // Any admitted worker stops at pure name rules, before touching FTP.
        config.Spread.GlobalSkiplist.Add(new() { Pattern = "*", Action = SkiplistAction.Deny });
        using var manager = Manager(config);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager._getMainPool = _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test admission not released");
            return null;
        };
        var first = Task.Run(() => manager.StartRaceDetailed("-TV-", Release, ["dst", "peer"], SpreadMode.Race, "dst", "/received/" + Release));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var duplicate = Announce(manager);
            Assert.Equal(RaceStartOutcome.AlreadyActive, duplicate.Outcome);
            Assert.True(duplicate.HintsMerged);
            Assert.Null(duplicate.Job);
            var starting = Assert.Single(Field<List<PersistedRace>>(manager, "_startingRequests"));
            Assert.Equal("tv-hd", starting.Section);
            Assert.Contains("src", starting.ServerIds);
            Assert.Equal("src", Saved().Single().KnownSourceServerId);
        }
        finally { release.Set(); }
        var result = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RaceStartOutcome.Started, result.Outcome);
        Assert.Equal("tv-hd", result.Job!.Section);
        Assert.Equal("src", Field<string>(result.Job, "_knownSourceServerId"));
        Assert.Contains("src", result.Job.Sites.Keys);
        Assert.Empty(Field<List<PersistedRace>>(manager, "_startingRequests"));
    }

    [Fact]
    public void Active_merge_buffers_source_without_repinning_receiver_and_persists_latest_request()
    {
        var config = Config();
        // Both-role feeder is source-only when spliced outside this attempt's destinations.
        config.Servers[0].SpreadSite.DownloadOnly = false;
        using var manager = Manager(config);
        var job = Job(config, manager);
        var paths = new Dictionary<string, string> { ["dst"] = "/pinned/" + Release, ["peer"] = "/peer/" + Release };
        Set(job, "_sitePathsRef", paths);
        Field<List<SpreadJob>>(manager, "_activeJobs").Add(job);
        Field<Dictionary<SpreadJob, PersistedRace>>(manager, "_activeRequests").Add(job, Request());
        var result = Announce(manager);
        Assert.True(result.HintsMerged);
        Assert.Same(job, result.Job);
        Assert.Equal("-TV-", job.Section);
        Assert.Single(job.PendingSourceHints);
        Assert.False(Field<bool>(job, "_forceScan"));
        Assert.Equal(1, job.ApplyPendingSourceHints());
        Assert.Equal("/pinned/" + Release, paths["dst"]);
        Assert.Equal("/incoming/tv/" + Release, Field<ConcurrentDictionary<string, string>>(job, "_extraSourcePaths")["src"]);
        Assert.Empty(job.CapturePartialState().Directories);
        Assert.Empty(Field<Dictionary<string, HashSet<string>>>(job, "_fileOwnership"));
        Assert.False(Field<bool>(job, "_sourceScanSucceeded"));
        Assert.True(job.AwaitingFirstAnnounceScan());
        Assert.False(Field<bool>(job, "_forceScan"));
        Assert.Equal("src", Saved().Single().KnownSourceServerId);
        Assert.Equal("-TV-", Saved().Single().Section); // active paths/section remain pinned
        Assert.False(Announce(manager).HintsMerged);
        // A failed first scheduled attempt permits ordinary bounded retry, never
        // the indefinite incomplete-release retention granted by real inventory.
        job.NoteAnnounceScanAttempt("src");
        Assert.False(job.AwaitingFirstAnnounceScan());
        Assert.False(job.WaitingForIncompleteRelease);
        var deferred = Request().Defer(_now, "Waiting for announced source verification", job.WaitingForIncompleteRelease, false);
        Assert.False(deferred.KeepUntilAvailable);
        Assert.True(deferred.IsExpired(_now.AddDays(2)));
        typeof(SpreadJob).GetMethod("ProcessFiles", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(job, ["src", new List<SpreadFileInfo>(), 0L]);
        Assert.True(job.Sites["src"].IsSource);
    }

    [Fact]
    public void Source_hint_is_applied_during_discovery_without_claiming_inventory_or_ownership()
    {
        var config = Config();
        using var manager = Manager(config);
        var job = Job(config, manager);
        job.QueueSourceHints([new("src", "tv-hd", "/incoming/tv/" + Release)]);
        var paths = new Dictionary<string, string>();
        var sources = new HashSet<string>();
        Assert.Equal(1, job.ApplyPendingSourceHints(paths, sources));
        Assert.Contains("src", sources);
        Assert.Equal("/incoming/tv/" + Release, paths["src"]);
        Assert.False(Field<bool>(job, "_sourceScanSucceeded"));
        Assert.Empty(job.CapturePartialState().Directories);
        Assert.Empty(Field<Dictionary<string, HashSet<string>>>(job, "_fileOwnership"));
        Assert.True(job.AwaitingFirstAnnounceScan());
        job.Stop();
    }

    [Fact]
    public void Pending_active_hint_accepts_a_corrected_source_alias_without_repinning()
    {
        var config = Config();
        using var manager = Manager(config);
        var job = Job(config, manager);
        job.QueueSourceHints([new("src", "-TV-")]);
        Assert.Equal(0, job.ApplyPendingSourceHints());
        job.QueueSourceHints([new("src", "tv-hd")]);
        Assert.Equal(1, job.ApplyPendingSourceHints());
        Assert.Equal("/incoming/tv/" + Release, Field<ConcurrentDictionary<string, string>>(job, "_extraSourcePaths")["src"]);
        job.QueueSourceHints([new("src", "tv-hd", "/different/" + Release)]);
        Assert.Equal(0, job.ApplyPendingSourceHints());
        Assert.Equal("/incoming/tv/" + Release, Field<ConcurrentDictionary<string, string>>(job, "_extraSourcePaths")["src"]);
        job.Stop();
    }

    [Theory]
    [InlineData("never-source")]
    [InlineData("disabled")]
    [InlineData("denied")]
    [InlineData("no-pool")]
    [InlineData("pinned")]
    [InlineData("merge-off")]
    public void Active_splice_preserves_admission_pool_and_pinned_path_constraints(string blockedBy)
    {
        var config = Config();
        if (blockedBy == "never-source") config.Servers[0].SpreadSite.NeverSource = true;
        if (blockedBy == "disabled") config.Servers[0].Enabled = false;
        if (blockedBy == "denied") config.Servers[0].SpreadSite.Skiplist.Add(new() { Pattern = "*", Action = SkiplistAction.Deny });
        if (blockedBy == "merge-off") config.Spread.MergeRaceAnnounces = false;
        using var manager = Manager(config);
        var job = Job(config, manager);
        if (blockedBy == "no-pool") job.LivePoolResolver = _ => null;
        if (blockedBy == "pinned") Set(job, "_sitePathsRef", new Dictionary<string, string> { ["src"] = "/old/" + Release });
        job.QueueSourceHints([new("src", "tv-hd", "/incoming/tv/" + Release)]);
        Assert.Equal(0, job.ApplyPendingSourceHints());
        Assert.Empty(Field<ConcurrentDictionary<string, string>>(job, "_extraSourcePaths"));
        Assert.False(job.AwaitingFirstAnnounceScan());
        Assert.False(Field<bool>(job, "_forceScan"));
        Assert.Empty(job.CapturePartialState().Directories);
        job.Stop();
    }

    [Fact]
    public void Rejecting_an_old_snapshot_does_not_remove_a_concurrent_corrected_hint()
    {
        var config = Config();
        using var manager = Manager(config);
        var job = Job(config, manager);
        var denied = Site("src", true);
        denied.SpreadSite.Skiplist.Add(new() { Pattern = "*", Action = SkiplistAction.Deny });
        job.QueueSourceHints([new("src", "old-alias", "/old/" + Release)]);
        job.ServerConfigResolver = id =>
        {
            job.QueueSourceHints([new("src", "tv-hd", "/incoming/tv/" + Release)]);
            job.ServerConfigResolver = next => config.Servers.FirstOrDefault(s => s.Id == next);
            return denied;
        };
        Assert.Equal(0, job.ApplyPendingSourceHints());
        Assert.Equal("tv-hd", Assert.Single(job.PendingSourceHints).Section);
        Assert.Equal(1, job.ApplyPendingSourceHints());
        Assert.Equal("/incoming/tv/" + Release, Field<ConcurrentDictionary<string, string>>(job, "_extraSourcePaths")["src"]);
        job.Stop();
    }

    [Fact]
    public void Trusted_announced_bytes_update_prediction_without_changing_retry_or_lifetime()
    {
        using var manager = Manager(Config());
        ReserveSlot(manager);
        var original = Request() with { KnownSourceServerId = "src", ServerIds = ["src", "dst"],
            NextProbeAtUtc = _now.AddMinutes(4), WaitAttempts = 3, PredictedSeconds = 300, PredictionBasis = "fallback" };
        Field<List<PersistedRace>>(manager, "_raceQueue").Add(original);
        Field<SpeedTracker>(manager, "_speedTracker").RecordTransfer("src", "dst", 100 * 1024 * 1024, TimeSpan.FromSeconds(10));
        manager.ObserveSitebot(new() { Kind = SitebotEventKind.Complete, ServerId = "src", Release = Release,
            ObservedAtUtc = _now, Bytes = 1000L * 1024 * 1024, Files = 10 });
        var updated = Assert.Single(manager.QueuedRaces);
        Assert.Equal(1000L * 1024 * 1024, updated.ExpectedBytes);
        Assert.Equal(100, updated.PredictedSeconds);
        Assert.Equal(original.QueuedAtUtc, updated.QueuedAtUtc);
        Assert.Equal(original.NextProbeAtUtc, updated.NextProbeAtUtc);
        Assert.Equal(original.WaitAttempts, updated.WaitAttempts);
        Assert.False(updated.KeepUntilAvailable);
        Assert.Empty(manager.ActiveJobs);
        Assert.Equal(updated.ExpectedBytes, Saved().Single(r => r.ReleaseName == Release).ExpectedBytes);
    }

    private SpreadJob Job(AppConfig config, SpreadManager manager)
    {
        var pools = Field<Dictionary<string, FtpConnectionPool>>(manager, "_spreadPools");
        var job = new SpreadJob("-TV-", Release, SpreadMode.Race, config.Spread, new(), new(), new(), new(), new());
        job.ServerConfigResolver = id => config.Servers.FirstOrDefault(s => s.Id == id);
        job.LivePoolResolver = id => pools.GetValueOrDefault(id);
        return job;
    }
}
