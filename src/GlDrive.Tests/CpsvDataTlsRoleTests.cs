using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// CpsvDataHelper assumed every CPSV peer is glftpd, which answers CPSV by doing
/// SSL_connect on the data channel (TLS client), so GlDrive always did SSL_accept.
/// ioFTPD advertises CPSV in FEAT too but stays the TLS SERVER on the data channel.
/// Both ends then waited for the other's ClientHello: every LIST to "sitea"
/// (sitea.example.net, ioFTPD) failed with "data channel TLS handshake not completed
/// within 10s" (2026-11-02 19:16..20:02, 35+ polls, 0 successes), quarantining a login
/// each time. A live probe on 2026-11-02 confirmed the peer sent nothing for 4 s and
/// that a client-mode handshake on the same socket succeeded immediately.
///
/// Rule: the data-channel TLS role is a property of the PEER. Learn it per data IP:
/// a peer that speaks first is a TLS client (we serve), a peer that stays silent is a
/// TLS server (we connect). A learned role that later fails is forgotten.
/// Each test uses its own loopback address because roles are keyed by IP.
/// </summary>
public sealed class CpsvDataTlsRoleTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private static X509Certificate2 MakeCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=peer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    /// <summary>ioFTPD shape: the peer waits for OUR ClientHello, then sends a payload.</summary>
    private static async Task ServeAsTlsServer(TcpListener listener, string payload)
    {
        using var sock = await listener.AcceptTcpClientAsync();
        using var ssl = new SslStream(sock.GetStream(), false);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = MakeCert(),
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        });
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(payload));
        await ssl.ShutdownAsync();
        await Drain(ssl);
    }

    /// <summary>glftpd shape: the peer starts the handshake itself (after an optional delay).</summary>
    private static async Task ServeAsTlsClient(TcpListener listener, string payload, TimeSpan helloDelay)
    {
        using var sock = await listener.AcceptTcpClientAsync();
        if (helloDelay > TimeSpan.Zero) await Task.Delay(helloDelay);
        using var ssl = new SslStream(sock.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "data",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        });
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(payload));
        await ssl.ShutdownAsync();
        await Drain(ssl);
    }

    // Closing with unread bytes (a TLS 1.3 session ticket) makes Windows send RST, which can
    // destroy the payload before the other side reads it. Read to EOF before closing.
    private static async Task Drain(Stream s)
    {
        var buf = new byte[1024];
        try { while (await s.ReadAsync(buf) > 0) { } } catch (IOException) { }
    }

    private static async Task<string> NegotiateAndRead(IPAddress ip, int port)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ip, port);
        using var ssl = await CpsvDataHelper.NegotiateDataTls(tcp.GetStream(), CancellationToken.None, Deadline, Window);
        using var reader = new StreamReader(ssl, Encoding.ASCII);
        return await reader.ReadToEndAsync();
    }

    private static (TcpListener listener, int port) Listen(IPAddress ip)
    {
        CpsvDataHelper.ForgetDataTlsRole(ip);
        var l = new TcpListener(ip, 0);
        l.Start();
        return (l, ((IPEndPoint)l.LocalEndpoint).Port);
    }

    [Fact]
    public async Task Silent_peer_that_is_a_tls_server_is_negotiated_as_client()
    {
        var ip = IPAddress.Parse("127.0.0.41");
        var (listener, port) = Listen(ip);
        try
        {
            var peer = ServeAsTlsServer(listener, "drwxrwxrwx 0DAY");
            Assert.Equal("drwxrwxrwx 0DAY", await NegotiateAndRead(ip, port));
            await peer;
            Assert.Equal(CpsvDataHelper.DataTlsPeerRole.Server, CpsvDataHelper.LearnedDataTlsRole(ip));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Learned_server_peer_skips_the_probe_window()
    {
        var ip = IPAddress.Parse("127.0.0.42");
        var (listener, port) = Listen(ip);
        try
        {
            var first = ServeAsTlsServer(listener, "one");
            Assert.Equal("one", await NegotiateAndRead(ip, port));
            await first;

            var second = ServeAsTlsServer(listener, "two");
            var sw = Stopwatch.StartNew();
            Assert.Equal("two", await NegotiateAndRead(ip, port));
            await second;
            Assert.True(sw.Elapsed < Window, $"learned server role still probed: {sw.Elapsed}");
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Peer_that_is_a_tls_client_is_still_served()
    {
        var ip = IPAddress.Parse("127.0.0.43");
        var (listener, port) = Listen(ip);
        try
        {
            var peer = ServeAsTlsClient(listener, "glftpd listing", TimeSpan.Zero);
            Assert.Equal("glftpd listing", await NegotiateAndRead(ip, port));
            await peer;
            Assert.Equal(CpsvDataHelper.DataTlsPeerRole.Client, CpsvDataHelper.LearnedDataTlsRole(ip));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Learned_client_peer_is_waited_for_past_the_probe_window()
    {
        // Once a site has proven it starts the handshake, a slow ClientHello must not be
        // mistaken for a TLS server: that guess is reserved for peers we know nothing about.
        var ip = IPAddress.Parse("127.0.0.44");
        var (listener, port) = Listen(ip);
        try
        {
            var first = ServeAsTlsClient(listener, "fast", TimeSpan.Zero);
            Assert.Equal("fast", await NegotiateAndRead(ip, port));
            await first;

            var slow = ServeAsTlsClient(listener, "slow", Window + TimeSpan.FromMilliseconds(400));
            Assert.Equal("slow", await NegotiateAndRead(ip, port));
            await slow;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Stale_learned_role_is_forgotten_after_it_fails()
    {
        // A learned "server" role must not be permanent: if the address starts behaving
        // like glftpd (daemon swap, BNC put in front), one failure re-opens the probe.
        var ip = IPAddress.Parse("127.0.0.45");
        var (listener, port) = Listen(ip);
        try
        {
            var first = ServeAsTlsServer(listener, "ioftpd");
            Assert.Equal("ioftpd", await NegotiateAndRead(ip, port));
            await first;

            var swapped = ServeAsTlsClient(listener, "x", TimeSpan.Zero);
            await Assert.ThrowsAnyAsync<Exception>(() => NegotiateAndRead(ip, port));
            try { await swapped; } catch { /* the peer's half of the clash fails too */ }
            Assert.Equal(CpsvDataHelper.DataTlsPeerRole.Unknown, CpsvDataHelper.LearnedDataTlsRole(ip));

            var healed = ServeAsTlsClient(listener, "glftpd now", TimeSpan.Zero);
            Assert.Equal("glftpd now", await NegotiateAndRead(ip, port));
            await healed;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Peer_that_never_handshakes_still_times_out_within_the_deadline()
    {
        var ip = IPAddress.Parse("127.0.0.46");
        var (listener, port) = Listen(ip);
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(ip, port);
            using var peer = await accept;

            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAsync<DataChannelTimeoutException>(() =>
                CpsvDataHelper.NegotiateDataTls(tcp.GetStream(), CancellationToken.None,
                    TimeSpan.FromMilliseconds(800), Window));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"overran deadline: {sw.Elapsed}");
            Assert.Equal(CpsvDataHelper.DataTlsPeerRole.Unknown, CpsvDataHelper.LearnedDataTlsRole(ip));
        }
        finally { listener.Stop(); }
    }
}
