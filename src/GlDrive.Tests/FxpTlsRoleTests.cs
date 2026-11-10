using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentFTP;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class FxpTlsRoleTests
{
    [Theory]
    [InlineData(FxpMode.PasvPasv)]
    [InlineData(FxpMode.CpsvPasv)]
    [InlineData(FxpMode.PasvCpsv)]
    public async Task Ordinary_direct_modes_assign_opposite_TLS_roles_and_copy_exact_bytes(FxpMode mode)
    {
        await using var fixture = new SecurePair();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var sourceClient = Client(fixture.Source.Port);
        using var destinationClient = Client(fixture.Destination.Port);
        await sourceClient.Connect(timeout.Token);
        await destinationClient.Connect(timeout.Token);
        // Pooled sessions may already have SSCN ON from an earlier role.
        await sourceClient.Execute("SSCN ON", timeout.Token);
        await destinationClient.Execute("SSCN ON", timeout.Token);
        fixture.Source.Commands.Clear();
        fixture.Destination.Commands.Clear();
        var transfer = new FxpTransfer(_ => { });
        var source = new PooledConnection(sourceClient, null!);
        var destination = new PooledConnection(destinationClient, null!);

        var ok = await transfer.ExecuteAsync(source, destination, "/fixture/payload.bin", "/target/payload.bin",
            mode, 8, timeout.Token, fileSizeBytes: fixture.Payload.Length);

        Assert.True(ok, $"{mode}: {transfer.ErrorMessage}; TLS: {fixture.Failure}; " +
            $"source=[{string.Join(",", fixture.Source.Commands)}]; dest=[{string.Join(",", fixture.Destination.Commands)}]");
        Assert.True(fixture.Encrypted);
        Assert.Equal(fixture.Payload, fixture.Received);
        Assert.Equal(mode != FxpMode.PasvCpsv, fixture.SourceTlsClientAtTransfer);
        Assert.Equal(mode == FxpMode.PasvCpsv, fixture.DestinationTlsClientAtTransfer);
        var passive = mode == FxpMode.PasvCpsv ? fixture.Source : fixture.Destination;
        var active = mode == FxpMode.PasvCpsv ? fixture.Destination : fixture.Source;
        Assert.Contains("SSCN OFF", passive.Commands);
        Assert.DoesNotContain("SSCN ON", passive.Commands);
        Assert.Contains("SSCN ON", active.Commands);
        Assert.True(Array.IndexOf(passive.Commands.ToArray(), "SSCN OFF") < Array.IndexOf(passive.Commands.ToArray(), "PASV"));
        Assert.False(source.Poisoned);
        Assert.False(destination.Poisoned);
        Assert.Equal("200", (await sourceClient.Execute("NOOP", timeout.Token)).Code);
        Assert.Equal("200", (await destinationClient.Execute("NOOP", timeout.Token)).Code);
        Assert.False(active.TlsClient);
        Assert.Equal("SSCN OFF", active.Commands.Last(command => command.StartsWith("SSCN ")));
        // The next borrower can perform a normal local TLS RETR on this same
        // session. Leaving SSCN ON would create two clients and fail TLS here.
        Assert.Equal(fixture.Payload, await fixture.DownloadLocal(active,
            mode == FxpMode.PasvCpsv ? destinationClient : sourceClient, timeout.Token));
    }

    [Theory]
    [InlineData(FxpMode.PasvPasv)]
    [InlineData(FxpMode.CpsvPasv)]
    [InlineData(FxpMode.PasvCpsv)]
    public async Task Clean_dupe_restores_active_role_before_pool_reuse(FxpMode mode)
    {
        await using var fixture = new SecurePair();
        fixture.Destination.StorReply = "553 File exists";
        using var sourceClient = Client(fixture.Source.Port);
        using var destinationClient = Client(fixture.Destination.Port);
        await sourceClient.Connect();
        await destinationClient.Connect();
        var source = new PooledConnection(sourceClient, null!);
        var destination = new PooledConnection(destinationClient, null!);
        var transfer = new FxpTransfer(_ => { });

        Assert.True(await transfer.ExecuteAsync(source, destination, "/fixture/payload.bin", "/target/payload.bin",
            mode, 5, CancellationToken.None));
        Assert.True(transfer.WasDupe);
        var active = mode == FxpMode.PasvCpsv ? fixture.Destination : fixture.Source;
        Assert.False(active.TlsClient);
        Assert.Equal("SSCN OFF", active.Commands.Last(command => command.StartsWith("SSCN ")));
        Assert.False(source.Poisoned);
        Assert.False(destination.Poisoned);
        Assert.DoesNotContain(fixture.Source.Commands, command => command.StartsWith("RETR "));
    }

    [Theory]
    [InlineData("550")]
    [InlineData("421")]
    [InlineData("timeout")]
    public async Task Failed_role_reset_quarantines_session_but_preserves_delivered_file(string reply)
    {
        await using var fixture = new SecurePair();
        fixture.Source.RejectResetCode = reply;
        using var sourceClient = Client(fixture.Source.Port);
        using var destinationClient = Client(fixture.Destination.Port);
        await sourceClient.Connect();
        await destinationClient.Connect();
        var source = new PooledConnection(sourceClient, null!);
        var destination = new PooledConnection(destinationClient, null!);
        var transfer = new FxpTransfer(_ => { });
        var elapsed = Stopwatch.StartNew();

        Assert.True(await transfer.ExecuteAsync(source, destination, "/fixture/payload.bin", "/target/payload.bin",
            FxpMode.CpsvPasv, 5, CancellationToken.None));
        Assert.Equal(fixture.Payload, fixture.Received);
        Assert.Equal(TransferState.Complete, transfer.State);
        Assert.True(source.Poisoned);
        Assert.False(destination.Poisoned);
        Assert.Equal("SSCN OFF", fixture.Source.Commands.Last());
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), "Cleanup must have a bounded managed wait.");
    }

    [Theory]
    [InlineData(FxpMode.CpsvPasv)]
    [InlineData(FxpMode.PasvCpsv)]
    public async Task Clean_negotiation_failure_never_returns_role_dirty_peer(FxpMode mode)
    {
        await using var fixture = new SecurePair();
        var passive = mode == FxpMode.PasvCpsv ? fixture.Source : fixture.Destination;
        var active = mode == FxpMode.PasvCpsv ? fixture.Destination : fixture.Source;
        passive.RejectPasvCode = "500";
        using var sourceClient = Client(fixture.Source.Port);
        using var destinationClient = Client(fixture.Destination.Port);
        await sourceClient.Connect();
        await destinationClient.Connect();
        var source = new PooledConnection(sourceClient, null!);
        var destination = new PooledConnection(destinationClient, null!);
        var transfer = new FxpTransfer(_ => { });

        Assert.False(await transfer.ExecuteAsync(source, destination, "/fixture/payload.bin", "/target/payload.bin",
            mode, 5, CancellationToken.None));
        Assert.Equal(mode == FxpMode.PasvCpsv ? FxpFaultSide.Source : FxpFaultSide.Dest, transfer.FaultSide);
        Assert.True((mode == FxpMode.PasvCpsv ? destination : source).Poisoned);
        Assert.DoesNotContain("SSCN OFF", active.Commands);
        Assert.DoesNotContain(fixture.Source.Commands.Concat(fixture.Destination.Commands),
            command => command.StartsWith("STOR ") || command.StartsWith("RETR "));
    }

    [Fact]
    public async Task Ambiguous_completion_never_sends_role_cleanup_on_unsafe_session()
    {
        await using var fixture = new SecurePair();
        fixture.Source.CompletionCode = "150";
        using var sourceClient = Client(fixture.Source.Port);
        using var destinationClient = Client(fixture.Destination.Port);
        await sourceClient.Connect();
        await destinationClient.Connect();
        var source = new PooledConnection(sourceClient, null!);
        var destination = new PooledConnection(destinationClient, null!);
        var transfer = new FxpTransfer(_ => { });

        Assert.False(await transfer.ExecuteAsync(source, destination, "/fixture/payload.bin", "/target/payload.bin",
            FxpMode.CpsvPasv, 5, CancellationToken.None));
        Assert.Equal(FxpFaultSide.None, transfer.FaultSide);
        Assert.True(source.Poisoned);
        Assert.DoesNotContain("SSCN OFF", fixture.Source.Commands);
    }

    private static AsyncFtpClient Client(int port)
    {
        var client = new AsyncFtpClient("127.0.0.1", "fixture", "fixture", port);
        client.Config.EncryptionMode = FtpEncryptionMode.None;
        client.Config.Noop = false;
        client.Config.SelfConnectMode = FtpSelfConnectMode.Never;
        client.Config.DisconnectWithQuit = false;
        client.Config.ConnectTimeout = 2000;
        client.Config.ReadTimeout = 5000;
        return client;
    }

    [Theory]
    [InlineData(true, "500")]
    [InlineData(true, "550")]
    [InlineData(false, "500")]
    [InlineData(false, "550")]
    public async Task Rejected_TLS_role_never_starts_a_transfer_or_downgrades_protection(bool sourceRejects, string code)
    {
        await using var fixture = new SecurePair();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var sourceClient = Client(fixture.Source.Port);
        using var destinationClient = Client(fixture.Destination.Port);
        await sourceClient.Connect(timeout.Token);
        await destinationClient.Connect(timeout.Token);
        (sourceRejects ? fixture.Source : fixture.Destination).RejectSscnCode = code;
        var transfer = new FxpTransfer(_ => { });

        Assert.False(await transfer.ExecuteAsync(new(sourceClient, null!), new(destinationClient, null!),
            "/fixture/payload.bin", "/target/payload.bin", FxpMode.CpsvPasv, 5, timeout.Token));
        Assert.Contains("SSCN", transfer.ErrorMessage);
        Assert.Equal(sourceRejects ? FxpFaultSide.Source : FxpFaultSide.Dest, transfer.FaultSide);
        var commands = fixture.Source.Commands.Concat(fixture.Destination.Commands).ToArray();
        Assert.DoesNotContain(commands, command => command.StartsWith("STOR ") || command.StartsWith("RETR "));
        Assert.DoesNotContain("PROT C", commands);
        Assert.Null(fixture.Received);
    }

    [Theory]
    [InlineData(FxpMode.CpsvPasv, "421")]
    [InlineData(FxpMode.CpsvPasv, "150")]
    [InlineData(FxpMode.CpsvPasv, "350")]
    [InlineData(FxpMode.Relay, "421")]
    [InlineData(FxpMode.Relay, "150")]
    [InlineData(FxpMode.Relay, "350")]
    public async Task Unsafe_role_reply_never_runs_a_direct_or_relay_data_command(FxpMode mode, string code)
    {
        await using var fixture = new SecurePair();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var sourceClient = Client(fixture.Source.Port);
        using var destinationClient = Client(fixture.Destination.Port);
        await sourceClient.Connect(timeout.Token);
        await destinationClient.Connect(timeout.Token);
        fixture.Destination.RejectSscnCode = code;
        var transfer = new FxpTransfer(_ => { });
        var source = new PooledConnection(sourceClient, null!);
        var destination = new PooledConnection(destinationClient, null!);

        Assert.False(await transfer.ExecuteAsync(source, destination, "/fixture/payload.bin", "/target/payload.bin",
            mode, 5, timeout.Token));
        Assert.Contains("SSCN", transfer.ErrorMessage);
        Assert.Equal(FxpFaultSide.None, transfer.FaultSide);
        Assert.DoesNotContain(fixture.Source.Commands.Concat(fixture.Destination.Commands),
            command => command.Split(' ')[0] is "PASV" or "CPSV" or "PORT" or "STOR" or "RETR");
        if (mode == FxpMode.Relay)
        {
            Assert.True(source.Poisoned);
            Assert.True(destination.Poisoned);
        }
        Assert.Null(fixture.Received);
    }

    // Control sockets are scripted FTP; the server-to-server data sockets perform
    // actual TLS 1.2 negotiation with a pinned disposable certificate. SSCN ON is
    // TLS client mode and OFF server mode, independently of TCP active/passive.
    private sealed class SecurePair : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(12));
        private readonly X509Certificate2 _certificate;
        internal Endpoint Source { get; }
        internal Endpoint Destination { get; }
        internal Task<bool> Completion { get; }
        internal byte[] Payload { get; } = Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray();
        internal byte[]? Received { get; private set; }
        internal bool Encrypted { get; private set; }
        internal string? Failure { get; private set; }
        internal bool SourceTlsClientAtTransfer { get; private set; }
        internal bool DestinationTlsClientAtTransfer { get; private set; }

        internal SecurePair()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            _certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null);
            Source = new Endpoint(this, _stop.Token);
            Destination = new Endpoint(this, _stop.Token);
            Completion = Transfer();
        }

        private async Task<bool> Transfer()
        {
            try
            {
                await Task.WhenAll(Source.Ready.Task, Destination.Ready.Task).WaitAsync(_stop.Token);
                SourceTlsClientAtTransfer = Source.TlsClient;
                DestinationTlsClientAtTransfer = Destination.TlsClient;
                var passive = Source.Passive != null ? Source : Destination;
                var active = ReferenceEquals(passive, Source) ? Destination : Source;
                Assert.NotNull(passive.Passive);
                Assert.NotNull(active.Target);
                Assert.True(IPAddress.IsLoopback(active.Target.Address));
                using var outgoing = new TcpClient();
                var accepted = passive.Passive.AcceptTcpClientAsync(_stop.Token);
                await outgoing.ConnectAsync(active.Target, _stop.Token);
                using var incoming = await accepted;
                using var passiveTls = new SslStream(incoming.GetStream(), false, passive.TlsClient ? ValidateCertificate : null);
                using var activeTls = new SslStream(outgoing.GetStream(), false, active.TlsClient ? ValidateCertificate : null);
                await Task.WhenAll(Authenticate(passiveTls, passive.TlsClient), Authenticate(activeTls, active.TlsClient));
                Encrypted = passiveTls.IsEncrypted && activeTls.IsEncrypted;
                var sourceTls = ReferenceEquals(passive, Source) ? passiveTls : activeTls;
                var destinationTls = ReferenceEquals(passive, Source) ? activeTls : passiveTls;
                var received = new byte[Payload.Length];
                await Task.WhenAll(sourceTls.WriteAsync(Payload.AsMemory(), _stop.Token).AsTask(),
                    destinationTls.ReadExactlyAsync(received.AsMemory(), _stop.Token).AsTask());
                Received = received;
                return true;
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException or OperationCanceledException)
            {
                Failure = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
            => certificate?.GetCertHashString() == _certificate.GetCertHashString();

        internal async Task<byte[]> DownloadLocal(Endpoint endpoint, AsyncFtpClient control, CancellationToken ct)
        {
            Assert.Equal("227", (await control.Execute("PASV", ct)).Code);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync((IPEndPoint)endpoint.Passive!.LocalEndpoint, ct);
            Assert.Equal("150", (await control.Execute("RETR /fixture/local.sfv", ct)).Code);
            using var tls = new SslStream(tcp.GetStream(), false, ValidateCertificate);
            await Authenticate(tls, client: true);
            var received = new byte[Payload.Length];
            await tls.ReadExactlyAsync(received.AsMemory(), ct);
            Assert.Equal("226", (await control.GetReply(ct)).Code);
            return received;
        }

        internal async Task SendLocal(Endpoint endpoint)
        {
            using var tcp = await endpoint.Passive!.AcceptTcpClientAsync(_stop.Token);
            using var tls = new SslStream(tcp.GetStream(), false, endpoint.TlsClient ? ValidateCertificate : null);
            await Authenticate(tls, endpoint.TlsClient);
            await tls.WriteAsync(Payload.AsMemory(), _stop.Token);
        }

        private Task Authenticate(SslStream stream, bool client) => client
            ? stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "fixture", EnabledSslProtocols = SslProtocols.Tls12
            }, _stop.Token)
            : stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12,
                ClientCertificateRequired = false
            }, _stop.Token);

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await Source.DisposeAsync();
            await Destination.DisposeAsync();
            await Completion;
            _certificate.Dispose();
            _stop.Dispose();
        }
    }

    private sealed class Endpoint : IAsyncDisposable
    {
        private readonly TcpListener _control = new(IPAddress.Loopback, 0);
        private readonly CancellationToken _stop;
        private readonly SecurePair _pair;
        private readonly Task _worker;
        internal ConcurrentQueue<string> Commands { get; } = new();
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Port => ((IPEndPoint)_control.LocalEndpoint).Port;
        internal TcpListener? Passive { get; private set; }
        internal IPEndPoint? Target { get; private set; }
        internal bool TlsClient { get; private set; }
        internal string? RejectSscnCode { get; set; }
        internal string? RejectResetCode { get; set; }
        internal string? RejectPasvCode { get; set; }
        internal string? StorReply { get; set; }
        internal string CompletionCode { get; set; } = "226";

        internal Endpoint(SecurePair pair, CancellationToken stop)
        {
            _pair = pair;
            _stop = stop;
            _control.Start();
            _worker = Serve();
        }

        private async Task Serve()
        {
            try
            {
                using var peer = await _control.AcceptTcpClientAsync(_stop);
                using var stream = peer.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, true) { AutoFlush = true, NewLine = "\r\n" };
                await writer.WriteLineAsync("220 Synthetic secure FXP fixture");
                while (await reader.ReadLineAsync(_stop) is { } command)
                {
                    Commands.Enqueue(command);
                    var parts = command.Split(' ', 2);
                    if (command == "SSCN OFF" && Ready.Task.IsCompleted && RejectResetCode is { } resetReply)
                    {
                        if (resetReply == "timeout") await Task.Delay(Timeout.Infinite, _stop);
                        else await writer.WriteLineAsync(resetReply + " Fixture role reset denied");
                        continue;
                    }
                    if (parts[0] == "SSCN" && RejectSscnCode is { } rejection)
                    {
                        await writer.WriteLineAsync(rejection + " Fixture SSCN denied");
                        continue;
                    }
                    if (parts[0] == "SSCN") TlsClient = parts[1] == "ON";
                    if (parts[0] == "PORT")
                    {
                        var address = parts[1].Split(',').Select(int.Parse).ToArray();
                        Target = new IPEndPoint(new IPAddress(address.Take(4).Select(i => (byte)i).ToArray()), address[4] * 256 + address[5]);
                    }
                    if (parts[0] == "PASV")
                    {
                        if (RejectPasvCode is { } pasvRejection)
                        {
                            await writer.WriteLineAsync(pasvRejection + " Fixture PASV denied");
                            continue;
                        }
                        Passive?.Stop();
                        Passive = new TcpListener(IPAddress.Loopback, 0);
                        Passive.Start();
                        var port = ((IPEndPoint)Passive.LocalEndpoint).Port;
                        await writer.WriteLineAsync($"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})");
                        continue;
                    }
                    if (parts[0] == "STOR" && StorReply is { } storReply)
                    {
                        await writer.WriteLineAsync(storReply);
                        continue;
                    }
                    if (command == "RETR /fixture/local.sfv")
                    {
                        await writer.WriteLineAsync("150 Opening local TLS download");
                        await _pair.SendLocal(this);
                        await writer.WriteLineAsync("226 Local TLS download complete");
                        continue;
                    }
                    await writer.WriteLineAsync(parts[0] switch
                    {
                        "USER" => "331 Password required", "PASS" => "230 Logged in",
                        "SYST" => "215 UNIX Type: L8", "FEAT" => "211 No extensions",
                        "PWD" => "257 \"/\" is current directory", "PRET" => "500 PRET unsupported",
                        "STOR" or "RETR" => "150 Opening encrypted data connection", _ => "200 OK"
                    });
                    if (parts[0] is "STOR" or "RETR")
                    {
                        Ready.TrySetResult();
                        var success = await _pair.Completion;
                        await writer.WriteLineAsync(success ? CompletionCode + " Encrypted transfer complete" : "435 Fixture TLS negotiation failed");
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _control.Stop();
            Passive?.Stop();
            await _worker;
        }
    }
}
