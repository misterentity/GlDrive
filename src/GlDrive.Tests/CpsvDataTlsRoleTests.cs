using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
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
/// within 10s" (2026-11-02 19:16..20:02, 35+ polls, 0 successes).
///
/// v3.19.7 answered such a peer as TLS client via SChannel; ioFTPD reset 3 of 3 of those
/// handshakes while accepting OpenSSL's. CPSV buys nothing on a daemon that stays TLS
/// server, so the site is switched to standard PASV (GnuTLS client, like the control
/// channel). Misreading a stalled glftpd backend as ioFTPD would knock a working site off
/// CPSV, so: proven hosts/IPs are never guessed about, an unproven host needs two
/// consecutive silent channels, and the switch expires without successful standard listings.
/// Each test uses its own loopback address and host name because state is keyed by both.
/// </summary>
public sealed class CpsvDataTlsRoleTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

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

    /// <summary>ioFTPD shape: the peer accepts the socket and waits for OUR ClientHello.</summary>
    private static async Task<TcpClient> ServeSilently(TcpListener listener) => await listener.AcceptTcpClientAsync();

    // Closing with unread bytes (a TLS 1.3 session ticket) makes Windows send RST, which can
    // destroy the payload before the other side reads it. Read to EOF before closing.
    private static async Task Drain(Stream s)
    {
        var buf = new byte[1024];
        try { while (await s.ReadAsync(buf) > 0) { } } catch (IOException) { }
    }

    private static async Task<string> NegotiateAndRead(IPAddress ip, int port, string host, TimeSpan? deadline = null)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ip, port);
        CpsvDataHelper.AttributeDataSocket(tcp.Client, host);
        using var ssl = await CpsvDataHelper.NegotiateDataTls(tcp.GetStream(), CancellationToken.None,
            deadline ?? Deadline, Window);
        using var reader = new StreamReader(ssl, Encoding.ASCII);
        return await reader.ReadToEndAsync();
    }

    private static async Task<Exception> SilentAttempt(TcpListener listener, IPAddress ip, int port, string host,
        TimeSpan? deadline = null)
    {
        var peer = ServeSilently(listener);
        try
        {
            return await Assert.ThrowsAnyAsync<IOException>(() => NegotiateAndRead(ip, port, host, deadline));
        }
        finally { (await peer).Dispose(); }
    }

    private static (TcpListener listener, int port) Listen(IPAddress ip, string host)
    {
        CpsvDataHelper.ForgetDataTlsRole(ip);
        CpsvDataHelper.ForgetHost(host);
        var l = new TcpListener(ip, 0);
        l.Start();
        return (l, ((IPEndPoint)l.LocalEndpoint).Port);
    }

    [Fact]
    public async Task Silent_peer_switches_an_unproven_host_to_pasv_on_the_second_strike()
    {
        var ip = IPAddress.Parse("127.0.0.41");
        const string host = "ioftpd.test";
        var (listener, port) = Listen(ip, host);
        try
        {
            var sw = Stopwatch.StartNew();
            var first = Assert.IsType<CpsvPeerIsTlsServerException>(await SilentAttempt(listener, ip, port, host));
            Assert.True(sw.Elapsed < Deadline, $"silent peer waited the whole deadline: {sw.Elapsed}");
            Assert.False(first.SwitchedToPasv);
            Assert.False(CpsvDataHelper.IsCpsvUnusable(host));

            var second = Assert.IsType<CpsvPeerIsTlsServerException>(await SilentAttempt(listener, ip, port, host));
            Assert.True(second.SwitchedToPasv);
            Assert.True(CpsvDataHelper.IsCpsvUnusable(host));
            Assert.True(CpsvDataHelper.IsCpsvUnusable(host.ToUpperInvariant()));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Peer_that_is_a_tls_client_is_served_and_proves_ip_and_host()
    {
        var ip = IPAddress.Parse("127.0.0.43");
        const string host = "glftpd.test";
        var (listener, port) = Listen(ip, host);
        try
        {
            var peer = ServeAsTlsClient(listener, "glftpd listing", TimeSpan.Zero);
            Assert.Equal("glftpd listing", await NegotiateAndRead(ip, port, host));
            await peer;
            Assert.Equal(CpsvDataHelper.DataTlsPeerRole.Client, CpsvDataHelper.LearnedDataTlsRole(ip));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Proven_host_is_never_switched_by_a_silent_backend()
    {
        // A BNC hands out several glftpd backends; a stalled NEW backend IP must not look
        // like ioFTPD once the site has proven itself. It times out as before.
        var provenIp = IPAddress.Parse("127.0.0.47");
        var newBackend = IPAddress.Parse("127.0.0.48");
        const string host = "bnc.test";
        var (l1, p1) = Listen(provenIp, host);
        CpsvDataHelper.ForgetDataTlsRole(newBackend);
        var l2 = new TcpListener(newBackend, 0);
        l2.Start();
        try
        {
            var peer = ServeAsTlsClient(l1, "ok", TimeSpan.Zero);
            Assert.Equal("ok", await NegotiateAndRead(provenIp, p1, host));
            await peer;

            var p2 = ((IPEndPoint)l2.LocalEndpoint).Port;
            for (var i = 0; i < CpsvDataHelper.SilentStrikesToSwitch + 1; i++)
                Assert.IsType<DataChannelTimeoutException>(
                    await SilentAttempt(l2, newBackend, p2, host, TimeSpan.FromMilliseconds(700)));
            Assert.False(CpsvDataHelper.IsCpsvUnusable(host));
        }
        finally { l1.Stop(); l2.Stop(); }
    }

    [Fact]
    public async Task Learned_client_peer_is_waited_for_past_the_probe_window()
    {
        var ip = IPAddress.Parse("127.0.0.44");
        const string host = "slow-glftpd.test";
        var (listener, port) = Listen(ip, host);
        try
        {
            var first = ServeAsTlsClient(listener, "fast", TimeSpan.Zero);
            Assert.Equal("fast", await NegotiateAndRead(ip, port, host));
            await first;

            var slow = ServeAsTlsClient(listener, "slow", Window + TimeSpan.FromMilliseconds(400));
            Assert.Equal("slow", await NegotiateAndRead(ip, port, host));
            await slow;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Learned_ip_is_forgotten_after_a_handshake_in_that_role_fails()
    {
        var ip = IPAddress.Parse("127.0.0.49");
        const string host = "swapped.test";
        var (listener, port) = Listen(ip, host);
        try
        {
            var peer = ServeAsTlsClient(listener, "ok", TimeSpan.Zero);
            Assert.Equal("ok", await NegotiateAndRead(ip, port, host));
            await peer;

            Assert.IsType<DataChannelTimeoutException>(
                await SilentAttempt(listener, ip, port, host, TimeSpan.FromMilliseconds(500)));
            Assert.Equal(CpsvDataHelper.DataTlsPeerRole.Unknown, CpsvDataHelper.LearnedDataTlsRole(ip));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Production_data_sockets_carry_their_control_host()
    {
        // The switch is only reachable if the socket OpenDataTcp hands out is attributed.
        var ip = IPAddress.Parse("127.0.0.50");
        const string host = "wired.test";
        var (listener, port) = Listen(ip, host);
        try
        {
            for (var i = 0; i < CpsvDataHelper.SilentStrikesToSwitch; i++)
            {
                var peer = ServeSilently(listener);
                using var tcp = await CpsvDataHelper.ConnectDataSocket(host, ip.ToString(), port, null, CancellationToken.None);
                await Assert.ThrowsAsync<CpsvPeerIsTlsServerException>(() =>
                    CpsvDataHelper.NegotiateDataTls(tcp.GetStream(), CancellationToken.None, Deadline, Window));
                (await peer).Dispose();
            }
            Assert.True(CpsvDataHelper.IsCpsvUnusable(host));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void Switch_expires_so_a_wrong_one_cannot_become_permanent()
    {
        const string host = "expiry.test";
        CpsvDataHelper.ForgetHost(host);
        var now = DateTime.UtcNow;

        CpsvDataHelper.MarkCpsvUnusableAt(host, now - CpsvDataHelper.CpsvSwitchLifetime + TimeSpan.FromMinutes(5));
        Assert.True(CpsvDataHelper.IsCpsvUnusable(host, now));

        CpsvDataHelper.MarkCpsvUnusableAt(host, now - CpsvDataHelper.CpsvSwitchLifetime - TimeSpan.FromMinutes(1));
        Assert.False(CpsvDataHelper.IsCpsvUnusable(host, now));
        Assert.False(CpsvDataHelper.IsCpsvUnusable(host, now - CpsvDataHelper.CpsvSwitchLifetime));
    }

    [Fact]
    public void Successful_standard_listings_keep_a_working_fallback_then_idle_expiry_allows_reprobe()
    {
        const string host = "active-pasv.test";
        CpsvDataHelper.ForgetHost(host);
        var start = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        CpsvDataHelper.MarkCpsvUnusableAt(host, start);

        // Exercise several original six-hour boundaries without resetting the
        // classification or manufacturing another deliberately failed CPSV LIST.
        for (var hour = 1; hour <= 24; hour++)
        {
            var completed = start.AddHours(hour);
            Assert.True(CpsvDataHelper.IsCpsvUnusable(host, completed));
            CpsvDataHelper.RecordStandardListingSuccess(host, completed);
        }

        var lastSuccess = start.AddHours(24);
        Assert.True(CpsvDataHelper.IsCpsvUnusable(host,
            lastSuccess + CpsvDataHelper.CpsvSwitchLifetime - TimeSpan.FromTicks(1)));
        Assert.False(CpsvDataHelper.IsCpsvUnusable(host,
            lastSuccess + CpsvDataHelper.CpsvSwitchLifetime));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unclassified-pasv.test")]
    public void Standard_listing_success_does_not_classify_an_unknown_host(string? host)
    {
        if (host is not null) CpsvDataHelper.ForgetHost(host);
        var now = new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        CpsvDataHelper.RecordStandardListingSuccess(host, now);
        Assert.False(CpsvDataHelper.IsCpsvUnusable(host, now));
    }

    [Fact]
    public void Standard_listing_success_renews_the_case_insensitive_host()
    {
        const string host = "case-pasv.test";
        CpsvDataHelper.ForgetHost(host);
        var start = new DateTime(2030, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        CpsvDataHelper.MarkCpsvUnusableAt(host, start);
        CpsvDataHelper.RecordStandardListingSuccess(host.ToUpperInvariant(), start.AddHours(5));

        Assert.True(CpsvDataHelper.IsCpsvUnusable(host, start.AddHours(7)));
        Assert.False(CpsvDataHelper.IsCpsvUnusable(host, start.AddHours(11)));
    }

    [Fact]
    public void An_older_completion_cannot_shorten_a_newer_fallback_renewal()
    {
        const string host = "out-of-order-pasv.test";
        CpsvDataHelper.ForgetHost(host);
        var start = new DateTime(2030, 1, 4, 0, 0, 0, DateTimeKind.Utc);
        CpsvDataHelper.MarkCpsvUnusableAt(host, start);
        CpsvDataHelper.RecordStandardListingSuccess(host, start.AddHours(5));
        CpsvDataHelper.RecordStandardListingSuccess(host, start.AddHours(2));

        Assert.True(CpsvDataHelper.IsCpsvUnusable(host, start.AddHours(9)));
        Assert.False(CpsvDataHelper.IsCpsvUnusable(host, start.AddHours(11)));
    }

    [Fact]
    public void A_completed_standard_listing_can_renew_an_existing_entry_past_its_old_deadline()
    {
        const string host = "completed-late-pasv.test";
        CpsvDataHelper.ForgetHost(host);
        var start = new DateTime(2030, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        CpsvDataHelper.MarkCpsvUnusableAt(host, start);
        // A listing selected PASV before the deadline and finished just after it.
        // Successful data is still evidence while that same fallback entry exists.
        CpsvDataHelper.RecordStandardListingSuccess(host, start.AddHours(7));

        Assert.True(CpsvDataHelper.IsCpsvUnusable(host, start.AddHours(8)));
        Assert.False(CpsvDataHelper.IsCpsvUnusable(host, start.AddHours(13)));
    }

    [Fact]
    public void A_completed_standard_listing_does_not_resurrect_an_expired_removed_entry()
    {
        const string host = "removed-pasv.test";
        CpsvDataHelper.ForgetHost(host);
        var start = new DateTime(2030, 1, 6, 0, 0, 0, DateTimeKind.Utc);
        CpsvDataHelper.MarkCpsvUnusableAt(host, start);
        Assert.False(CpsvDataHelper.IsCpsvUnusable(host, start + CpsvDataHelper.CpsvSwitchLifetime));
        CpsvDataHelper.RecordStandardListingSuccess(host, start.AddHours(7));

        Assert.False(CpsvDataHelper.IsCpsvUnusable(host, start.AddHours(7)));
    }

    [Fact]
    public async Task Expiry_racing_a_successful_listing_agrees_with_the_surviving_lease()
    {
        const string host = "concurrent-pasv.test";
        var start = new DateTime(2030, 1, 7, 0, 0, 0, DateTimeKind.Utc);
        var observedAt = start.AddHours(7);
        try
        {
            for (var attempt = 0; attempt < 1000; attempt++)
            {
                CpsvDataHelper.MarkCpsvUnusableAt(host, start);
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var check = Task.Run(async () =>
                {
                    await ready.Task;
                    return CpsvDataHelper.IsCpsvUnusable(host, observedAt);
                });
                var renewal = Task.Run(async () =>
                {
                    await ready.Task;
                    CpsvDataHelper.RecordStandardListingSuccess(host, start.AddHours(5));
                });
                ready.SetResult();
                await Task.WhenAll(check, renewal);

                // Expiry may win and remove the entry, or renewal may win and keep
                // it. Reporting CPSV while the renewed lease survives is never valid.
                Assert.Equal(CpsvDataHelper.IsCpsvUnusable(host, observedAt), await check);
            }
        }
        finally { CpsvDataHelper.ForgetHost(host); }
    }

    [Fact]
    public void Pool_stops_using_cpsv_once_its_host_is_switched()
    {
        const string host = "pool-ioftpd.test";
        CpsvDataHelper.ForgetHost(host);
        var pool = new FtpConnectionPool(null!);
        typeof(FtpConnectionPool).GetField("_cpsvAdvertised", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(pool, true);
        typeof(FtpConnectionPool).GetProperty(nameof(FtpConnectionPool.ControlHost))!.SetValue(pool, host);

        Assert.True(pool.UseCpsv);
        CpsvDataHelper.MarkCpsvUnusableAt(host, DateTime.UtcNow);
        Assert.False(pool.UseCpsv);
        CpsvDataHelper.ForgetHost(host);
        Assert.True(pool.UseCpsv);
    }

    [Fact]
    public async Task Peer_that_never_handshakes_still_times_out_within_the_deadline()
    {
        var ip = IPAddress.Parse("127.0.0.46");
        var (listener, port) = Listen(ip, "never.test");
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(ip, port);
            using var peer = await accept;

            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<IOException>(() =>
                CpsvDataHelper.NegotiateDataTls(tcp.GetStream(), CancellationToken.None,
                    TimeSpan.FromMilliseconds(800), Window));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"overran deadline: {sw.Elapsed}");
        }
        finally { listener.Stop(); }
    }
}
