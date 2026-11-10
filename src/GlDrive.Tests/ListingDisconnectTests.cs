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

public sealed class ListingDisconnectTests
{
    [Fact]
    public async Task Disconnected_control_with_cached_type_is_an_io_failure_not_an_empty_listing_or_null_reference()
    {
        await using var server = new Server();
        using var client = await Connect(server);
        await client.Execute("TYPE I");
        // Reproduce the state FluentFTP's internal LIST retry retains after an
        // IOException: cached data type, but a closed control stream. This is a
        // managed loopback session; no GnuTLS teardown is induced by this test.
        ((IInternalFtpClient)client).GetBaseStream().Close();
        var error = await Assert.ThrowsAsync<IOException>(() => FtpListing.GetListing(client, "/fixture", default));
        Assert.IsType<NullReferenceException>(error.InnerException);
        Assert.False(client.IsConnected);
        Assert.Equal(1, server.Logins);
        Assert.Equal(0, server.PassiveCommands);
    }

    [Fact]
    public async Task Unexpected_post_borrow_listing_failure_discards_the_scan_session()
    {
        await using var server = new Server();
        var client = await Connect(server);
        // A non-IO exception at the actual library data-open boundary. It must
        // follow the same quarantine path as transport and protocol failures.
        client.Config.DataConnectionType = (FtpDataConnectionType)int.MaxValue;
        await using var pool = new FtpConnectionPool(null!, 1);
        typeof(FtpConnectionPool).GetField("_created", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(pool, 1);
        var queue = (Channel<AsyncFtpClient>)typeof(FtpConnectionPool)
            .GetField("_pool", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pool)!;
        Assert.True(queue.Writer.TryWrite(client));
        using var job = new SpreadJob("TV", "Fixture.S01E01-GROUP", SpreadMode.Race, new SpreadConfig(),
            new(), new() { ["fixture"] = pool }, new() { ["fixture"] = new ServerConfig { Name = "fixture" } },
            new SpeedTracker(), new SkiplistEvaluator());
        var scan = (Task)typeof(SpreadJob).GetMethod("ScanSites", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(job, [new Dictionary<string, string> { ["fixture"] = "/fixture" }, CancellationToken.None])!;
        await scan.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(queue.Reader.TryRead(out _));
        Assert.Equal(0, pool.TotalCreated);
        Assert.Equal(1, server.Logins);
    }

    private static async Task<AsyncFtpClient> Connect(Server server)
    {
        var client = new AsyncFtpClient("127.0.0.1", "synthetic", "synthetic", server.Port);
        FtpClientFactory.ApplyOwnerExclusiveConfig(client.Config);
        client.Config.ConnectTimeout = 2000;
        client.Config.ReadTimeout = 2000;
        await client.Connect();
        return client;
    }

    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        public int Logins { get; private set; }
        public int PassiveCommands { get; private set; }
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Server()
        {
            _listener.Start();
            _worker = Run();
        }

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
                    var verb = command.Split(' ')[0];
                    if (verb == "USER") Logins++;
                    if (verb is "EPSV" or "PASV")
                        PassiveCommands++;
                    await writer.WriteLineAsync(verb switch
                    {
                        "USER" => "331 Password required", "PASS" => "230 Logged in",
                        "SYST" => "215 UNIX Type: L8", "PWD" => "257 \"/\" is current directory",
                        "CWD" => "250 Directory changed", "FEAT" => "211 No extensions",
                        "QUIT" => "221 Goodbye", _ => "200 OK"
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
}
