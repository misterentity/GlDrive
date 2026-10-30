using System.Reflection;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GlDrive.Tests;

[CollectionDefinition("Spread scan admission", DisableParallelization = true)]
public sealed class SpreadScanAdmissionCollection { }

[Collection("Spread scan admission")]
public sealed class SpreadScanAdmissionTests
{
    private sealed class Gate(Func<Exception> fail) : IAccountLoginGate
    {
        public int Attempts { get; private set; }
        public Task<bool> TryAcquireAsync(CancellationToken ct, TimeSpan? timeout = null, bool priority = false)
        {
            Attempts++;
            throw fail(); // Never opens a network connection.
        }
        public void Release(bool priority = false) { }
        public int Held => 0;
        public int Limit => 4;
        public int Reserved => 0;
        public int PriorityLimit => 4;
        public int GeneralLimit => 4;
        public void TightenTo(int newLimit) { }
    }

    private sealed class Events : ILogEventSink
    {
        public List<LogEvent> Items { get; } = [];
        public void Emit(LogEvent logEvent) { lock (Items) Items.Add(logEvent); }
    }

    private static SpreadJob Job(Dictionary<string, FtpConnectionPool> main, Dictionary<string, FtpConnectionPool> spread)
        => new("TV", "Fixture.S01E01-GROUP", SpreadMode.Race, new SpreadConfig(), spread, main,
            main.Keys.Concat(spread.Keys).Distinct().ToDictionary(id => id, id => new ServerConfig { Name = id }),
            new SpeedTracker(), new SkiplistEvaluator());

    private static Task Scan(SpreadJob job, CancellationToken ct = default, params string[] sites)
        => (Task)typeof(SpreadJob).GetMethod("ScanSites", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(job, [sites.ToDictionary(id => id, _ => "/fixture"), ct])!;

    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static async Task Seed(FtpConnectionPool pool, ListingServer server)
    {
        var client = new AsyncFtpClient("127.0.0.1", "synthetic", "synthetic", server.Port);
        FtpClientFactory.ApplyOwnerExclusiveConfig(client.Config);
        client.Config.ConnectTimeout = 2000;
        client.Config.ReadTimeout = 2000;
        await client.Connect();
        typeof(FtpConnectionPool).GetField("_created", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pool, 1);
        Assert.True(Field<Channel<AsyncFtpClient>>(pool, "_pool").Writer.TryWrite(client));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public async Task Admission_hold_stops_repeated_empty_pool_borrows_and_warning_flood(
        bool siteHealth, bool authentication, bool loginRate)
    {
        var held = new FtpLoginHeldException("Synthetic admission hold", authentication,
            siteHealth || authentication || loginRate ? DateTime.UtcNow.AddHours(1) : null,
            loginRateHold: loginRate, siteHealthHold: siteHealth);
        var mainGate = new Gate(() => held);
        var fallbackGate = new Gate(() => held);
        await using var main = new FtpConnectionPool(null!, 3, mainGate);
        await using var spread = new FtpConnectionPool(null!, 3, fallbackGate);
        using var job = Job(new() { ["held"] = main }, new() { ["held"] = spread });
        var events = new Events();
        var previous = Log.Logger;
        using var logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(events).CreateLogger();
        Log.Logger = logger;
        try
        {
            for (var i = 0; i < 5; i++) await Scan(job, default, "held");
            Assert.Equal(1, mainGate.Attempts);
            Assert.Equal(0, fallbackGate.Attempts);
            Assert.DoesNotContain(events.Items, e => e.Level >= LogEventLevel.Warning);
            Assert.Single(events.Items, e => e.MessageTemplate.Text.StartsWith("Spread scan admission deferred"));
            Assert.DoesNotContain(events.Items, e => e.MessageTemplate.Text.StartsWith("Spread scan: all"));
        }
        finally { Log.Logger = previous; }
    }

    [Fact]
    public async Task Expired_hold_is_retried_and_an_extended_hold_is_observed()
    {
        var due = DateTime.UtcNow.AddHours(1);
        var gate = new Gate(() => new FtpLoginHeldException("Synthetic hold", false, due, siteHealthHold: true));
        await using var pool = new FtpConnectionPool(null!, 3, gate);
        using var job = Job(new() { ["held"] = pool }, new());
        await Scan(job, default, "held");
        await Scan(job, default, "held");
        Assert.Equal(1, gate.Attempts);
        Field<Dictionary<string, DateTime>>(job, "_scanAdmissionRetryAt")["held"] = DateTime.UtcNow.AddSeconds(-1);
        due = DateTime.UtcNow.AddHours(2);
        await Scan(job, default, "held");
        await Scan(job, default, "held");
        Assert.Equal(2, gate.Attempts);
    }

    [Fact]
    public async Task Held_site_does_not_suppress_another_sites_scan_or_caller_cancellation()
    {
        var heldGate = new Gate(() => new FtpLoginHeldException("Synthetic hold", false, DateTime.UtcNow.AddHours(1)));
        var busyGate = new Gate(() => new OperationCanceledException());
        await using var held = new FtpConnectionPool(null!, 3, heldGate);
        await using var busy = new FtpConnectionPool(null!, 3, busyGate);
        using var job = Job(new() { ["held"] = held, ["busy"] = busy }, new());
        await Scan(job, default, "held", "busy");
        await Scan(job, default, "held", "busy");
        Assert.Equal(1, heldGate.Attempts);
        Assert.Equal(2, busyGate.Attempts);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scan(job, stop.Token, "held"));
        Assert.Equal(1, heldGate.Attempts);
    }

    [Fact]
    public async Task Replaced_pool_does_not_inherit_previous_accounts_scan_hold()
    {
        var originalGate = new Gate(() => new FtpLoginHeldException("Old account hold", false, DateTime.UtcNow.AddHours(1)));
        var replacementGate = new Gate(() => new OperationCanceledException());
        await using var original = new FtpConnectionPool(null!, 1, originalGate);
        await using var replacement = new FtpConnectionPool(null!, 1, replacementGate);
        using var job = Job(new(), new() { ["site"] = original });
        await Scan(job, default, "site");
        Assert.True(job.IsScanAdmissionDeferred("site"));
        job.UpdatePools(new() { ["site"] = replacement });
        Assert.False(job.IsScanAdmissionDeferred("site"));
        await Scan(job, default, "site");
        Assert.Equal(1, replacementGate.Attempts);
    }

    [Fact]
    public void Admission_denials_are_deferrals_but_post_borrow_failures_remain_faults()
    {
        var held = new FtpLoginHeldException("Synthetic hold", false, DateTime.UtcNow.AddHours(1));
        Assert.True(ScanFailureClassifier.IsContention(held));
        Assert.True(ScanFailureClassifier.IsContention(new InvalidOperationException("Wrapped", held)));
        Assert.False(ScanFailureClassifier.IsContention(new ScanListingFailedException("/fixture", held)));
        Assert.False(ScanFailureClassifier.IsContention(new ScanListingFailedException("/fixture", new System.IO.IOException("Lost connection"))));
    }

    [Fact]
    public async Task Existing_fallback_is_scanned_during_hold_and_its_recovery_is_not_delayed()
    {
        var held = new FtpLoginHeldException("Synthetic hold", false, DateTime.UtcNow.AddHours(1));
        var mainGate = new Gate(() => held);
        var fallbackGate = new Gate(() => held);
        await using var server = new ListingServer();
        await using var main = new FtpConnectionPool(null!, 3, mainGate);
        await using var fallback = new FtpConnectionPool(null!, 3, fallbackGate);
        using var job = Job(new() { ["site"] = main }, new() { ["site"] = fallback });

        await Scan(job, default, "site");
        Assert.True(job.IsScanAdmissionDeferred("site"));
        await Seed(fallback, server);
        Assert.False(job.IsScanAdmissionDeferred("site"));
        await using (var busy = await fallback.Borrow())
        {
            await Scan(job, default, "site");
            Assert.Equal(0, server.Listings);
            Assert.Equal(0, fallbackGate.Attempts);
        }
        Assert.False(job.IsScanAdmissionDeferred("site"));
        await Scan(job, default, "site");
        await Scan(job, default, "site");

        Assert.Equal(2, server.Listings);
        Assert.Equal(1, mainGate.Attempts);
        Assert.Equal(0, fallbackGate.Attempts);
        Assert.Equal(1, job.Sites["site"].FilesOwned);
        Assert.Equal(0, fallback.QuarantineSize);
        Assert.Equal(1, fallback.TotalCreated);
    }

    [Fact]
    public async Task Post_borrow_listing_fault_remains_warning_when_fallback_is_held()
    {
        var held = new FtpLoginHeldException("Synthetic hold", false, DateTime.UtcNow.AddHours(1));
        await using var server = new ListingServer(failListing: true);
        await using var main = new FtpConnectionPool(null!, 1);
        await using var fallback = new FtpConnectionPool(null!, 1, new Gate(() => held));
        using var job = Job(new() { ["site"] = main }, new() { ["site"] = fallback });
        await Seed(main, server);
        var events = new Events();
        var previous = Log.Logger;
        using var logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(events).CreateLogger();
        Log.Logger = logger;
        try
        {
            await Scan(job, default, "site");
            var warning = Assert.Single(events.Items, e => e.MessageTemplate.Text.StartsWith("Spread scan FAILED"));
            Assert.IsType<ScanListingFailedException>(warning.Exception);
            Assert.Equal(LogEventLevel.Warning, warning.Level);
            Assert.Equal(1, server.Listings);
            Assert.Equal(1, main.QuarantineSize);
        }
        finally { Log.Logger = previous; }
    }

    [Fact]
    public async Task Running_race_parks_when_registered_destination_has_no_admitted_connection()
    {
        await using var server = new ListingServer();
        await using var source = new FtpConnectionPool(null!, 1);
        var gate = new Gate(() => new FtpLoginHeldException("Synthetic hold", false, DateTime.UtcNow.AddHours(1)));
        await using var destination = new FtpConnectionPool(null!, 1, gate);
        await Seed(source, server);
        var configs = new Dictionary<string, ServerConfig>
        {
            ["source"] = new() { Id = "source", Name = "source", SpreadSite = new() { DownloadOnly = true } },
            ["destination"] = new() { Id = "destination", Name = "destination", SpreadSite = new() { Sections = new() { ["TV"] = "/TV" } } }
        };
        var pools = new Dictionary<string, FtpConnectionPool> { ["source"] = source, ["destination"] = destination };
        using var job = new SpreadJob("TV", "Fixture.S01E01-GROUP", SpreadMode.Race,
            new SpreadConfig { MergeRaceAnnounces = false }, pools, pools, configs,
            new SpeedTracker(), new SkiplistEvaluator(), knownSourceServerId: "source", knownSourcePath: "/TV/Fixture.S01E01-GROUP");
        await job.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SpreadJobState.Waiting, job.State);
        Assert.Equal("Waiting for server reconnection", job.WaitReason);
        Assert.Equal(0, Field<int>(job, "_completionRetries"));
        // Discovery checks the configured section AND the default /recent watch
        // path. Then one scan observes the hold; one concurrent transfer setup
        // may also have started before that scan returns.
        var discoveries = SpreadJob.SourceHintCandidatePaths(configs["destination"], null, "TV", job.ReleaseName).Count;
        Assert.Equal(2, discoveries);
        Assert.InRange(gate.Attempts, discoveries + 1, discoveries + 2);
        var parkedAttempts = gate.Attempts;
        for (var i = 0; i < 10; i++) await Scan(job, default, "destination");
        Assert.Equal(parkedAttempts, gate.Attempts);
    }

    private sealed class ListingServer(bool failListing = false) : IAsyncDisposable
    {
        private readonly TcpListener _listener = Start();
        private readonly CancellationTokenSource _stop = new();
        private Task? _worker;
        public int Listings { get; private set; }
        public int Port { get { _worker ??= Run(); return ((IPEndPoint)_listener.LocalEndpoint).Port; } }
        private static TcpListener Start() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return listener; }
        private async Task Run()
        {
            TcpListener? data = null;
            try
            {
                using var peer = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var stream = peer.GetStream();
                using var reader = new StreamReader(stream);
                using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
                await writer.WriteLineAsync("220 Synthetic FTP");
                while (await reader.ReadLineAsync(_stop.Token) is { } command)
                {
                    var verb = command.Split(' ')[0];
                    if (verb is "EPSV" or "PASV")
                    {
                        data?.Stop();
                        data = Start();
                        var port = ((IPEndPoint)data.LocalEndpoint).Port;
                        await writer.WriteLineAsync(verb == "EPSV" ? $"229 Entering Extended Passive Mode (|||{port}|)" : $"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})");
                        continue;
                    }
                    if (verb is "LIST" or "MLSD")
                    {
                        Listings++;
                        using var payload = await data!.AcceptTcpClientAsync(_stop.Token);
                        if (failListing) { await writer.WriteLineAsync("421 Synthetic session lost"); continue; }
                        await writer.WriteLineAsync("150 Opening data connection");
                        using (var output = new StreamWriter(payload.GetStream()) { AutoFlush = true, NewLine = "\r\n" })
                            await output.WriteLineAsync(verb == "MLSD" ? "type=file;size=1024;modify=20261030000000; fixture.bin" : "-rw-r--r-- 1 user group 1024 Oct 30 00:00 fixture.bin");
                        await writer.WriteLineAsync("226 Listing complete");
                        continue;
                    }
                    await writer.WriteLineAsync(verb switch
                    {
                        "USER" => "331 Password required", "PASS" => "230 Logged in", "SYST" => "215 UNIX Type: L8",
                        "PWD" => "257 \"/\" is current directory", "CWD" => "250 Directory changed",
                        "FEAT" => "211 No extensions", "QUIT" => "221 Goodbye", _ => "200 OK"
                    });
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
            finally { data?.Stop(); }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); if (_worker != null) await _worker; _stop.Dispose(); }
    }
}
