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
        await using var sourceServer = new ControlServer(cpsvAvailable: requested == FxpMode.Relay);
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
        Assert.Contains("CPSV", sourceServer.Commands);
        Assert.DoesNotContain(destinationServer.Commands, command => command.StartsWith("STOR ", StringComparison.Ordinal));
        Assert.True(source.Poisoned);
        Assert.True(destination.Poisoned);
    }

    [Fact]
    public async Task RelayProbeUsesSourceCpsvAndDestinationPortAsTlsServer()
    {
        await using var sourceServer = new ControlServer(cpsvAvailable: true);
        await using var destinationServer = new ControlServer();
        var (ok, _) = await RunRelayProbe(sourceServer, destinationServer);

        Assert.True(ok);
        Assert.Contains("CPSV", sourceServer.Commands);
        Assert.DoesNotContain("SSCN ON", sourceServer.Commands);
        Assert.Contains("SSCN OFF", destinationServer.Commands);
        Assert.DoesNotContain("SSCN ON", destinationServer.Commands);
        Assert.Contains("PORT 127,0,0,1,8,2", destinationServer.Commands);
        Assert.DoesNotContain("PASV", destinationServer.Commands);
    }

    [Fact]
    public async Task RelayProbeSendsRetrWhileDestinationWithholdsStorPreliminary()
    {
        // glftpd as an active destination may not answer STOR until data flows, and the
        // source only opens its side once it has RETR. Waiting for STOR's 150 deadlocked
        // the old probe into STOR 425 on every attempt.
        var retrSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sourceServer = new ControlServer(cpsvAvailable: true, onRetr: retrSeen);
        await using var destinationServer = new ControlServer(storGate: retrSeen.Task);
        var (ok, transfer) = await RunRelayProbe(sourceServer, destinationServer);

        Assert.True(ok);
        Assert.False(transfer.WasDupe);
        Assert.Contains(sourceServer.Commands, command => command.StartsWith("RETR ", StringComparison.Ordinal));
    }

    /// <summary>
    /// 2026-11-03 18:16: a direct probe's STOR/RETR preliminary hit FluentFTP's 30 s ReadTimeout
    /// before the equal 30 s managed timeout, so a plain timeout escaped into the Relay fallback,
    /// which sent TYPE I on a session that still owed a reply: "Reconnect needed but forbidden
    /// by the client config (SelfConnectMode)". Only a clean rejection proves both control
    /// channels are in sync; anything else must not be reused for Relay.
    /// </summary>
    [Fact]
    public async Task DirectProbeTransportTimeoutNeverFallsBackToRelayOnTheSameSessions()
    {
        var never = new TaskCompletionSource();
        await using var sourceServer = new ControlServer(cpsvAvailable: true);
        await using var destinationServer = new ControlServer(storGate: never.Task);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var sourceClient = Client(sourceServer.Port, 4000);
        using var destinationClient = Client(destinationServer.Port, 4000);
        await sourceClient.Connect(timeout.Token);
        await destinationClient.Connect(timeout.Token);
        var source = new PooledConnection(sourceClient, null!);
        var destination = new PooledConnection(destinationClient, null!);
        var transfer = new FxpTransfer(_ => { });

        Assert.False(await transfer.ExecuteAsync(source, destination, "/fixture/file.bin", "/target/file.bin",
            FxpMode.Relay, 10, timeout.Token, "race-fixture", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 4096));

        Assert.Single(sourceServer.Commands, command => command == "TYPE I");
        Assert.DoesNotContain("Reconnect", transfer.ErrorMessage);
        Assert.True(source.Poisoned);
        Assert.True(destination.Poisoned);
    }

    /// <summary>
    /// 2026-11-03 19:33 and 19:43 (v3.19.11): direct FXP files failed "Timed out trying to read
    /// data from the socket stream!" exactly 30 s after they started. The control channel is
    /// silent for the whole server-to-server transfer, and FluentFTP's GetReply enforces
    /// Config.ReadTimeout (30 s in production) beneath the managed per-file deadline, so any
    /// direct file slower than 30 s failed regardless of its transfer ceiling.
    /// </summary>
    [Theory]
    [InlineData(FxpMode.PasvPasv)]
    [InlineData(FxpMode.Relay)]
    public async Task DirectCompletionWaitsForTheTransferCeilingNotTheReadTimeout(FxpMode mode)
    {
        var slow = TimeSpan.FromMilliseconds(4500);
        await using var sourceServer = new ControlServer(cpsvAvailable: mode == FxpMode.Relay, completionDelay: slow);
        await using var destinationServer = new ControlServer(completionDelay: slow);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Above StorHeadStart so the probe's STOR preliminary isn't what times out.
        using var sourceClient = Client(sourceServer.Port, 3500);
        using var destinationClient = Client(destinationServer.Port, 3500);
        await sourceClient.Connect(timeout.Token);
        await destinationClient.Connect(timeout.Token);
        var source = new PooledConnection(sourceClient, null!);
        var destination = new PooledConnection(destinationClient, null!);
        var transfer = new FxpTransfer(_ => { });

        Assert.True(await transfer.ExecuteAsync(source, destination, "/fixture/file.bin", "/target/file.bin",
            mode, 20, timeout.Token, "race-fixture", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 4096),
            transfer.ErrorMessage);
        Assert.Equal(3500, sourceClient.Config.ReadTimeout);
        Assert.Equal(3500, destinationClient.Config.ReadTimeout);
        Assert.False(source.Poisoned);
        Assert.False(destination.Poisoned);
    }

    [Theory]
    [InlineData("550", "System Error- blue.lights.s04e06.1080p.hdtv.h264-organic.r42: File exists.", true)]
    [InlineData("553", "File is being uploaded by altruist", true)]
    [InlineData("553", "file.r01: It was uploaded by someone ( 0m  2s ago).", true)]
    [InlineData("550", "/incoming/x/file.r06: No such file or directory.", false)]
    [InlineData("550", "Permission denied.", false)]
    [InlineData("425", "File exists.", false)]
    public void DupeRejectionIsRecognizedByItsWordingOnEitherRejectionCode(string code, string message, bool dupe)
    {
        Assert.Equal(dupe, FxpTransfer.IsDupeRejection(new FtpReply { Code = code, Message = message }));
    }

    private static async Task<(bool Ok, FxpTransfer Transfer)> RunRelayProbe(ControlServer sourceServer, ControlServer destinationServer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // Above FxpTransfer.StorHeadStart, as production's 15 s read timeout is.
        using var sourceClient = Client(sourceServer.Port, 8000);
        using var destinationClient = Client(destinationServer.Port, 8000);
        await sourceClient.Connect(timeout.Token);
        await destinationClient.Connect(timeout.Token);
        var transfer = new FxpTransfer(_ => { });
        var ok = await transfer.ExecuteAsync(new PooledConnection(sourceClient, null!), new PooledConnection(destinationClient, null!),
            "/fixture/file.bin", "/target/file.bin", FxpMode.Relay, 10, timeout.Token,
            "race-fixture", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 4096);
        return (ok, transfer);
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

    private static AsyncFtpClient Client(int port, int readTimeoutMs = 2000)
    {
        var client = new AsyncFtpClient("127.0.0.1", "fixture", "fixture", port);
        client.Config.EncryptionMode = FtpEncryptionMode.None;
        client.Config.Noop = false;
        client.Config.SelfConnectMode = FtpSelfConnectMode.Never;
        client.Config.DisconnectWithQuit = false;
        client.Config.ConnectTimeout = 2000;
        client.Config.ReadTimeout = readTimeoutMs;
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

        private readonly bool _cpsvAvailable;
        private readonly Task? _storGate;
        private readonly TaskCompletionSource? _onRetr;
        private readonly TimeSpan _completionDelay;

        public ControlServer(bool duplicate = false, bool rejectPassive = false, bool cpsvAvailable = false,
            Task? storGate = null, TaskCompletionSource? onRetr = null, TimeSpan completionDelay = default)
        {
            _completionDelay = completionDelay;
            _cpsvAvailable = cpsvAvailable;
            _storGate = storGate;
            _onRetr = onRetr;
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
                    if (verb == "RETR") _onRetr?.TrySetResult();
                    if (verb == "STOR" && _storGate is not null) await _storGate.WaitAsync(_stop.Token);
                    var reply = verb switch
                    {
                        "USER" => "331 Password required",
                        "PASS" => "230 Logged in",
                        "SYST" => "215 UNIX Type: L8",
                        "FEAT" => "500 Features unavailable",
                        "PWD" => "257 \"/\" is current directory",
                        "PASV" => rejectPassive ? "425 Passive unavailable" : "227 Entering Passive Mode (127,0,0,1,8,1)",
                        "CPSV" when _cpsvAvailable => "227 Entering Passive Mode (127,0,0,1,8,2)",
                        "CPSV" => "500 Synthetic fixture has no data channel",
                        "STOR" when duplicate => "553 File already exists",
                        "STOR" or "RETR" => "150 Opening synthetic transfer",
                        "QUIT" => "221 Goodbye",
                        _ => "200 Command accepted"
                    };
                    await writer.WriteLineAsync(reply);
                    if (verb is "RETR" || verb == "STOR" && !duplicate)
                    {
                        if (_completionDelay > TimeSpan.Zero) await Task.Delay(_completionDelay, _stop.Token);
                        await writer.WriteLineAsync("226 Synthetic transfer complete");
                    }
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
