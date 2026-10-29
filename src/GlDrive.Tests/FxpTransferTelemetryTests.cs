using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentFTP;
using GlDrive.AiAgent;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class FxpTransferTelemetryTests
{
    [Theory]
    [InlineData(FxpMode.Relay, FxpMode.CpsvPasv, false)]
    [InlineData(FxpMode.Relay, FxpMode.CpsvPasv, true)]
    [InlineData(FxpMode.CpsvPasv, FxpMode.CpsvPasv, false)]
    [InlineData(FxpMode.CpsvPasv, FxpMode.CpsvPasv, true)]
    [InlineData(FxpMode.PasvPasv, FxpMode.PasvPasv, false)]
    [InlineData(FxpMode.PasvPasv, FxpMode.PasvPasv, true)]
    [InlineData(FxpMode.PasvCpsv, FxpMode.PasvCpsv, false)]
    [InlineData(FxpMode.PasvCpsv, FxpMode.PasvCpsv, true)]
    public async Task RealExecutionReportsActualDirectProtocolAndDuplicateOutcome(FxpMode requested, FxpMode expected, bool duplicate)
    {
        await using var sourceServer = new ControlServer();
        await using var destinationServer = new ControlServer(duplicate: duplicate);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var sourceClient = Client(sourceServer.Port);
        using var destinationClient = Client(destinationServer.Port);
        await sourceClient.Connect(timeout.Token);
        await destinationClient.Connect(timeout.Token);
        var source = new PooledConnection(sourceClient, null!);
        var destination = new PooledConnection(destinationClient, null!);
        FileTransferEvent? observed = null;
        var transfer = new FxpTransfer(evt => observed = evt);

        Assert.True(await transfer.ExecuteAsync(source, destination, "/fixture/file.bin", "/target/file.bin",
            requested, 3, timeout.Token, "race-fixture", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 4096));

        Assert.NotNull(observed);
        Assert.Equal(expected.ToString(), observed.TransferMode);
        Assert.Equal(duplicate, observed.DuplicateSkipped);
        Assert.Null(observed.AbortReason);
        Assert.Equal(4096, observed.Bytes); // Preserve existing expected-byte telemetry semantics.
        Assert.Equal(duplicate, transfer.WasDupe);
        Assert.Equal(!duplicate, sourceServer.Commands.Any(command => command.StartsWith("RETR ", StringComparison.Ordinal)));
        Assert.Contains(destinationServer.Commands, command => command.StartsWith("STOR ", StringComparison.Ordinal));
        Assert.False(source.Poisoned);
        Assert.False(destination.Poisoned);
    }

    [Fact]
    public async Task FailedDirectProbeReportsRelayWhenFallbackWasAttempted()
    {
        await using var sourceServer = new ControlServer();
        await using var destinationServer = new ControlServer(rejectPassive: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var sourceClient = Client(sourceServer.Port);
        using var destinationClient = Client(destinationServer.Port);
        await sourceClient.Connect(timeout.Token);
        await destinationClient.Connect(timeout.Token);
        var source = new PooledConnection(sourceClient, null!);
        var destination = new PooledConnection(destinationClient, null!);
        FileTransferEvent? observed = null;
        var transfer = new FxpTransfer(evt => observed = evt);

        Assert.False(await transfer.ExecuteAsync(source, destination, "/fixture/file.bin", "/target/file.bin",
            FxpMode.Relay, 3, timeout.Token, "race-fixture", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 4096));

        Assert.NotNull(observed);
        Assert.Equal("Relay", observed.TransferMode);
        Assert.False(observed.DuplicateSkipped);
        Assert.NotEmpty(observed.AbortReason!);
        Assert.Contains("PASV", destinationServer.Commands);
        Assert.Contains("CPSV", sourceServer.Commands);
        Assert.True(source.Poisoned);
        Assert.True(destination.Poisoned);
    }

    [Fact]
    public void HistoricalTelemetryKeepsUnknownDuplicateAndTransportEvidence()
    {
        var row = JsonSerializer.Deserialize<FileTransferEvent>("""{"bytes":4096,"abortReason":null}""")!;
        Assert.Null(row.DuplicateSkipped);
        Assert.Null(row.TransferMode);
        var known = JsonSerializer.SerializeToElement(row with { DuplicateSkipped = false, TransferMode = "Relay" });
        Assert.False(known.GetProperty("duplicateSkipped").GetBoolean());
        Assert.Equal("Relay", known.GetProperty("transferMode").GetString());
    }

    private static AsyncFtpClient Client(int port)
    {
        var client = new AsyncFtpClient("127.0.0.1", "fixture", "fixture", port);
        client.Config.EncryptionMode = FtpEncryptionMode.None;
        client.Config.Noop = false;
        client.Config.SelfConnectMode = FtpSelfConnectMode.Never;
        client.Config.DisconnectWithQuit = false;
        client.Config.ConnectTimeout = 2000;
        client.Config.ReadTimeout = 2000;
        return client;
    }

    // Scripted control replies only. No data socket, TLS handshake, or remote endpoint is opened.
    private sealed class ControlServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        public ConcurrentQueue<string> Commands { get; } = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public ControlServer(bool duplicate = false, bool rejectPassive = false)
        {
            _listener.Start();
            _worker = Serve(duplicate, rejectPassive);
        }

        private async Task Serve(bool duplicate, bool rejectPassive)
        {
            try
            {
                using var peer = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var stream = peer.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
                await writer.WriteLineAsync("220 Synthetic FTP fixture");
                while (await reader.ReadLineAsync(_stop.Token) is { } command)
                {
                    Commands.Enqueue(command);
                    var verb = command.Split(' ', 2)[0];
                    var reply = verb switch
                    {
                        "USER" => "331 Password required",
                        "PASS" => "230 Logged in",
                        "SYST" => "215 UNIX Type: L8",
                        "FEAT" => "500 Features unavailable",
                        "PWD" => "257 \"/\" is current directory",
                        "PASV" => rejectPassive ? "425 Passive unavailable" : "227 Entering Passive Mode (127,0,0,1,8,1)",
                        "CPSV" => "500 Synthetic fixture has no data channel",
                        "STOR" when duplicate => "553 File already exists",
                        "STOR" or "RETR" => "150 Opening synthetic transfer",
                        "QUIT" => "221 Goodbye",
                        _ => "200 Command accepted"
                    };
                    await writer.WriteLineAsync(reply);
                    if (verb is "RETR" || verb == "STOR" && !duplicate)
                        await writer.WriteLineAsync("226 Synthetic transfer complete");
                    if (verb == "QUIT") break;
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            { }
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
