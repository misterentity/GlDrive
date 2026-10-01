using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// CpsvDataHelper bounds the data-channel TCP connect and TLS handshake at 10s each with
/// a linked CancellationTokenSource, so a stalled data channel surfaced as
/// OperationCanceledException — indistinguishable from "the caller cancelled".
/// Every caller read it that way: SpreadJob logged "FXP transfer timed out ... after 11s
/// ... (ceiling 180s)" (2026-09-30 17:32, Tales.From.The.Crypt.S06E09 r21), FxpTransfer
/// recorded the telemetry row as "cancelled" with no error, and DownloadManager marked a
/// download whose data channel stalled as user-Cancelled with no retry.
///
/// Rule: the helper's OWN deadline is a transport failure (IOException); only the
/// caller's token produces a cancellation.
/// </summary>
public sealed class CpsvDataChannelDeadlineTests
{
    [Fact]
    public async Task Own_deadline_surfaces_as_a_transport_failure_not_a_cancellation()
    {
        var ex = await Assert.ThrowsAsync<DataChannelTimeoutException>(() =>
            CpsvDataHelper.WithDataDeadline(
                async t => { await Task.Delay(Timeout.Infinite, t); return 0; },
                "data channel TLS handshake", TimeSpan.FromMilliseconds(50), CancellationToken.None));

        Assert.IsAssignableFrom<IOException>(ex);
        Assert.Contains("data channel TLS handshake", ex.Message);
        Assert.Equal(TimeSpan.FromMilliseconds(50), ex.Deadline);
    }

    [Fact]
    public async Task Caller_cancellation_still_propagates_as_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CpsvDataHelper.WithDataDeadline(
                async t => { await Task.Delay(Timeout.Infinite, t); return 0; },
                "data connection", TimeSpan.FromMinutes(5), cts.Token));

        Assert.IsNotType<DataChannelTimeoutException>(ex);
    }

    [Fact]
    public async Task Completed_operation_returns_its_result()
    {
        var result = await CpsvDataHelper.WithDataDeadline(
            _ => Task.FromResult(42), "data connection", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Other_failures_pass_through_unchanged()
    {
        await Assert.ThrowsAsync<SocketException>(() =>
            CpsvDataHelper.WithDataDeadline<int>(
                _ => throw new SocketException((int)SocketError.ConnectionRefused),
                "data connection", TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task Silent_peer_during_data_tls_is_a_transport_failure()
    {
        // The production shape: glftpd accepted the data socket but never sent its
        // ClientHello. We are the TLS server, so the handshake waits on the peer.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var peer = new TcpClient();
            var accept = listener.AcceptTcpClientAsync();
            await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var server = await accept;

            await Assert.ThrowsAsync<DataChannelTimeoutException>(() =>
                CpsvDataHelper.NegotiateDataTls(server.GetStream(), CancellationToken.None,
                    TimeSpan.FromMilliseconds(200)));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void Deadline_failure_is_not_a_clean_rejection()
    {
        // Scan's LIST handler poisons + wraps every non-clean IOException, so a stalled
        // data channel still spends (and discards) the borrowed login there.
        Assert.False(FtpCommandRejection.IsClean(
            new DataChannelTimeoutException("data connection", TimeSpan.FromSeconds(10))));
    }
}
