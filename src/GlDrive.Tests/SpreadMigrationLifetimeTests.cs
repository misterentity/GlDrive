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
using Xunit;

namespace GlDrive.Tests;

public sealed class SpreadMigrationLifetimeTests
{
    [Fact]
    public async Task Migration_uses_the_idle_spread_session_while_the_main_pool_is_occupied()
    {
        await using var fixture = await Fixture.Create(relocatedPathExists: true);
        await using var main = await Fixture.Create();
        await using var occupied = await main.Pool.Borrow();
        fixture.Job.LiveMainPoolResolver = _ => main.Pool;

        await fixture.Migrate().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("/recent/tv/Release",
            Get<ConcurrentDictionary<string, string>>(fixture.Job, "_extraSourcePaths")["source"]);
        Assert.Empty(main.Server.ProbedPaths);
        Assert.Equal(1, main.Pool.ActiveCount);
        Assert.Equal(0, fixture.Pool.ActiveCount);
        Assert.Equal(0, fixture.Pool.QuarantineSize);
        Assert.Empty(Get<HashSet<string>>(fixture.Job, "_sourceMigrationPending"));
        await using var reused = await fixture.Pool.Borrow();
        Assert.True((await reused.Client.Execute("NOOP")).Success);
    }

    [Fact]
    public async Task Migration_uses_the_spread_session_when_the_main_pool_has_no_connections()
    {
        await using var fixture = await Fixture.Create(oldPathExists: false, relocatedPathExists: true);
        await using var exhausted = new FtpConnectionPool(null!, 1);
        fixture.Job.LiveMainPoolResolver = _ => exhausted;

        await fixture.Migrate().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("/recent/tv/Release",
            Get<ConcurrentDictionary<string, string>>(fixture.Job, "_extraSourcePaths")["source"]);
        Assert.Equal(0, exhausted.TotalCreated);
        Assert.Empty(Get<HashSet<string>>(fixture.Job, "_sourceMigratedAway"));
        Assert.Equal(0, fixture.Pool.QuarantineSize);
    }

    [Fact]
    public async Task Losing_all_pools_during_relocation_does_not_report_unprobed_candidates_as_missing()
    {
        await using var fixture = await Fixture.Create(gateOldPath: true, oldPathExists: false);
        var searched = false;
        fixture.Job.SourceSearch = (_, _, _, _) =>
        {
            searched = true;
            return Task.FromResult<(string serverId, string path, string category)?>(null);
        };
        var migration = fixture.Migrate();
        await fixture.Server.OldPathEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Job.LiveMainPoolResolver = _ => null;
        fixture.Job.LivePoolResolver = _ => null;
        fixture.Server.ReleaseOldPath.TrySetResult();
        await migration.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(searched);
        Assert.Empty(Get<HashSet<string>>(fixture.Job, "_sourceMigratedAway"));
        Assert.Empty(Get<HashSet<string>>(fixture.Job, "_sourceMigrationPending"));
        Assert.DoesNotContain("/recent/tv/Release", fixture.Server.ProbedPaths);
    }

    [Theory]
    [InlineData("/old/Release")]
    [InlineData("/recent/tv/Release")]
    public async Task A_failed_directory_probe_discards_its_session_without_declaring_the_source_gone(string failedPath)
    {
        await using var fixture = await Fixture.Create(oldPathExists: false, readTimeout: 1000);
        fixture.Server.DisconnectPath = failedPath;
        var searched = false;
        fixture.Job.SourceSearch = (_, _, _, _) =>
        {
            searched = true;
            return Task.FromResult<(string serverId, string path, string category)?>(null);
        };

        if (failedPath == "/old/Release")
        {
            // Exercise the old-path command boundary directly: its conservative
            // result is independent of any subsequent reconnect attempt.
            var probe = (Task<bool>)typeof(SpreadJob).GetMethod("SourceStillHasRelease",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fixture.Job,
                    ["source", failedPath, CancellationToken.None, CancellationToken.None])!;
            Assert.True(await probe.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else
        {
            await fixture.Migrate().WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Contains(failedPath, fixture.Server.ProbedPaths);
        Assert.False(searched);
        Assert.Empty(Get<HashSet<string>>(fixture.Job, "_sourceMigratedAway"));
        Assert.Empty(Get<HashSet<string>>(fixture.Job, "_sourceMigrationPending"));
        Assert.Equal(0, fixture.Pool.ActiveCount);
        Assert.Equal(0, fixture.Pool.TotalCreated);
        Assert.Equal(1, fixture.Pool.QuarantineSize);
    }

    [Fact]
    public async Task Race_exit_drains_the_active_probe_even_after_a_duplicate_request_and_preserves_its_session()
    {
        await using var fixture = await Fixture.Create(gateOldPath: true);
        var migration = fixture.Migrate();
        await fixture.Server.OldPathEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = fixture.Migrate();
        var exit = fixture.Job.RunAsync(); // deterministic early exit through the real finally
        try
        {
            await Task.Delay(100);
            Assert.False(exit.IsCompleted);
        }
        finally { fixture.Server.ReleaseOldPath.TrySetResult(); }

        await Task.WhenAll(exit, migration, duplicate).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("/recent/tv/Release", fixture.Server.ProbedPaths);
        Assert.Empty(Get<ConcurrentDictionary<string, string>>(fixture.Job, "_extraSourcePaths"));
        await using var reused = await fixture.Pool.Borrow().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True((await reused.Client.Execute("NOOP")).Success);
        Assert.Equal(0, fixture.Pool.QuarantineSize);
    }

    [Fact]
    public async Task Race_exit_cancels_a_migration_waiting_for_a_login_without_starting_a_probe()
    {
        await using var fixture = await Fixture.Create();
        var occupied = await fixture.Pool.Borrow();
        var migration = fixture.Migrate();
        try
        {
            Assert.False(migration.IsCompleted);
            await fixture.Job.RunAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await migration.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(fixture.Server.ProbedPaths);
        }
        finally
        {
            await occupied.DisposeAsync();
            await migration.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task An_alternate_search_finishing_after_exit_cannot_register_a_source_or_start_another_migration()
    {
        await using var fixture = await Fixture.Create(oldPathExists: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<(string serverId, string path, string category)?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resolved = 0;
        fixture.Job.SourceSearch = (_, _, _, _) => { entered.TrySetResult(); return result.Task; };
        fixture.Job.LivePoolResolver = id =>
        {
            if (id == "alternate") Interlocked.Increment(ref resolved);
            return fixture.Pool;
        };
        fixture.Job.ServerConfigResolver = id => new ServerConfig { Id = id };
        var migration = fixture.Migrate();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var exit = fixture.Job.RunAsync();
        try
        {
            await Task.Delay(100);
            Assert.False(exit.IsCompleted);
        }
        finally { result.TrySetResult(("alternate", "/alternate/Release", "TV")); }
        await Task.WhenAll(exit, migration).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, resolved);
        Assert.Empty(Get<ConcurrentDictionary<string, string>>(fixture.Job, "_extraSourcePaths"));
        var probes = fixture.Server.ProbedPaths.Count;
        await fixture.Migrate();
        Assert.Equal(probes, fixture.Server.ProbedPaths.Count);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal required ControlServer Server { get; init; }
        internal required FtpConnectionPool Pool { get; init; }
        internal required SpreadJob Job { get; init; }

        internal static async Task<Fixture> Create(bool gateOldPath = false, bool oldPathExists = true,
            bool relocatedPathExists = false, int readTimeout = 5000)
        {
            var server = new ControlServer(gateOldPath, oldPathExists, relocatedPathExists);
            var client = new AsyncFtpClient("127.0.0.1", "test", "test", server.Port);
            client.Config.NoopInterval = 0;
            client.Config.SelfConnectMode = FtpSelfConnectMode.Never;
            client.Config.ConnectTimeout = 3000;
            client.Config.ReadTimeout = readTimeout;
            await client.Connect();
            var pool = new FtpConnectionPool(null!, 1);
            Set(pool, "_created", 1);
            Get<Channel<AsyncFtpClient>>(pool, "_pool").Writer.TryWrite(client);
            var config = new ServerConfig { Id = "source", Name = "Synthetic source" };
            config.SpreadSite.Sections.Clear();
            config.Notifications.WatchPath = "/recent";
            var spread = new SpreadConfig();
            spread.GlobalSkiplist.Add(new SkiplistRule { Pattern = "*", Action = SkiplistAction.Deny });
            var pools = new Dictionary<string, FtpConnectionPool> { [config.Id] = pool };
            var job = new SpreadJob("TV", "Release", SpreadMode.Race, spread, pools, pools,
                new() { [config.Id] = config }, new SpeedTracker(), new SkiplistEvaluator());
            Set(job, "_sitePathsRef", new Dictionary<string, string> { [config.Id] = "/old/Release" });
            Get<HashSet<string>>(job, "_sourceServersField").Add(config.Id);
            return new Fixture { Server = server, Pool = pool, Job = job };
        }

        internal Task Migrate() => (Task)typeof(SpreadJob).GetMethod("HandleSourceMigration",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Job, ["source", CancellationToken.None])!;

        public async ValueTask DisposeAsync()
        {
            Server.ReleaseOldPath.TrySetResult();
            Job.Dispose();
            await Pool.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private sealed class ControlServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        internal TaskCompletionSource OldPathEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseOldPath { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<string> ProbedPaths { get; } = new();
        internal string? DisconnectPath { get; set; }
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        internal ControlServer(bool gateOldPath, bool oldPathExists, bool relocatedPathExists)
        {
            _listener.Start();
            _worker = Run(gateOldPath, oldPathExists, relocatedPathExists);
        }

        private async Task Run(bool gateOldPath, bool oldPathExists, bool relocatedPathExists)
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
                    if (command.StartsWith("CWD ", StringComparison.Ordinal))
                    {
                        var path = command[4..];
                        if (path != "/") ProbedPaths.Enqueue(path);
                        if (path == DisconnectPath)
                        {
                            // An abrupt wire failure must fail the pending control read,
                            // rather than letting a graceful EOF wait for ReadTimeout.
                            peer.Client.LingerState = new LingerOption(true, 0);
                            peer.Close();
                            return;
                        }
                        if (path == "/old/Release")
                        {
                            OldPathEntered.TrySetResult();
                            if (gateOldPath) await ReleaseOldPath.Task.WaitAsync(_stop.Token);
                        }
                        await writer.WriteLineAsync(path == "/" || (oldPathExists && path == "/old/Release")
                            || (relocatedPathExists && path == "/recent/tv/Release")
                            ? "250 Directory changed" : "550 No such directory");
                        continue;
                    }
                    await writer.WriteLineAsync(command.Split(' ')[0] switch
                    {
                        "USER" => "331 Password required", "PASS" => "230 Logged in",
                        "SYST" => "215 UNIX Type: L8", "PWD" => "257 \"/\" is current directory",
                        "FEAT" => "211 No extensions", "QUIT" => "221 Goodbye", _ => "200 OK"
                    });
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _worker;
            _stop.Dispose();
        }
    }

    private static void Set(object target, string field, object value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static T Get<T>(object target, string field) => (T)target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
