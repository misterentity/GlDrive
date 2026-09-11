using FluentFTP;
using FluentFTP.Exceptions;
using GlDrive.Ftp;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public sealed class ConnectionMonitorProbeTests
{
    private sealed class ProbeClient(Func<Task<FtpReply>> execute) : AsyncFtpClient("example.invalid")
    {
        public bool Called { get; private set; }
        public new Task<FtpReply> Execute(string command, CancellationToken token = default)
        {
            Assert.Equal("NOOP", command);
            Assert.False(token.CanBeCanceled); // Never cancel a native GnuTLS read.
            Called = true;
            return execute();
        }
    }

    [Fact]
    public async Task Positive_completion_keeps_the_connection_reusable()
    {
        using var client = new ProbeClient(() => Task.FromResult(new FtpReply { Code = "200" }));
        var conn = new PooledConnection(client, null!);
        await ConnectionMonitor.ProbeConnectionAsync(conn, token => client.Execute("NOOP", token), CancellationToken.None);
        Assert.True(client.Called);
        Assert.False(conn.Poisoned);
    }

    [Theory]
    [InlineData("421")]
    [InlineData("450")]
    [InlineData("500")]
    [InlineData("530")]
    [InlineData("150")]
    [InlineData("350")]
    public async Task Rejected_or_incomplete_reply_is_not_healthy(string code)
    {
        using var client = new ProbeClient(() => Task.FromResult(new FtpReply { Code = code, Message = "Rejected" }));
        var conn = new PooledConnection(client, null!);
        var error = await Assert.ThrowsAsync<FtpCommandException>(() =>
            ConnectionMonitor.ProbeConnectionAsync(conn, token => client.Execute("NOOP", token), CancellationToken.None));
        Assert.Equal(code, error.CompletionCode);
        Assert.True(conn.Poisoned);
    }

    [Fact]
    public async Task Transport_failure_preserves_reason_and_quarantines_connection()
    {
        var failure = new System.IO.IOException("Remote control connection closed");
        using var client = new ProbeClient(() => Task.FromException<FtpReply>(failure));
        var conn = new PooledConnection(client, null!);
        Assert.Same(failure, await Assert.ThrowsAsync<System.IO.IOException>(() =>
            ConnectionMonitor.ProbeConnectionAsync(conn, token => client.Execute("NOOP", token), CancellationToken.None)));
        Assert.True(conn.Poisoned);
    }

    [Fact]
    public async Task Timeout_quarantines_without_cancelling_the_pending_read()
    {
        var pending = new TaskCompletionSource<FtpReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new ProbeClient(() => pending.Task);
        var conn = new PooledConnection(client, null!);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => ConnectionMonitor.ProbeConnectionAsync(
                conn, token => client.Execute("NOOP", token), CancellationToken.None, TimeSpan.FromMilliseconds(20)));
            Assert.True(conn.Poisoned);
            Assert.False(pending.Task.IsCompleted);
        }
        finally { pending.TrySetResult(new FtpReply { Code = "200" }); }
    }

    [Fact]
    public async Task Shutdown_during_read_propagates_cancellation_and_quarantines()
    {
        var pending = new TaskCompletionSource<FtpReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        using var client = new ProbeClient(() => pending.Task);
        var conn = new PooledConnection(client, null!);
        try
        {
            var probe = ConnectionMonitor.ProbeConnectionAsync(conn, token => client.Execute("NOOP", token), cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
            Assert.True(conn.Poisoned);
            Assert.False(pending.Task.IsCompleted);
        }
        finally { pending.TrySetResult(new FtpReply { Code = "200" }); }
    }

    [Fact]
    public async Task Already_cancelled_shutdown_does_not_start_or_poison_a_read()
    {
        using var client = new ProbeClient(() => throw new InvalidOperationException("Unexpected NOOP"));
        var conn = new PooledConnection(client, null!);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConnectionMonitor.ProbeConnectionAsync(conn, token => client.Execute("NOOP", token), new CancellationToken(true)));
        Assert.False(client.Called);
        Assert.False(conn.Poisoned);
    }
}
