using System.Reflection;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Services;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class MountPoolPublicationTests
{
    [Fact]
    public async Task Live_resolver_cannot_obtain_a_pool_before_initialization_starts()
    {
        await using var fixture = new Fixture();

        Assert.Null(fixture.Mount.Pool);
        // The job also has the same pool in its initial snapshot. An unavailable
        // live result must not fall back to that snapshot and admit an early borrow.
        Assert.Null(fixture.Job.ResolveMainPool("synthetic"));
    }

    [Fact]
    public async Task Live_resolver_cannot_borrow_while_the_initial_login_is_blocked()
    {
        var gate = new WaitingGate();
        await using var fixture = new Fixture(gate);
        using var caller = new CancellationTokenSource();
        var initialization = fixture.Pool.Initialize(caller.Token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(1, fixture.Pool.TotalCreated);
            Assert.False(initialization.IsCompleted);
            Assert.Null(fixture.Mount.Pool);
            Assert.Null(fixture.Job.ResolveMainPool("synthetic"));
        }
        finally
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
        }
        Assert.Null(fixture.Mount.Pool);
        Assert.Equal(0, fixture.Pool.TotalCreated);
    }

    [Fact]
    public async Task Ready_and_exhausted_pools_remain_available_but_disposed_pools_do_not()
    {
        await using var fixture = new Fixture();
        // Simulate the flag published by successful Initialize. Actual native
        // publication is covered by NativeInitializationCheck; this test exercises
        // the resolver's lifecycle contract without logging in or mounting a drive.
        typeof(FtpConnectionPool).GetProperty(nameof(FtpConnectionPool.IsConnected))!
            .SetValue(fixture.Pool, true);
        typeof(FtpConnectionPool).GetField("_created", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Pool, 1);
        Assert.Same(fixture.Pool, fixture.Mount.Pool);
        Assert.Same(fixture.Pool, fixture.Job.ResolveMainPool("synthetic"));

        typeof(FtpConnectionPool).GetField("_created", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Pool, 0);
        Assert.True(fixture.Pool.IsExhausted);
        Assert.Same(fixture.Pool, fixture.Mount.Pool); // dashboard/recovery can still reinitialize it

        await fixture.Pool.DisposeAsync();
        // Even a stale readiness flag cannot expose a retired pool.
        typeof(FtpConnectionPool).GetProperty(nameof(FtpConnectionPool.IsConnected))!
            .SetValue(fixture.Pool, true);
        Assert.Null(fixture.Mount.Pool);
        Assert.Null(fixture.Job.ResolveMainPool("synthetic"));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public FtpConnectionPool Pool { get; }
        public MountService Mount { get; }
        public SpreadJob Job { get; }

        public Fixture(IAccountLoginGate? gate = null)
        {
            var config = new ServerConfig { Id = "synthetic" };
            Pool = new FtpConnectionPool(null!, 1, gate);
            // No Mount call occurs: the fixture owns only a synthetic pending pool.
            Mount = new MountService(config, new DownloadConfig(), null!);
            typeof(MountService).GetField("_pool", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Mount, Pool);
            Job = new SpreadJob("TEST", "Synthetic", SpreadMode.Race, new SpreadConfig(), new(),
                new() { [config.Id] = Pool }, new() { [config.Id] = config },
                new SpeedTracker(), new SkiplistEvaluator())
            {
                LiveMainPoolResolver = id => id == config.Id ? Mount.Pool : null
            };
        }

        public async ValueTask DisposeAsync()
        {
            Job.Dispose();
            typeof(MountService).GetField("_pool", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Mount, null);
            Mount.Dispose();
            await Pool.DisposeAsync();
        }
    }

    private sealed class WaitingGate : IAccountLoginGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Held => 0;
        public int Limit => 1;
        public int Reserved => 0;
        public int PriorityLimit => 1;
        public int GeneralLimit => 1;
        public async Task<bool> TryAcquireAsync(CancellationToken ct, TimeSpan? timeout = null, bool priority = false)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { }
            return false;
        }
        public void Release(bool priority = false) => throw new InvalidOperationException("No permit was granted");
        public void TightenTo(int newLimit) { }
    }
}
