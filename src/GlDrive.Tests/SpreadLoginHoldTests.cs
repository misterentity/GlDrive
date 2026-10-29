using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using GlDrive.Tls;
using Xunit;

namespace GlDrive.Tests;

[CollectionDefinition("Spread login holds", DisableParallelization = true)]
public sealed class SpreadLoginHoldCollection { }

[Collection("Spread login holds")]
public sealed class SpreadLoginHoldTests
{
    [Theory]
    [InlineData("source")]
    [InlineData("destination")]
    public async Task Held_borrow_parks_only_that_endpoint_preserves_peer_and_resumes_after_deadline(string heldId)
    {
        await using var fixture = new Fixture();
        var peerId = heldId == "source" ? "destination" : "source";
        await fixture.ConnectPeer(peerId);
        var retryAt = DateTime.UtcNow.AddMinutes(2);
        var admissions = 0;
        DateTime? Hold(FtpLoginAccount account)
        {
            if (account.Host != fixture.Configs[heldId].Connection.Host) return null;
            admissions++;
            return retryAt;
        }
        GuardedFtpLogin.RetryNotBeforeRequested += Hold;
        try
        {
            Assert.NotNull(fixture.NextTransfer());
            await fixture.Execute();
            var retries = Field<Dictionary<string, DateTime>>(fixture.Job, "_poolRetryAt");
            Assert.Equal(retryAt, Assert.Single(retries).Value);
            Assert.True(retries.ContainsKey(heldId));
            for (var pass = 0; pass < 200; pass++) Assert.Null(fixture.NextTransfer());
            Assert.Equal(1, admissions);
            Assert.Equal(1, fixture.Pools[peerId].TotalCreated);
            Assert.Equal(0, fixture.Pools[peerId].QuarantineSize);
            Assert.All(fixture.Pools.Values, pool => Assert.Equal(0, pool.ActiveCount));
            Assert.All(fixture.Job.Sites.Values, site => Assert.Equal(0, site.ActiveTransfers));
            Assert.Empty(Field<Dictionary<(string, string, string), int>>(fixture.Job, "_failureCounts"));
            Assert.DoesNotContain(fixture.Server.Commands, command => command.StartsWith("RETR ") || command.StartsWith("STOR "));
            await using (var peer = await fixture.Pools[peerId].Borrow()) Assert.False(peer.Poisoned);

            // The healthy alternate route remains schedulable while the original is held.
            if (heldId == "source") fixture.Observe("alternate");
            var alternate = fixture.NextTransfer(includeAlternate: true);
            Assert.NotNull(alternate);
            Assert.DoesNotContain(heldId, new[] { alternate.Value.srcId, alternate.Value.dstId });

            // Advancing the scheduler's recorded deadline needs neither a restart nor reset.
            retries[heldId] = DateTime.UtcNow.AddSeconds(-1);
            Assert.NotNull(fixture.NextTransfer());
        }
        finally { GuardedFtpLogin.RetryNotBeforeRequested -= Hold; }
    }

    [Fact]
    public async Task Both_failed_borrows_keep_their_own_hold_deadlines()
    {
        await using var fixture = new Fixture();
        var sourceRetry = DateTime.UtcNow.AddSeconds(40);
        var destinationRetry = sourceRetry.AddSeconds(50);
        DateTime? Hold(FtpLoginAccount account) => account.Host == fixture.Configs["source"].Connection.Host
            ? sourceRetry : account.Host == fixture.Configs["destination"].Connection.Host ? destinationRetry : null;
        GuardedFtpLogin.RetryNotBeforeRequested += Hold;
        try
        {
            await fixture.Execute();
            var retries = Field<Dictionary<string, DateTime>>(fixture.Job, "_poolRetryAt");
            Assert.Equal(2, retries.Count);
            Assert.Equal(sourceRetry, retries["source"]);
            Assert.Equal(destinationRetry, retries["destination"]);
            Assert.Null(fixture.NextTransfer());
            Assert.Equal(sourceRetry, Invoke(fixture.Job, "NextBackoffExpiry"));
            Assert.All(fixture.Pools.Values, pool => Assert.Equal(0, pool.TotalCreated));
        }
        finally { GuardedFtpLogin.RetryNotBeforeRequested -= Hold; }
    }

    [Fact]
    public async Task Unavailable_admission_state_gets_a_finite_retry_without_parking_the_peer()
    {
        await using var fixture = new Fixture();
        await fixture.ConnectPeer("source");
        DateTime? Hold(FtpLoginAccount account) => account.Host == fixture.Configs["destination"].Connection.Host
            ? throw new IOException("Synthetic admission state unavailable") : null;
        GuardedFtpLogin.RetryNotBeforeRequested += Hold;
        try
        {
            var before = DateTime.UtcNow;
            await fixture.Execute();
            var retry = Assert.Single(Field<Dictionary<string, DateTime>>(fixture.Job, "_poolRetryAt"));
            Assert.Equal("destination", retry.Key);
            Assert.InRange(retry.Value, before.AddSeconds(29), DateTime.UtcNow.AddSeconds(31));
            Assert.Null(fixture.NextTransfer());
        }
        finally { GuardedFtpLogin.RetryNotBeforeRequested -= Hold; }
    }

    [Fact]
    public async Task Expired_destination_retry_cannot_mask_a_future_pool_hold_and_idle_allowance_stays_bounded()
    {
        await using var fixture = new Fixture();
        var now = DateTime.UtcNow;
        var destinationRetries = Field<Dictionary<string, DateTime>>(fixture.Job, "_destRetryAt");
        destinationRetries["expired"] = now.AddMinutes(-1);
        destinationRetries["dropped"] = DateTime.MaxValue;
        var poolRetries = Field<Dictionary<string, DateTime>>(fixture.Job, "_poolRetryAt");
        poolRetries["source"] = now.AddMinutes(30);
        Assert.Equal(poolRetries["source"], Invoke(fixture.Job, "NextBackoffExpiry"));
        Assert.Equal(0, SpreadJob.EffectiveIdleSeconds(now.AddSeconds(-90), now, true, false, 1800));
        Assert.Equal(180, SpreadJob.EffectiveIdleSeconds(now.AddSeconds(-180), now, true, false, 1800));
        poolRetries["source"] = now.AddSeconds(-1);
        Assert.Null(Invoke(fixture.Job, "NextBackoffExpiry"));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "spread-login-hold-" + Guid.NewGuid().ToString("N"));
        internal Dictionary<string, ServerConfig> Configs { get; }
        internal Dictionary<string, FtpConnectionPool> Pools { get; }
        internal SpreadJob Job { get; }
        internal ControlServer Server { get; } = new();
        private readonly Dictionary<string, string> _paths;
        private readonly SpreadFileInfo _file = new() { Name = "fixture.r00", FullPath = "/fixture/Release/fixture.r00", Size = 1024 };

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            var certificates = new CertificateManager(Path.Combine(_root, "pins.json"));
            var suffix = Guid.NewGuid().ToString("N");
            Configs = new[] { "source", "destination", "alternate" }.ToDictionary(id => id, id =>
                new ServerConfig { Id = id, Name = id, Connection = new() { Host = id + "-" + suffix + ".invalid", Username = "synthetic" } });
            Pools = Configs.ToDictionary(p => p.Key, p => new FtpConnectionPool(new FtpClientFactory(p.Value, certificates), 1));
            _paths = Configs.Keys.ToDictionary(id => id, _ => "/fixture/Release");
            Job = new("TV", "Fixture.S01E01-GROUP", SpreadMode.Race, new(), Pools, Pools, Configs, new SpeedTracker(), new SkiplistEvaluator());
            Observe("source");
        }

        internal void Observe(string id)
        {
            Field<HashSet<string>>(Job, "_sourceServersField").Add(id);
            Invoke(Job, "ProcessFiles", id, new List<SpreadFileInfo> { _file }, 0L);
            Job.ReconcileProgressFromOwnership();
        }

        internal async Task ConnectPeer(string id)
        {
            var client = new AsyncFtpClient("127.0.0.1", "synthetic", "synthetic", Server.Port);
            FtpClientFactory.ApplyOwnerExclusiveConfig(client.Config);
            client.Config.ConnectTimeout = 2000;
            client.Config.ReadTimeout = 2000;
            await client.Connect();
            Set(Pools[id], "_created", 1);
            Assert.True(Field<Channel<AsyncFtpClient>>(Pools[id], "_pool").Writer.TryWrite(client));
        }

        internal (SpreadFileInfo file, string srcId, string dstId)? NextTransfer(bool includeAlternate = false) =>
            ((SpreadFileInfo file, string srcId, string dstId)?)Invoke(Job, "FindBestTransfer",
                includeAlternate ? _paths : _paths.Where(p => p.Key != "alternate").ToDictionary(), new SpreadScorer(new SpeedTracker()));

        internal async Task Execute()
        {
            Assert.True((bool)Invoke(Job, "TryClaimSlots", "source", "destination")!);
            await ((Task)Invoke(Job, "ExecuteTransfer", _file, "source", "destination", _paths["destination"],
                CancellationToken.None, CancellationToken.None, null!)!).WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async ValueTask DisposeAsync()
        {
            Job.Dispose();
            foreach (var pool in Pools.Values) await pool.DisposeAsync();
            await Server.DisposeAsync();
            Directory.Delete(_root, true);
        }
    }

    private sealed class ControlServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        internal ConcurrentQueue<string> Commands { get; } = new();
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal ControlServer() { _listener.Start(); _worker = Run(); }
        private async Task Run()
        {
            try
            {
                using var peer = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var stream = peer.GetStream();
                using var reader = new StreamReader(stream);
                using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
                await writer.WriteLineAsync("220 Synthetic FTP");
                while (await reader.ReadLineAsync(_stop.Token) is { } command)
                {
                    Commands.Enqueue(command);
                    await writer.WriteLineAsync(command.Split(' ')[0] switch
                    {
                        "USER" => "331 Password required", "PASS" => "230 Logged in",
                        "SYST" => "215 UNIX Type: L8", "PWD" => "257 \"/\" is current directory",
                        "FEAT" => "211 No extensions", "QUIT" => "221 Goodbye", _ => "200 OK"
                    });
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or SocketException) { }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _worker; _stop.Dispose(); }
    }

    private static object? Invoke(object target, string method, params object[] args) => target.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
