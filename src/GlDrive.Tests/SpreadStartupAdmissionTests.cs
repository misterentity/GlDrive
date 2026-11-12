using System.IO;
using System.Reflection;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using GlDrive.Tls;
using Xunit;

namespace GlDrive.Tests;

[Collection("Config read-only mode")]
public sealed class SpreadStartupAdmissionTests
{
    [Theory]
    [InlineData("before factory")]
    [InlineData("future hold")]
    [InlineData("expired hold")]
    [InlineData("initializing")]
    public void Restored_known_source_waits_without_dispatch_or_retry_spending_until_its_pool_exists(string phase)
    {
        using var f = new Fixture();
        f.SetPhase(phase);
        for (var i = 0; i < 20; i++) f.Drain();

        Assert.Equal(0, f.Admissions);
        Assert.Empty(f.Manager.ActiveJobs);
        Assert.Empty(f.Manager.History.Items);
        f.AssertOriginalRequest();

        // The real initialization completion calls this same queue drain after registering
        // its pool. No new announce, deadline reset or replacement request is necessary.
        f.RegisterSourcePool();
        f.Drain();
        Assert.Equal(1, f.Admissions);
        f.AssertOriginalRequest();
    }

    [Theory]
    [InlineData("before factory")]
    [InlineData("future hold")]
    [InlineData("expired hold")]
    public void Fresh_known_source_request_is_queued_during_startup_even_with_two_ready_peers(string phase)
    {
        using var f = new Fixture(restore: false);
        f.SetPhase(phase);
        var result = f.Manager.StartRaceDetailed("TV", f.Request.ReleaseName, f.Request.ServerIds,
            SpreadMode.Race, "source", f.Request.KnownSourcePath);

        Assert.Equal(RaceStartOutcome.Queued, result.Outcome);
        Assert.Null(result.Job);
        Assert.Null(result.QueuePosition);
        Assert.Equal(0, f.Admissions);
        var queued = Assert.Single(f.Manager.QueuedRaces);
        Assert.Equal(0, queued.SourceProbeRetries);
        Assert.Equal("source", queued.KnownSourceServerId);
    }

    [Fact]
    public void A_healthy_announced_alternate_source_can_dispatch_while_original_source_is_pending()
    {
        using var f = new Fixture(alternateSource: true);
        f.SetPhase("future hold");
        f.Drain();
        Assert.Equal(1, f.Admissions);
        f.AssertOriginalRequest();
    }

    [Fact]
    public void A_pending_destination_does_not_block_two_ready_participants_with_a_known_source()
    {
        using var f = new Fixture(knownSource: "peer-a");
        f.SetPhase("future hold");
        f.Drain();
        Assert.Equal(1, f.Admissions);
        f.AssertOriginalRequest();
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("removed")]
    [InlineData("automatic mount disabled")]
    public void Retired_startup_source_does_not_pin_the_remaining_participants(string change)
    {
        using var f = new Fixture();
        if (change == "disabled") f.Source.Enabled = false;
        else if (change == "removed") f.Config.Servers.Remove(f.Source);
        else f.Source.Mount.AutoMountOnStart = false;
        f.Drain();
        Assert.Equal(1, f.Admissions);
    }

    [Fact]
    public async Task Explicit_pool_disposal_clears_factory_hold_and_startup_intent()
    {
        using var f = new Fixture();
        f.SetPhase("future hold");
        await f.Manager.DisposePool("source");
        f.Drain();
        Assert.Equal(1, f.Admissions);
        Assert.False(f.IsHeld());
    }

    [Fact]
    public void Factory_hold_respects_deadline_and_owner_but_expiry_does_not_publish_a_source_pool()
    {
        using var f = new Fixture();
        f.SetPhase("future hold");
        Assert.True(f.IsHeld());
        Assert.False(f.IsHeld(DateTime.UtcNow.AddHours(2)));
        f.Drain();
        Assert.Equal(0, f.Admissions);

        f.SetPhase("future hold");
        f.ReplaceFactory();
        Assert.False(f.IsHeld());
        f.Drain();
        Assert.Equal(0, f.Admissions);
        f.RegisterSourcePool();
        f.Drain();
        Assert.Equal(1, f.Admissions);
    }

    [Fact]
    public void Cleanup_remains_eligible_while_source_startup_is_pending()
    {
        using var f = new Fixture(cleanup: true);
        f.SetPhase("future hold");
        var eligible = (IEnumerable<int>)f.Invoke("EligibleQueueIndicesNoLock", DateTime.UtcNow)!;
        Assert.Equal([0], eligible.ToArray());
    }

    [Fact]
    public void Expired_ordinary_requests_still_leave_the_queue_while_the_source_is_pending()
    {
        using var f = new Fixture();
        var queue = Field<List<PersistedRace>>(f.Manager, "_raceQueue");
        queue[0] = f.Request with { QueuedAtUtc = DateTime.UtcNow.AddHours(-25) };
        f.Drain();
        Assert.Empty(f.Manager.QueuedRaces);
        Assert.Equal(0, f.Admissions);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-startup-admission-" + Guid.NewGuid().ToString("N"));
        private readonly bool _readOnly = ConfigManager.ReadOnly;
        private readonly CertificateManager _certificates;
        internal AppConfig Config { get; }
        internal ServerConfig Source => Config.Servers.Single(server => server.Id == "source");
        internal SpreadManager Manager { get; }
        internal PersistedRace Request { get; }
        internal int Admissions;

        internal Fixture(bool restore = true, bool alternateSource = false, string knownSource = "source", bool cleanup = false)
        {
            Directory.CreateDirectory(_root);
            ConfigManager.ReadOnly = false;
            Config = new() { Servers = [Server("source"), Server("peer-a"), Server("peer-b")] };
            Config.Spread.MergeRaceAnnounces = true;
            Request = new("TV", "Restored.Startup-GRP", ["source", "peer-a", "peer-b"],
                SpreadMode.Race, knownSource, "/TV/Restored.Startup-GRP", DateTime.UtcNow.AddHours(-1), Resumes: 1)
            {
                SourceProbeRetries = 2, WaitAttempts = 3, CleanupOnly = cleanup,
                SourceHints = alternateSource ? [new("peer-a", "TV", "/TV/Restored.Startup-GRP")] : []
            };
            if (restore) Assert.True(Store.Save(() => [Request]));
            _certificates = new CertificateManager(Path.Combine(_root, "pins.json"));
            Manager = new SpreadManager(Config, _root);
            Pools.Add("peer-a", new FtpConnectionPool(null!, 3));
            Pools.Add("peer-b", new FtpConnectionPool(null!, 3));
            // This callback is reached by actual queue admission, before job construction or
            // FTP. A wrong dispatch is observable while the original durable request survives.
            Manager._getMainPool = _ =>
            {
                Interlocked.Increment(ref Admissions);
                throw new IOException("Controlled admission stop; no FTP performed");
            };
        }

        private static ServerConfig Server(string id) => new()
        {
            Id = id, Connection = new() { Host = id + ".startup.invalid", Username = "synthetic" }
        };
        private RaceQueueStore Store => new(Path.Combine(_root, "race-queue.json"));
        private Dictionary<string, FtpConnectionPool> Pools => Field<Dictionary<string, FtpConnectionPool>>(Manager, "_spreadPools");
        private Dictionary<string, FtpClientFactory> Factories => Field<Dictionary<string, FtpClientFactory>>(Manager, "_factories");

        internal void SetPhase(string phase)
        {
            if (phase == "before factory") return;
            var factory = new FtpClientFactory(Source, _certificates);
            Factories["source"] = factory;
            if (phase == "initializing") return;
            var deadline = DateTime.UtcNow.AddHours(phase == "expired hold" ? -1 : 1);
            Manager.AdmissionUtcNow = () => deadline.AddHours(-1);
            Invoke("RecordPoolAdmissionHold", "source", factory, (DateTime?)deadline!);
            Manager.AdmissionUtcNow = () => DateTime.UtcNow;
        }
        internal void ReplaceFactory() => Factories["source"] = new FtpClientFactory(Source, _certificates);
        internal bool IsHeld(DateTime? now = null) => (bool)Invoke("IsPoolAdmissionHeldNoLock", "source", now ?? DateTime.UtcNow)!;
        internal void RegisterSourcePool() => Pools.Add("source", new FtpConnectionPool(null!, 3));
        internal void Drain() => Invoke("DequeueNextRace");
        internal object? Invoke(string name, params object[] args) => typeof(SpreadManager)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Manager, args);

        internal void AssertOriginalRequest()
        {
            var queued = Assert.Single(Manager.QueuedRaces);
            Assert.Equal(Request.RequestId, queued.RequestId);
            Assert.Equal(Request.QueuedAtUtc, queued.QueuedAtUtc);
            Assert.Equal(Request.SourceProbeRetries, queued.SourceProbeRetries);
            Assert.Equal(Request.WaitAttempts, queued.WaitAttempts);
            Assert.Equal(Request.Resumes, queued.Resumes);
            Assert.Equal(Request.KnownSourceServerId, queued.KnownSourceServerId);
            Assert.Equal(Request.KnownSourcePath, queued.KnownSourcePath);
            Assert.Equal(Request.RequestId, Assert.Single(Store.Load()).RequestId);
        }

        public void Dispose()
        {
            Manager.Dispose();
            ConfigManager.ReadOnly = _readOnly;
            Directory.Delete(_root, recursive: true);
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
