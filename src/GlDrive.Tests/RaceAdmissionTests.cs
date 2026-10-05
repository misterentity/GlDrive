using System.IO;
using System.Reflection;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

[Collection("Config read-only mode")]
public sealed class RaceAdmissionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gldrive-admission-" + Guid.NewGuid().ToString("N"));
    private readonly bool _wasReadOnly = ConfigManager.ReadOnly;
    private string QueuePath => Path.Combine(_directory, "race-queue.json");

    public RaceAdmissionTests()
    {
        Directory.CreateDirectory(_directory);
        ConfigManager.ReadOnly = false;
    }

    public void Dispose()
    {
        ConfigManager.ReadOnly = _wasReadOnly;
        Directory.Delete(_directory, recursive: true);
    }

    private static PersistedRace Request(string release) =>
        new("tv-hd", release, ["src", "dst"], SpreadMode.Race, "src", "/tv/" + release,
            DateTime.UtcNow, Resumes: 1);

    private SpreadManager CreateManager(int maximum = 1, params PersistedRace[] queued)
    {
        if (queued.Length > 0) new RaceQueueStore(QueuePath).Save(() => queued);
        var config = new AppConfig();
        config.Spread.MaxConcurrentRaces = maximum;
        // Any successfully admitted test worker terminates before FTP discovery.
        config.Spread.GlobalSkiplist.Add(new SkiplistRule { Pattern = "*", Action = SkiplistAction.Deny });
        config.Servers.Add(new ServerConfig { Id = "src", Name = "Source" });
        config.Servers.Add(new ServerConfig { Id = "dst", Name = "Destination" });
        var manager = new SpreadManager(config, _directory);
        var pools = (Dictionary<string, FtpConnectionPool>)typeof(SpreadManager)
            .GetField("_spreadPools", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        // These pools are never initialized or borrowed: controlled callbacks
        // pause/reject admission; admitted workers stop at the skiplist above.
        pools.Add("src", new FtpConnectionPool(null!));
        pools.Add("dst", new FtpConnectionPool(null!));
        return manager;
    }

    private List<PersistedRace> ReadQueue() =>
        JsonSerializer.Deserialize<List<PersistedRace>>(File.ReadAllText(QueuePath))!;

    private static void Invoke(SpreadManager manager, string method) =>
        typeof(SpreadManager).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, null);

    private static TaskCompletionSource Entered() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task DequeuedRequest_RemainsInConcurrentDurableSnapshotWhileStarting()
    {
        var request = Request("Held.Release");
        using var manager = CreateManager(1, request);
        using var release = new ManualResetEventSlim();
        var entered = Entered();
        manager._getMainPool = _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test admission was not released");
            throw new IOException("Controlled admission failure");
        };
        var starting = Task.Run(() => Invoke(manager, "DequeueNextRace"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Invoke(manager, "PersistQueue");
            var saved = Assert.Single(ReadQueue());
            Assert.Equal(request.ReleaseName, saved.ReleaseName);
            Assert.Equal(request.QueuedAtUtc, saved.QueuedAtUtc);
            Assert.Equal(request.Resumes, saved.Resumes);
            Assert.False(saved.WasActive); // Admission has not run a transfer yet.
        }
        finally { release.Set(); await starting.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task ConcurrentStart_CountsTheReservedSlotBeforeTheJobIsRegistered()
    {
        using var manager = CreateManager();
        using var release = new ManualResetEventSlim();
        var entered = Entered();
        var callbacks = 0;
        manager._getMainPool = _ =>
        {
            if (Interlocked.Increment(ref callbacks) == 1)
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test admission was not released");
            }
            throw new IOException("Controlled admission failure");
        };
        var starting = Task.Run(() => Record.Exception(() => manager.StartRace("tv-hd", "First", ["src", "dst"], SpreadMode.Race)));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(manager.StartRace("tv-hd", "Second", ["src", "dst"], SpreadMode.Race));
            Assert.Equal(1, callbacks);
            Assert.Equal(["First", "Second"], ReadQueue().Select(r => r.ReleaseName));
        }
        finally { release.Set(); await starting.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task ConcurrentStart_DeduplicatesAReservedReleaseAcrossSectionsAndCase()
    {
        using var manager = CreateManager(2);
        using var release = new ManualResetEventSlim();
        var entered = Entered();
        var callbacks = 0;
        manager._getMainPool = _ =>
        {
            if (Interlocked.Increment(ref callbacks) == 1)
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test admission was not released");
            }
            throw new IOException("Controlled admission failure");
        };
        var starting = Task.Run(() => Record.Exception(() => manager.StartRace("tv-hd", "First", ["src", "dst"], SpreadMode.Race)));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(manager.StartRace("-TV-", "FIRST", ["src", "dst"], SpreadMode.Race));
            Assert.Equal(1, callbacks);
            Assert.Equal("First", Assert.Single(ReadQueue()).ReleaseName);
        }
        finally { release.Set(); await starting.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public void Dequeue_StartFailureRetainsTheOriginalRequestWithoutRetryingInASpin()
    {
        var request = Request("Retry.Release");
        using var manager = CreateManager(1, request);
        var callbacks = 0;
        manager._getMainPool = _ =>
        {
            callbacks++;
            throw new IOException("Controlled admission failure");
        };
        Invoke(manager, "DequeueNextRace");
        var saved = Assert.Single(ReadQueue());
        Assert.Equal(request.ReleaseName, saved.ReleaseName);
        Assert.Equal(request.QueuedAtUtc, saved.QueuedAtUtc);
        Assert.Equal(request.Resumes, saved.Resumes);
        Assert.False(saved.WasActive);
        Assert.Equal(1, callbacks);
        Assert.Empty(manager.ActiveJobs);
    }

    [Fact]
    public void DirectStart_PoolDisappearsDuringAdmission_RejectsWithoutSchedulingSurpriseWork()
    {
        using var manager = CreateManager();
        var removed = false;
        manager._getMainPool = _ =>
        {
            if (!removed)
            {
                removed = true;
                manager.DisposePool("dst").GetAwaiter().GetResult();
            }
            return null;
        };
        Assert.Throws<InvalidOperationException>(() => manager.StartRace("tv-hd", "Reconnect.Release", ["src", "dst"], SpreadMode.Race));
        Assert.Empty(manager.ActiveJobs);
        Assert.Empty(ReadQueue());
    }

    [Fact]
    public void Dequeue_PoolDisappearsDuringAdmission_RetainsRequestAndDoesNotLaunchAJob()
    {
        using var manager = CreateManager(1, Request("Reconnect.Release"));
        var removed = false;
        manager._getMainPool = _ =>
        {
            if (!removed)
            {
                removed = true;
                manager.DisposePool("dst").GetAwaiter().GetResult();
            }
            return null;
        };
        Invoke(manager, "DequeueNextRace");
        Assert.Empty(manager.ActiveJobs);
        var saved = Assert.Single(ReadQueue());
        Assert.Equal("Reconnect.Release", saved.ReleaseName);
        Assert.False(saved.WasActive);
    }

    [Fact]
    public async Task Dispose_DuringAdmissionPreservesReservedAndQueuedRequests()
    {
        using var manager = CreateManager(1, Request("Held.Release"), Request("Waiting.Release"));
        using var release = new ManualResetEventSlim();
        var entered = Entered();
        manager._getMainPool = _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test admission was not released");
            return null;
        };
        var starting = Task.Run(() => Invoke(manager, "DequeueNextRace"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Invoke(manager, "PersistQueue");
            manager.Dispose();
        }
        finally { release.Set(); await starting.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(["Held.Release", "Waiting.Release"], ReadQueue().Select(r => r.ReleaseName));
        Assert.All(ReadQueue(), request => Assert.False(request.WasActive));
        Assert.Empty(manager.ActiveJobs);
    }

    [Fact]
    public async Task SuccessfulAdmission_AtomicallyBecomesActiveAndTerminalWorkersReleaseCapacity()
    {
        using var manager = CreateManager(1, Request("First.Release"), Request("Second.Release"));
        var snapshots = new System.Collections.Concurrent.ConcurrentQueue<List<PersistedRace>>();
        manager.JobStarted += _ => snapshots.Enqueue(ReadQueue());

        Invoke(manager, "DequeueNextRace");
        await WaitUntil(() => manager.History.Items.Count == 2);

        var captured = snapshots.ToArray();
        Assert.Equal(2, captured.Length);
        Assert.Equal(["First.Release", "Second.Release"], captured[0].Select(r => r.ReleaseName));
        Assert.True(captured[0][0].WasActive);
        Assert.False(captured[0][1].WasActive);
        var second = Assert.Single(captured[1]);
        Assert.Equal("Second.Release", second.ReleaseName);
        Assert.True(second.WasActive);
        Assert.All(manager.History.Items, r => Assert.Equal(SpreadJobState.Failed, r.Result)); // Deliberate skiplist denial.
        Assert.Empty(manager.ActiveJobs);
        Assert.Empty(ReadQueue());
    }

    [Fact]
    public async Task AcceptedJob_PoolDisappearsBeforeWorkerStarts_ReturnsToQueueWithoutHistoryOrSpin()
    {
        using var manager = CreateManager();
        var starts = 0;
        manager.JobStarted += _ =>
        {
            Interlocked.Increment(ref starts);
            manager.DisposePool("dst").GetAwaiter().GetResult();
        };

        var job = manager.StartRace("tv-hd", "Reconnect.Release", ["src", "dst"], SpreadMode.Race);
        Assert.NotNull(job);
        await WaitUntil(() => manager.ActiveJobs.Count == 0 && ReadQueue() is [{ WasActive: false }]);

        Assert.Equal("Reconnect.Release", Assert.Single(ReadQueue()).ReleaseName);
        Assert.Empty(manager.History.Items);
        Invoke(manager, "DequeueNextRace");
        Assert.Equal(1, starts);
        Assert.Empty(manager.ActiveJobs);
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}
