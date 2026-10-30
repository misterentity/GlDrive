using System.IO;
using System.Reflection;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

[Collection("Config read-only mode")]
public sealed class SpreadManagerAdmissionHoldTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-manager-hold-" + Guid.NewGuid().ToString("N"));
    private readonly bool _readOnly = ConfigManager.ReadOnly;
    private DateTime _now = DateTime.UtcNow;

    public SpreadManagerAdmissionHoldTests() { Directory.CreateDirectory(_root); ConfigManager.ReadOnly = false; }
    public void Dispose() { ConfigManager.ReadOnly = _readOnly; Directory.Delete(_root, true); }

    private (SpreadManager Manager, FtpConnectionPool Pool, HeldGate Gate) Fixture(DateTime? deadline = null)
    {
        var gate = new HeldGate(deadline ?? _now.AddHours(1));
        var pool = new FtpConnectionPool(null!, 3, gate);
        typeof(FtpConnectionPool).GetProperty(nameof(FtpConnectionPool.IsConnected))!.SetValue(pool, true);
        var manager = new SpreadManager(new() { Servers = [
            new() { Id = "src", Connection = new() { Host = "source.fixture.test", Username = "fixture" } },
            new() { Id = "dst", Connection = new() { Host = "destination.fixture.test", Username = "fixture" } }] }, _root)
            { AdmissionUtcNow = () => _now };
        Pools(manager).Add("src", new FtpConnectionPool(null!, 3));
        Pools(manager).Add("dst", pool);
        return (manager, pool, gate);
    }

    private static Dictionary<string, FtpConnectionPool> Pools(SpreadManager manager) => Field<Dictionary<string, FtpConnectionPool>>(manager, "_spreadPools");
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(SpreadManager manager, string name, params object[] args) => typeof(SpreadManager)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, args);
    private static Task Reinit(SpreadManager manager) => (Task)Invoke(manager, "ReinitDeadPools", (object)new[] { "dst" })!;
    private PersistedRace Request(string release = "Held.Release-GRP") => new("TV", release, ["src", "dst"], SpreadMode.Race, "src", "/TV/" + release, _now);
    private int[] Eligible(SpreadManager manager) => ((IEnumerable<int>)Invoke(manager, "EligibleQueueIndicesNoLock", _now)!).ToArray();

    [Fact]
    public async Task Reinit_honors_site_hold_until_deadline_then_retries()
    {
        var (manager, pool, gate) = Fixture();
        using (manager)
        {
            await Reinit(manager);
            for (var i = 0; i < 20; i++) await Reinit(manager);
            Assert.Equal(1, gate.Attempts);
            Assert.Equal(0, pool.TotalCreated);
            _now = gate.RetryAtUtc;
            await Reinit(manager);
            Assert.Equal(2, gate.Attempts);
        }
    }

    [Fact]
    public async Task Held_exhausted_queue_stays_durable_without_starting_and_becomes_eligible_when_due()
    {
        var (manager, _, gate) = Fixture();
        using (manager)
        {
            await Reinit(manager);
            var request = Request();
            Field<List<PersistedRace>>(manager, "_raceQueue").Add(request);
            var starts = 0;
            manager.JobStarted += _ => starts++;
            Invoke(manager, "DequeueNextRace");
            Assert.Equal(0, starts);
            var saved = Assert.Single(manager.QueuedRaces);
            Assert.Equal(request.RequestId, saved.RequestId);
            Assert.Equal(request.QueuedAtUtc, saved.QueuedAtUtc);
            Assert.Equal(request.Resumes, saved.Resumes);
            Assert.Equal(request.KnownSourcePath, saved.KnownSourcePath);
            Assert.Empty(Eligible(manager));
            Invoke(manager, "PersistQueue");
            Assert.Equal(request.RequestId, Assert.Single(new RaceQueueStore(Path.Combine(_root, "race-queue.json")).Load()).RequestId);
            Assert.NotNull(Field<Timer?>(manager, "_queueWakeTimer"));
            _now = gate.RetryAtUtc;
            Assert.Equal([0], Eligible(manager));
        }
    }

    [Fact]
    public async Task New_request_during_known_hold_is_queued_even_with_spare_race_capacity()
    {
        var (manager, _, _) = Fixture();
        using (manager)
        {
            await Reinit(manager);
            var result = manager.StartRaceDetailed("TV", "Fresh.Release-GRP", ["src", "dst"], SpreadMode.Race);
            Assert.Equal(RaceStartOutcome.Queued, result.Outcome);
            Assert.Null(result.Job);
            Assert.Null(result.QueuePosition);
            Assert.Equal("Fresh.Release-GRP", Assert.Single(manager.QueuedRaces).ReleaseName);
        }
    }

    [Fact]
    public async Task Existing_session_remains_eligible_during_new_login_hold()
    {
        var (manager, pool, _) = Fixture();
        using (manager)
        {
            await Reinit(manager);
            Field<List<PersistedRace>>(manager, "_raceQueue").Add(Request());
            typeof(FtpConnectionPool).GetField("_created", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pool, 1);
            try { Assert.Equal([0], Eligible(manager)); }
            finally { typeof(FtpConnectionPool).GetField("_created", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pool, 0); }
        }
    }

    [Fact]
    public async Task Replaced_pool_does_not_inherit_old_hold_and_cleanup_still_gets_a_turn()
    {
        var (manager, pool, _) = Fixture();
        using (manager)
        {
            await Reinit(manager);
            var queue = Field<List<PersistedRace>>(manager, "_raceQueue");
            queue.Add(Request() with { CleanupOnly = true });
            Assert.Equal([0], Eligible(manager));
            queue[0] = Request();
            Pools(manager)["dst"] = new FtpConnectionPool(null!, 3);
            await pool.DisposeAsync();
            Assert.Equal([0], Eligible(manager));
        }
    }

    [Fact]
    public async Task Queue_timer_retries_held_request_without_a_new_announce_or_pool_event()
    {
        var (manager, _, gate) = Fixture(DateTime.UtcNow.AddSeconds(1));
        using (manager)
        {
            manager.AdmissionUtcNow = () => DateTime.UtcNow;
            await Reinit(manager);
            var request = Request();
            Field<List<PersistedRace>>(manager, "_raceQueue").Add(request);
            var entered = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Stop before job construction / FTP, after the real timer's admission.
            manager._getMainPool = _ =>
            {
                entered.TrySetResult(DateTime.UtcNow);
                throw new IOException("Controlled admission failure; preserve queued work");
            };
            Invoke(manager, "DequeueNextRace");
            Assert.False(entered.Task.IsCompleted);
            Assert.Equal(request.RequestId, Assert.Single(manager.QueuedRaces).RequestId);
            var admittedAt = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(admittedAt >= gate.RetryAtUtc);
            for (var i = 0; i < 100 && manager.QueuedRaces.Count == 0; i++) await Task.Delay(10);
            Assert.Equal(request.RequestId, Assert.Single(manager.QueuedRaces).RequestId);
        }
    }

    [Fact]
    public async Task Held_only_known_source_waits_even_with_two_healthy_peers_but_alternate_source_can_run()
    {
        var (manager, _, _) = Fixture();
        using (manager)
        {
            await Reinit(manager);
            Pools(manager).Add("third", new FtpConnectionPool(null!, 3));
            var request = Request() with { ServerIds = ["src", "dst", "third"], KnownSourceServerId = "dst" };
            var queue = Field<List<PersistedRace>>(manager, "_raceQueue");
            queue.Add(request);
            Assert.Empty(Eligible(manager));
            queue[0] = request with { SourceHints = [new("src", "TV", "/TV/Held.Release-GRP")] };
            Assert.Equal([0], Eligible(manager));
        }
    }

    [Fact]
    public async Task Successful_login_for_same_account_clears_hold_and_wakes_queue_early()
    {
        var (manager, _, gate) = Fixture();
        using (manager)
        {
            await Reinit(manager);
            var request = Request();
            Field<List<PersistedRace>>(manager, "_raceQueue").Add(request);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            manager._getMainPool = _ => { entered.TrySetResult(); throw new IOException("Controlled admission failure"); };
            var account = new FtpLoginAccount("destination.fixture.test", 21, "fixture");
            PublishLogin(new(account, false, null, false, false, false, _now));
            PublishLogin(new(account, true, null, false, false, true, _now));
            PublishLogin(new(new("unrelated.fixture.test", 21, "fixture"), true, null, false, false, false, _now));
            Assert.Empty(Eligible(manager));
            Assert.False(entered.Task.IsCompleted);
            PublishLogin(new(account, true, null, false, false, false, _now));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(DateTime.UtcNow < gate.RetryAtUtc);
            for (var i = 0; i < 100 && manager.QueuedRaces.Count == 0; i++) await Task.Delay(10);
            Assert.Equal(request.RequestId, Assert.Single(manager.QueuedRaces).RequestId);
        }
        Assert.DoesNotContain(LoginListeners()?.GetInvocationList() ?? [], handler => ReferenceEquals(handler.Target, manager));
    }

    [Fact]
    public async Task Duplicate_announce_with_healthy_source_wakes_held_queue()
    {
        var (manager, _, _) = Fixture();
        using (manager)
        {
            await Reinit(manager);
            Pools(manager).Add("third", new FtpConnectionPool(null!, 3));
            var config = Field<AppConfig>(manager, "_config");
            config.Servers.Add(new() { Id = "third" });
            config.Spread.MergeRaceAnnounces = true;
            var request = Request() with { ServerIds = ["src", "dst", "third"], KnownSourceServerId = "dst" };
            Field<List<PersistedRace>>(manager, "_raceQueue").Add(request);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            manager._getMainPool = _ => { entered.TrySetResult(); throw new IOException("Controlled admission failure"); };
            Assert.Empty(Eligible(manager));
            var result = manager.StartRaceDetailed("TV", request.ReleaseName, ["src", "dst", "third"], SpreadMode.Race,
                "src", "/TV/" + request.ReleaseName);
            Assert.Equal(RaceStartOutcome.Duplicate, result.Outcome);
            Assert.True(result.HintsMerged);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static Action<FtpLoginObservation>? LoginListeners() => (Action<FtpLoginObservation>?)typeof(GuardedFtpLogin)
        .GetField("LoginObserved", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
    private static void PublishLogin(FtpLoginObservation observation) => LoginListeners()?.Invoke(observation);

    private sealed class HeldGate(DateTime retryAtUtc) : IAccountLoginGate
    {
        public DateTime RetryAtUtc { get; } = retryAtUtc;
        public int Attempts { get; private set; }
        public int Held => 0;
        public int Limit => 3;
        public int Reserved => 0;
        public int PriorityLimit => 3;
        public int GeneralLimit => 3;
        public Task<bool> TryAcquireAsync(CancellationToken ct, TimeSpan? timeout = null, bool priority = false)
        {
            Attempts++;
            throw new FtpLoginHeldException("Fixture site-health deferral", false, RetryAtUtc, loginRateHold: true, siteHealthHold: true);
        }
        public void Release(bool priority = false) => throw new InvalidOperationException("No permit was acquired");
        public void TightenTo(int newLimit) { }
    }
}
