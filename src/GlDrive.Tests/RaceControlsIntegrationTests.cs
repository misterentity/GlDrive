using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

[Collection("Config read-only mode")]
public sealed class RaceControlsIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "race-controls-" + Guid.NewGuid().ToString("N"));
    private readonly bool _readOnly = ConfigManager.ReadOnly;
    public RaceControlsIntegrationTests() { Directory.CreateDirectory(_directory); ConfigManager.ReadOnly = false; }
    public void Dispose() { ConfigManager.ReadOnly = _readOnly; Directory.Delete(_directory, true); }
    private SpreadManager Manager() => new(new AppConfig { Servers = [RaceControlPolicyTests.Destination] }, _directory);
    private static T Field<T>(object instance, string name) => (T)instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static SpreadJob Job() => new("video", "Example.Release", SpreadMode.Race, new(), new(), new(), new(), new SpeedTracker(), new SkiplistEvaluator());

    [Fact]
    public void CleanupRequiresExplicitConfirmationAndPersistsTheExistingPartial()
    {
        using var manager = Manager(); var request = RaceControlPolicyTests.Request(true);
        Field<List<PersistedRace>>(manager, "_raceQueue").Add(request);
        Assert.Equal("confirmation-required", manager.RequestOwnedCleanupNow("video", "Example.Release", false).Code);
        Assert.False(Assert.Single(manager.QueuedRaces).CleanupOnly);
        Assert.True(manager.RequestOwnedCleanupNow("video", "Example.Release", true).Accepted);
        var saved = Assert.Single(new RaceQueueStore(Path.Combine(_directory, "race-queue.json")).Load());
        Assert.True(saved.CleanupOnly); Assert.Equal(request.Partial!.Directories[0].AccountIdentity, saved.Partial!.Directories[0].AccountIdentity);
        Assert.Equal(request.CleanupAttempts, saved.CleanupAttempts);
    }

    [Fact]
    public void ActiveCleanupAndMembershipChangesWaitForTransferLifetimeToEnd()
    {
        using var manager = Manager(); using var job = Job();
        Field<List<SpreadJob>>(manager, "_activeJobs").Add(job);
        Assert.Equal("stop-and-wait", manager.RequestOwnedCleanupNow("video", "Example.Release", true).Code);
        Assert.Equal("active-membership-unsupported", manager.EditQueuedRaceSite("video", "Example.Release", "dst", false).Code);
        Field<List<SpreadJob>>(manager, "_activeJobs").Remove(job);
    }

    [Fact]
    public void FailedCleanupSaveRestoresOriginalQueueAndDoesNotLeaveAdmissionBlocked()
    {
        using var manager = Manager(); var request = RaceControlPolicyTests.Request(true);
        Field<List<PersistedRace>>(manager, "_raceQueue").Add(request);
        var queuePath = Path.Combine(_directory, "race-queue.json");
        if (File.Exists(queuePath)) File.Delete(queuePath);
        Directory.CreateDirectory(queuePath);
        Assert.Equal("save-failed", manager.RequestOwnedCleanupNow("video", "Example.Release", true).Code);
        var restored = Assert.Single(manager.QueuedRaces);
        Assert.False(restored.CleanupOnly); Assert.Equal(request.NextProbeAtUtc, restored.NextProbeAtUtc);
        Assert.Empty(Field<HashSet<string>>(manager, "_controlRequestsInFlight"));
    }

    [Fact]
    public void FailedSaveRollbackFindsRepredictedRequestAndPreservesConcurrentHints()
    {
        using var manager = Manager(); var original = RaceControlPolicyTests.Request(true);
        var changed = RaceLifetimePolicy.CleanupRequest(original, DateTime.UtcNow, "requested") with
        {
            PredictedSeconds = 123,
            SourceHints = [new("later-source", "video", "/video/Example.Release")]
        };
        Field<List<PersistedRace>>(manager, "_raceQueue").Add(changed);
        Field<HashSet<string>>(manager, "_controlRequestsInFlight").Add(original.RequestId);
        manager.RestoreFailedControl(original, cleanup: true);
        var restored = Assert.Single(manager.QueuedRaces);
        Assert.False(restored.CleanupOnly); Assert.Equal(original.NextProbeAtUtc, restored.NextProbeAtUtc);
        Assert.Equal(123, restored.PredictedSeconds); Assert.Equal("later-source", Assert.Single(restored.SourceHints).ServerId);
        Assert.Empty(Field<HashSet<string>>(manager, "_controlRequestsInFlight"));
    }

    [Fact]
    public void CancellationCannotBeUndoneByConcurrentControlRollback()
    {
        using var manager = Manager(); var original = RaceControlPolicyTests.Request(true);
        Field<List<PersistedRace>>(manager, "_raceQueue").Add(original);
        Field<HashSet<string>>(manager, "_controlRequestsInFlight").Add(original.RequestId);
        Assert.False(manager.CancelRaceRequest(original.Section, original.ReleaseName));
        manager.RestoreFailedControl(original, cleanup: true);
        Assert.True(manager.CancelRaceRequest(original.Section, original.ReleaseName));
        Assert.True(Assert.Single(manager.QueuedRaces).CleanupOnly);
    }

    [Fact]
    public void ResetWaitsForScanBoundaryAndRetainsOwnershipAndPoolBackoff()
    {
        using var job = Job(); var scan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(SpreadJob).GetField("_backgroundScan", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(job, scan.Task);
        var failures = Field<Dictionary<(string, string, string), int>>(job, "_failureCounts"); failures[("a.rar", "src", "dst")] = 4;
        var retries = Field<Dictionary<string, DateTime>>(job, "_destRetryAt"); retries["dst"] = DateTime.MaxValue;
        var poolBackoff = Field<Dictionary<string, DateTime>>(job, "_poolRetryAt"); poolBackoff["dst"] = DateTime.UtcNow.AddHours(1);
        job.RestorePartial(RaceControlPolicyTests.Request(true));
        Assert.True(job.RequestReset()); Assert.False(job.ApplyPendingReset()); Assert.Single(failures);
        scan.SetResult(); Assert.True(job.ApplyPendingReset());
        Assert.Empty(failures); Assert.Empty(retries); Assert.Single(poolBackoff);
        Assert.Single(job.CapturePartialState().Directories); Assert.False(job.ApplyPendingReset());
        job.Stop(); Assert.False(job.RequestReset());
    }

    [Fact]
    public void ReRaceReturnsTheRealAdmissionSuppressionWithoutClearingIt()
    {
        using var manager = Manager();
        manager.History.Add(new() { Id = "old", Section = "video", ReleaseName = "Example.Release", Mode = SpreadMode.Race });
        var now = DateTime.UtcNow; manager.AdmissionUtcNow = () => now;
        Field<ConcurrentDictionary<(string, string), (DateTime, string)>>(manager, "_recentlyDeadRaces")
            [("", "example.release")] = (now.AddMinutes(5), "release-not-found");
        var result = manager.ReRace("old", ["src", "dst"]);
        Assert.Equal(RaceStartOutcome.Suppressed, result.Outcome); Assert.Equal(TimeSpan.FromMinutes(5), result.SuppressedFor);
        Assert.Equal(RaceStartOutcome.Unavailable, manager.ReRace("unknown").Outcome);
    }

    [Fact]
    public void NukeHoldsBlockAdmissionCleanupAndResetWithoutLosingOwnership()
    {
        using var manager = Manager(); manager.IsReleaseNuked = (_, _) => true;
        var request = RaceControlPolicyTests.Request(true);
        Field<List<PersistedRace>>(manager, "_raceQueue").Add(request);
        Assert.Equal("nuke-held", manager.RequestOwnedCleanupNow("video", "Example.Release", true).Code);
        var start = manager.StartRaceDetailed("video", "Example.Release", ["src", "dst"], SpreadMode.Race);
        Assert.Equal(RaceStartOutcome.Unavailable, start.Outcome);
        Assert.Single(Assert.Single(manager.QueuedRaces).Partial!.Directories);
    }

    [Fact]
    public void NukeHeldCleanupCannotBeSelectedByQueueScheduler()
    {
        using var manager = Manager(); manager.IsReleaseNuked = (_, _) => true;
        var request = RaceControlPolicyTests.Request(true) with { CleanupOnly = true, NextProbeAtUtc = DateTime.UtcNow.AddMinutes(-1) };
        Field<List<PersistedRace>>(manager, "_raceQueue").Add(request);
        var eligible = (IEnumerable<int>)typeof(SpreadManager)
            .GetMethod("EligibleQueueIndicesNoLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, [DateTime.UtcNow])!;
        Assert.Empty(eligible);
        manager.IsReleaseNuked = (_, _) => false;
        var released = (IEnumerable<int>)typeof(SpreadManager)
            .GetMethod("EligibleQueueIndicesNoLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, [DateTime.UtcNow])!;
        Assert.Equal(0, Assert.Single(released));
    }

    [Fact]
    public async Task NukeHoldPreservesPartialWithoutBorrowingAnyFtpConnection()
    {
        using var job = Job();
        job.RestorePartial(RaceControlPolicyTests.Request(true) with { CleanupOnly = true });
        job.IsReleaseNuked = (_, _) => true;
        await job.RunAsync();
        Assert.Single(job.CapturePartialState().Directories);
        Assert.Equal(SpreadJobState.Stopped, job.State);
    }

    [Fact]
    public void FailedNukeLookupFailsClosed()
    {
        using var manager = Manager(); manager.IsReleaseNuked = (_, _) => throw new IOException("unavailable");
        Assert.True(manager.IsNuked("dst", "Example.Release"));
        using var job = Job(); job.IsReleaseNuked = (_, _) => throw new IOException("unavailable");
        Assert.True(job.KnownNuked("dst"));
    }
}
