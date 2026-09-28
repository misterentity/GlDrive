using System.IO;
using FluentFTP;
using FluentFTP.Exceptions;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Services;
using GlDrive.Tls;
using Xunit;

namespace GlDrive.Tests;

public sealed class NukePollerRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-nuke-poller-" + Guid.NewGuid().ToString("N"));
    private readonly TelemetryRecorder _recorder;
    private readonly NukeCursorStore _cursors;

    public NukePollerRecoveryTests()
    {
        _recorder = new TelemetryRecorder(_root, 64);
        _cursors = new NukeCursorStore(_root);
    }

    private NukePoller Create(Func<IEnumerable<NukePoller.PollTarget>> targets,
        Func<DateTime>? clock = null, TimeSpan? timeout = null) =>
        new(_recorder, targets, _cursors, _root, 1, clock, timeout, startTimer: false);

    private static FtpReply Success() => new()
    {
        Code = "200",
        InfoMessages = "2026-09-28 08:00:00 by tester: Synthetic.Release-GROUP (3x) - bad.pack"
    };

    [Theory]
    [InlineData(MountState.Unmounted)]
    [InlineData(MountState.Connecting)]
    [InlineData(MountState.Reconnecting)]
    [InlineData(MountState.Error)]
    public void A_nonconnected_mount_with_a_nonexhausted_pool_is_not_a_poll_target(MountState state)
    {
        var config = new ServerConfig { Id = "synthetic" };
        var certificates = new CertificateManager(Path.Combine(_root, "certs.json"));
        using var mount = new MountService(config, new DownloadConfig(), certificates);
        var pool = new FtpConnectionPool(new FtpClientFactory(config, certificates));
        typeof(MountService).GetField("_pool", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.SetValue(mount, pool);
        typeof(MountService).GetProperty(nameof(MountService.CurrentState))!.SetValue(mount, state);
        Assert.False(pool.IsExhausted); // Reproduces the production mount-initialization state.
        Assert.False(NukePoller.CreateTarget(mount).IsAvailable());
        typeof(MountService).GetProperty(nameof(MountService.CurrentState))!.SetValue(mount, MountState.Connected);
        Assert.True(NukePoller.CreateTarget(mount).IsAvailable());
    }

    [Fact]
    public async Task Unavailable_server_does_not_borrow_or_consume_failure_budget()
    {
        var available = false;
        var calls = 0;
        using var poller = Create(() => [new("site", () => available, _ =>
        {
            calls++;
            return Task.FromResult(Success());
        })]);
        for (var i = 0; i < 4; i++) await poller.PollAllAsync();
        Assert.Equal(0, calls);
        Assert.Equal(DateTime.MinValue, _cursors.Get("site"));

        available = true;
        await poller.PollAllAsync();
        Assert.Equal(1, calls);
        Assert.Equal(new DateTime(2026, 9, 28, 8, 0, 0), _cursors.Get("site"));
    }

    [Fact]
    public async Task Three_failures_cool_down_then_probe_and_success_resets_the_budget()
    {
        var now = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);
        var calls = 0;
        var failing = true;
        using var poller = Create(() => [new("site", () => true, _ =>
        {
            calls++;
            return Task.FromResult(failing ? new FtpReply { Code = "550" } : Success());
        })], () => now);

        for (var i = 0; i < 4; i++) await poller.PollAllAsync();
        Assert.Equal(3, calls);
        Assert.Equal(DateTime.MinValue, _cursors.Get("site"));
        now = now.AddHours(1).AddTicks(-1);
        await poller.PollAllAsync();
        Assert.Equal(3, calls);
        now = now.AddTicks(1);
        failing = false;
        await poller.PollAllAsync();
        Assert.Equal(4, calls);
        Assert.NotEqual(DateTime.MinValue, _cursors.Get("site"));

        failing = true;
        for (var i = 0; i < 3; i++) await poller.PollAllAsync();
        Assert.Equal(7, calls); // Recovery gave this site a fresh three-failure budget.
        await poller.PollAllAsync();
        Assert.Equal(7, calls);
    }

    [Fact]
    public async Task Deadline_during_command_does_not_advance_cursor_and_next_server_still_polls()
    {
        var calls = 0;
        using var client = new AsyncFtpClient("example.invalid");
        var lease = new PooledConnection(client, null!);
        var pending = new TaskCompletionSource<FtpReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var poller = Create(() =>
        [
            new("slow", () => true, ct => NukePoller.ExecuteNukesAsync(lease, nativeToken =>
            {
                Assert.False(nativeToken.CanBeCanceled);
                calls++;
                return pending.Task;
            }, ct)),
            new("healthy", () => true, _ => Task.FromResult(Success()))
        ], timeout: TimeSpan.FromMilliseconds(50));
        try
        {
            await poller.PollAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, calls);
            Assert.True(lease.Poisoned);
            Assert.False(pending.Task.IsCompleted);
            Assert.Equal(DateTime.MinValue, _cursors.Get("slow"));
            Assert.NotEqual(DateTime.MinValue, _cursors.Get("healthy"));
        }
        finally { pending.TrySetResult(Success()); }
    }

    [Fact]
    public async Task Concurrent_poll_is_skipped_and_dispose_cancels_the_active_wait()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var poller = Create(() => [new("site", () => true, async ct =>
        {
            calls++;
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Success();
        })]);
        var active = poller.PollAllAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await poller.PollAllAsync();
        Assert.Equal(1, calls);
        poller.Dispose();
        await active.WaitAsync(TimeSpan.FromSeconds(5));
        await poller.PollAllAsync();
        Assert.Equal(1, calls);
        Assert.Equal(DateTime.MinValue, _cursors.Get("site"));
    }

    [Fact]
    public async Task Failed_recovery_probe_reopens_cooldown_without_blocking_other_servers()
    {
        var now = DateTime.UtcNow;
        var failedCalls = 0;
        var healthyCalls = 0;
        using var poller = Create(() =>
        [
            new("failed", () => true, _ =>
            {
                failedCalls++;
                return Task.FromException<FtpReply>(new IOException("Transport unavailable"));
            }),
            new("healthy", () => true, _ => { healthyCalls++; return Task.FromResult(Success()); })
        ], () => now);
        for (var i = 0; i < 4; i++) await poller.PollAllAsync();
        Assert.Equal(3, failedCalls);
        Assert.Equal(4, healthyCalls);
        now = now.AddHours(1);
        await poller.PollAllAsync();
        await poller.PollAllAsync();
        Assert.Equal(4, failedCalls);
        Assert.Equal(6, healthyCalls);
    }

    [Theory]
    [InlineData("200")]
    [InlineData("500")]
    [InlineData("550")]
    public async Task Complete_replies_leave_the_connection_reusable(string code)
    {
        using var client = new AsyncFtpClient("example.invalid");
        var lease = new PooledConnection(client, null!);
        var result = await NukePoller.ExecuteNukesAsync(lease, ct =>
        {
            Assert.False(ct.CanBeCanceled);
            return Task.FromResult(new FtpReply { Code = code });
        }, CancellationToken.None);
        Assert.Equal(code, result.Code);
        Assert.False(lease.Poisoned);
    }

    [Theory]
    [InlineData("421")]
    [InlineData("150")]
    [InlineData("350")]
    [InlineData("")]
    public async Task Closed_or_incomplete_replies_quarantine_the_connection(string code)
    {
        using var client = new AsyncFtpClient("example.invalid");
        var lease = new PooledConnection(client, null!);
        await Assert.ThrowsAsync<FtpCommandException>(() => NukePoller.ExecuteNukesAsync(
            lease, _ => Task.FromResult(new FtpReply { Code = code }), CancellationToken.None));
        Assert.True(lease.Poisoned);
    }

    [Fact]
    public async Task Transport_exception_preserves_the_cause_and_quarantines()
    {
        using var client = new AsyncFtpClient("example.invalid");
        var lease = new PooledConnection(client, null!);
        var failure = new IOException("Remote control session closed");
        var caught = await Assert.ThrowsAsync<IOException>(() => NukePoller.ExecuteNukesAsync(
            lease, _ => Task.FromException<FtpReply>(failure), CancellationToken.None));
        Assert.Same(failure, caught);
        Assert.True(lease.Poisoned);
    }

    [Fact]
    public async Task Cancellation_before_command_does_not_start_or_poison_it()
    {
        using var client = new AsyncFtpClient("example.invalid");
        var lease = new PooledConnection(client, null!);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NukePoller.ExecuteNukesAsync(
            lease, _ => throw new InvalidOperationException("Command must not start"), new CancellationToken(true)));
        Assert.False(lease.Poisoned);
    }

    public void Dispose()
    {
        _recorder.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
