using System.Reflection;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using FluentFTP;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

[CollectionDefinition("Pool borrow cancellation", DisableParallelization = true)]
public sealed class PoolBorrowCancellationCollection { }

/// <summary>
/// Race completion cancels background SFV borrows. A cancelled login-gate wait returns
/// false, just like a timeout; the pool must preserve the caller's cancellation instead
/// of parking the next race and spending its ghost-login recovery budget.
/// These exercise the real pool and gate using only synthetic loopback connections.
/// </summary>
[Collection("Pool borrow cancellation")]
public sealed class PoolBorrowCancellationTests
{
    [Fact]
    public async Task Already_cancelled_borrow_does_not_attempt_a_login_permit()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var gate = new ObservedGate();
        await using var pool = new FtpConnectionPool(null!, 1, gate);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.Borrow(caller.Token));

        Assert.Equal(0, gate.AcquireCalls);
        AssertUnchanged(pool);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Cancelled_wait_preserves_backoff_budget_and_permit_accounting(
        bool priority, bool blockedAtTotal)
    {
        // A limit of one lets the opposite role hold the total while this role's
        // sub-cap remains free. This also verifies the partially acquired permit
        // gets returned when cancellation interrupts the second semaphore wait.
        var gate = new ServerLoginGate("synthetic-cancellation", blockedAtTotal ? 1 : 2,
            blockedAtTotal ? 1 : 2, reserved: blockedAtTotal ? 0 : 1);
        var holderPriority = blockedAtTotal ? !priority : priority;
        Assert.True(await gate.TryAcquireAsync(default, TimeSpan.Zero, holderPriority));
        using var caller = new CancellationTokenSource();
        await using var pool = new FtpConnectionPool(null!, 1, gate, priority);

        var borrow = pool.Borrow(caller.Token);
        Assert.False(borrow.IsCompleted);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => borrow);

        AssertUnchanged(pool);
        Assert.Equal(1, gate.Held);
        gate.Release(holderPriority);
        Assert.True(await gate.TryAcquireAsync(default, TimeSpan.Zero, priority));
        gate.Release(priority);
        Assert.Equal(0, gate.Held);
        pool.FlushHealthCounters();
        Assert.Equal(1, pool.AbandonedBorrowsSinceFlush);
        Assert.Equal(0, pool.ExhaustCountSinceFlush);
        Assert.Equal(0, pool.GhostKillsSinceFlush);
    }

    [Fact]
    public async Task Cancellation_after_permit_grant_releases_it_before_connecting()
    {
        using var caller = new CancellationTokenSource();
        var gate = new ObservedGate { GrantAndCancel = caller };
        await using var pool = new FtpConnectionPool(null!, 1, gate);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.Borrow(caller.Token));

        Assert.Equal(1, gate.AcquireCalls);
        Assert.Equal(1, gate.ReleaseCalls);
        Assert.Equal(0, gate.Held);
        AssertUnchanged(pool);
    }

    [Fact]
    public async Task Genuine_gate_timeout_still_arms_backoff()
    {
        var gate = new ObservedGate();
        await using var pool = new FtpConnectionPool(null!, 1, gate);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.Borrow());

        Assert.True(pool.IsInLoginGateBackoff);
        Assert.Equal(1, gate.AcquireCalls);
        Assert.Equal(0, gate.ReleaseCalls);
        Assert.Equal(0, pool.TotalCreated);
        Assert.Equal(0, pool.ActiveCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_around_a_real_control_probe_preserves_the_pool_boundary(bool cancelBeforeBorrow)
    {
        await using var server = new WithheldNoopServer();
        var client = new AsyncFtpClient("127.0.0.1", "synthetic", "synthetic", server.Port);
        client.Config.Noop = false;
        client.Config.NoopInterval = 0;
        client.Config.SelfConnectMode = FtpSelfConnectMode.Never;
        client.Config.DisconnectWithQuit = false;
        client.Config.ConnectTimeout = 2000;
        client.Config.ReadTimeout = 5000;
        await client.Connect();
        var gate = new ObservedGate(initialHeld: 1);
        await using var pool = new FtpConnectionPool(null!, 1, gate);
        Set(pool, "_created", 1);
        Set(pool, "_permitsHeld", 1);
        var channel = (Channel<AsyncFtpClient>)typeof(FtpConnectionPool)
            .GetField("_pool", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;
        Assert.True(channel.Writer.TryWrite(client));
        pool.ConfigureHealth(validateOnBorrow: true, keepaliveSeconds: 0);
        using var caller = new CancellationTokenSource();
        if (cancelBeforeBorrow) caller.Cancel();

        try
        {
            var borrow = pool.Borrow(caller.Token);
            if (!cancelBeforeBorrow)
            {
                await server.NoopObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                caller.Cancel();
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => borrow.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.False(pool.IsThrottled);
            Assert.Equal(0, gate.AcquireCalls); // no replacement-login attempt
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(0, (int)typeof(FtpConnectionPool)
                .GetField("_ghostKilledSinceSuccess", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(pool)!);
            if (cancelBeforeBorrow)
            {
                Assert.False(server.NoopObserved.Task.IsCompleted);
                Assert.Equal(1, pool.TotalCreated);
                Assert.Equal(1, gate.Held);
                Assert.Equal(0, pool.QuarantineSize);
            }
            else
            {
                Assert.Equal(0, pool.TotalCreated);
                Assert.Equal(0, gate.Held);
                Assert.Equal(1, pool.QuarantineSize);
                // Managed FluentFTP may close its socket when cancellation fires.
                // The deferred quarantine is the pool's observable safety boundary;
                // this test does not make claims about native GnuTLS recv internals.
            }
        }
        finally
        {
            caller.Cancel();
            // Preserve the real 20-second native-safety delay; no global timing hooks
            // are changed. The fixture uses a managed loopback socket, not live GnuTLS.
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (pool.QuarantineSize > 0)
                await Task.Delay(25, drain.Token);
        }
    }

    private static void AssertUnchanged(FtpConnectionPool pool)
    {
        Assert.False(pool.IsThrottled);
        Assert.Equal(0, pool.TotalCreated);
        Assert.Equal(0, pool.ActiveCount);
        Assert.Equal(0, pool.QuarantineSize);
        Assert.Equal(0, (int)typeof(FtpConnectionPool)
            .GetField("_ghostKilledSinceSuccess", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(pool)!);
    }

    private static void Set(object target, string field, object value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class ObservedGate(int initialHeld = 0) : IAccountLoginGate
    {
        public CancellationTokenSource? GrantAndCancel { get; init; }
        public int AcquireCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public int Held { get; private set; } = initialHeld;
        public int Limit => 1;
        public int Reserved => 0;
        public int PriorityLimit => 1;
        public int GeneralLimit => 1;

        public Task<bool> TryAcquireAsync(CancellationToken ct, TimeSpan? timeout = null, bool priority = false)
        {
            AcquireCalls++;
            if (GrantAndCancel == null) return Task.FromResult(false);
            Held++;
            GrantAndCancel.Cancel();
            return Task.FromResult(true);
        }

        public void Release(bool priority = false) { ReleaseCalls++; Held--; }
        public void TightenTo(int newLimit) { }
    }

    private sealed class WithheldNoopServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        internal TaskCompletionSource NoopObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        internal WithheldNoopServer()
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
                    if (command == "NOOP")
                    {
                        NoopObserved.TrySetResult();
                        continue; // hold the reply so cancellation interrupts the actual probe
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
            finally { Disconnected.TrySetResult(); }
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
