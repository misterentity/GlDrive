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

public sealed class SourceProbeDiscoveryTests
{
    [Fact]
    public async Task Clean_negative_discovery_reports_absence_with_the_normal_retry_class()
    {
        await using var endpoint = await Endpoint.Create();
        using var job = CreateJob(new() { ["negative"] = endpoint.Pool });

        var error = await RunToError(job);

        Assert.StartsWith("Release not found on any server", error);
        Assert.Equal(["/tv/Synthetic.Release"], endpoint.Server.ProbedPaths);
        var classified = Assert.IsType<(TimeSpan ttl, string reason)>(SpreadManager.ClassifyDeadRace(error));
        Assert.Equal("release-not-found", classified.reason);
        Assert.Equal(TimeSpan.FromMinutes(60), classified.ttl);
        Assert.Equal(0, endpoint.Pool.ActiveCount);
    }

    [Theory]
    [InlineData("cooldown")]
    [InlineData("missing")]
    [InlineData("disposed")]
    public async Task Clean_negative_and_unavailable_source_remain_inconclusive(string unavailable)
    {
        await using var negative = await Endpoint.Create();
        await using var other = new FtpConnectionPool(null!, 1);
        var pools = new Dictionary<string, FtpConnectionPool>
        {
            ["negative"] = negative.Pool,
            ["unavailable"] = other
        };
        using var job = CreateJob(pools);
        if (unavailable == "missing")
            job.UpdatePools(new() { ["negative"] = negative.Pool });
        else if (unavailable == "disposed")
            await other.DisposeAsync();
        else
            Set(other, "_refusedUntilTicks", DateTime.UtcNow.AddMinutes(1).Ticks);

        var error = await RunToError(job);

        AssertInconclusive(error, clean: 1, total: 2, failed: 1);
        Assert.Equal(["/tv/Synthetic.Release"], negative.Server.ProbedPaths);
        Assert.Equal(0, negative.Pool.ActiveCount);
        Assert.Equal(0, other.ActiveCount);
    }

    [Fact]
    public async Task Clean_negative_and_a_dropped_control_connection_remain_inconclusive()
    {
        await using var negative = await Endpoint.Create();
        await using var failed = await Endpoint.Create(disconnectOnProbe: true);
        using var job = CreateJob(new() { ["negative"] = negative.Pool, ["failed"] = failed.Pool });

        var error = await RunToError(job);

        AssertInconclusive(error, clean: 1, total: 2, failed: 1);
        Assert.Equal(["/tv/Synthetic.Release"], negative.Server.ProbedPaths);
        Assert.Equal(["/tv/Synthetic.Release"], failed.Server.ProbedPaths);
        Assert.Equal(0, negative.Pool.ActiveCount);
        Assert.Equal(0, failed.Pool.ActiveCount);
    }

    [Fact]
    public async Task Missing_source_pools_are_counted_even_when_no_probe_can_answer()
    {
        await using var pool = new FtpConnectionPool(null!, 1);
        using var job = CreateJob(new() { ["missing"] = pool });
        job.UpdatePools(new());

        AssertInconclusive(await RunToError(job), clean: 0, total: 1, failed: 1);
    }

    [Fact]
    public async Task A_found_source_takes_precedence_over_another_sites_probe_failure()
    {
        await using var found = await Endpoint.Create(releaseExists: true);
        await using var unavailable = new FtpConnectionPool(null!, 1);
        Set(unavailable, "_refusedUntilTicks", DateTime.UtcNow.AddMinutes(1).Ticks);
        using var job = CreateJob(new() { ["found"] = found.Pool, ["unavailable"] = unavailable });

        var error = await RunToError(job);

        // All fixture sites are download-only: phase 2 exits without any FTP write.
        Assert.Contains("found release on 1", error);
        Assert.DoesNotContain("Source probe inconclusive", error);
        Assert.DoesNotContain("Release not found", error);
        Assert.Null(SpreadManager.ClassifyDeadRace(error));
        Assert.Equal(["/tv/Synthetic.Release"], found.Server.ProbedPaths);
        Assert.Equal(0, found.Pool.ActiveCount);
    }

    private static SpreadJob CreateJob(Dictionary<string, FtpConnectionPool> pools)
    {
        var configs = pools.Keys.ToDictionary(id => id, id =>
        {
            var config = new ServerConfig { Id = id, Name = "Synthetic " + id };
            config.SpreadSite.Sections.Clear();
            config.SpreadSite.Sections["TV"] = "/tv";
            config.SpreadSite.DownloadOnly = true;
            config.Notifications.WatchPath = "";
            return config;
        });
        return new SpreadJob("TV", "Synthetic.Release", SpreadMode.Race, new SpreadConfig(),
            pools, pools, configs, new SpeedTracker(), new SkiplistEvaluator());
    }

    private static async Task<string> RunToError(SpreadJob job)
    {
        var errors = new List<string>();
        job.Error += (_, message) => errors.Add(message);
        await job.RunAsync().WaitAsync(TimeSpan.FromSeconds(8));
        return Assert.Single(errors);
    }

    private static void AssertInconclusive(string error, int clean, int total, int failed)
    {
        Assert.StartsWith("Source probe inconclusive", error);
        Assert.Contains($"{clean} of {total} path probes", error);
        Assert.Contains($"{failed} failed", error);
        var classified = Assert.IsType<(TimeSpan ttl, string reason)>(SpreadManager.ClassifyDeadRace(error));
        Assert.Equal("source-probe-inconclusive", classified.reason);
        Assert.InRange(classified.ttl, TimeSpan.FromTicks(1), TimeSpan.FromMinutes(5));
    }

    private sealed class Endpoint : IAsyncDisposable
    {
        internal required ControlServer Server { get; init; }
        internal required FtpConnectionPool Pool { get; init; }

        internal static async Task<Endpoint> Create(bool releaseExists = false, bool disconnectOnProbe = false)
        {
            var server = new ControlServer(releaseExists, disconnectOnProbe);
            var client = new AsyncFtpClient("127.0.0.1", "test", "test", server.Port);
            client.Config.NoopInterval = 0;
            client.Config.SelfConnectMode = FtpSelfConnectMode.Never;
            client.Config.ConnectTimeout = 2000;
            client.Config.ReadTimeout = 1000;
            await client.Connect();
            var pool = new FtpConnectionPool(null!, 1);
            Set(pool, "_created", 1);
            var channel = (Channel<AsyncFtpClient>)typeof(FtpConnectionPool)
                .GetField("_pool", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;
            Assert.True(channel.Writer.TryWrite(client));
            return new Endpoint { Server = server, Pool = pool };
        }

        public async ValueTask DisposeAsync()
        {
            await Pool.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private sealed class ControlServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        internal ConcurrentQueue<string> ProbedPaths { get; } = new();
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        internal ControlServer(bool releaseExists, bool disconnectOnProbe)
        {
            _listener.Start();
            _worker = Run(releaseExists, disconnectOnProbe);
        }

        private async Task Run(bool releaseExists, bool disconnectOnProbe)
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
                        if (path != "/")
                        {
                            ProbedPaths.Enqueue(path);
                            if (disconnectOnProbe)
                            {
                                peer.Client.LingerState = new LingerOption(true, 0);
                                peer.Close();
                                return;
                            }
                        }
                        await writer.WriteLineAsync(path == "/" || releaseExists
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
}
