using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class PoolInitializationConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initializing_pool_reserves_capacity_against_concurrent_borrows(bool recovery)
    {
        using var caller = new CancellationTokenSource();
        var gate = new WaitingGate();
        await using var pool = new FtpConnectionPool(null!, 1, gate);
        var initialization = recovery ? pool.Reinitialize(caller.Token) : pool.Initialize(caller.Token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var borrow = pool.Borrow(caller.Token);
        try
        {
            // MountService exposes its pool while the first login is still connecting.
            // A scan must wait for that reserved connection, not wait for a second
            // permit whose cancellation could undo the first connection's count.
            Assert.Equal(1, gate.AcquireCalls);
            Assert.Equal(1, pool.TotalCreated);
            Assert.False(borrow.IsCompleted);
        }
        finally
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => borrow);
        }
        Assert.Equal(0, pool.TotalCreated);
        Assert.Equal(0, pool.ActiveCount);
        Assert.Equal(0, gate.Held);
        Assert.False(pool.IsThrottled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposal_during_initialization_preserves_cancellation_and_releases_reservation(bool recovery)
    {
        using var caller = new CancellationTokenSource();
        var gate = new WaitingGate();
        var pool = new FtpConnectionPool(null!, 1, gate);
        var initialization = recovery ? pool.Reinitialize(caller.Token) : pool.Initialize(caller.Token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await pool.DisposeAsync();
        caller.Cancel();
        // Disposing the initialization semaphore while its owner is awaiting a
        // login used to replace the caller's cancellation with ObjectDisposedException.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
        Assert.Equal(0, pool.TotalCreated);
        Assert.Equal(0, gate.Held);
        Assert.False(pool.IsConnected);
    }

    private sealed class WaitingGate : IAccountLoginGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AcquireCalls { get; private set; }
        public int Held => 0;
        public int Limit => 1;
        public int Reserved => 0;
        public int PriorityLimit => 1;
        public int GeneralLimit => 1;

        public async Task<bool> TryAcquireAsync(CancellationToken ct, TimeSpan? timeout = null, bool priority = false)
        {
            AcquireCalls++;
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { }
            return false;
        }

        public void Release(bool priority = false) => throw new InvalidOperationException("No permit was granted");
        public void TightenTo(int newLimit) { }
    }
}
