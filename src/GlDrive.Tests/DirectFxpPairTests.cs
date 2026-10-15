using System.Collections.Concurrent;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class DirectFxpPairTests
{
    [Fact]
    public async Task OppositeDirections_WaitForSameFirstPool_ThenBothComplete()
    {
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var a = new LeasePool("a", releaseFirst.Task);
        using var b = new LeasePool("b");
        using var cts = new CancellationTokenSource();
        var forward = SpreadManager.BorrowDirectFxpPair("a", a.Borrow, "b", b.Borrow, cts.Token);
        Assert.Equal(1, a.Acquired);
        var reverse = SpreadManager.BorrowDirectFxpPair("b", b.Borrow, "a", a.Borrow, cts.Token);
        try
        {
            // A is deliberately held before its first borrow returns. Source-first
            // ordering lets the reverse operation hold B and creates an ABBA cycle.
            Assert.Equal(0, b.Acquired);
            releaseFirst.TrySetResult();
            var first = await forward.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("a", first.Source.Site);
            Assert.Equal("b", first.Destination.Site);
            await first.Source.DisposeAsync();
            await first.Destination.DisposeAsync();
            var second = await reverse.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("b", second.Source.Site);
            Assert.Equal("a", second.Destination.Site);
        }
        finally
        {
            cts.Cancel();
            releaseFirst.TrySetResult();
            await Drain(forward);
            await Drain(reverse);
        }
        Assert.Equal(2, a.Acquired);
        Assert.Equal(2, b.Acquired);
        Assert.Equal(a.Acquired, a.Returned);
        Assert.Equal(b.Acquired, b.Returned);
    }

    [Fact]
    public async Task ReversePair_AcquiresCanonicalOrder_ButPreservesSourceIdentity()
    {
        var order = new ConcurrentQueue<string>();
        using var a = new LeasePool("a", order: order);
        using var z = new LeasePool("z", order: order);
        var pair = await SpreadManager.BorrowDirectFxpPair("z", z.Borrow, "a", a.Borrow, default);
        await using var source = pair.Source;
        await using var destination = pair.Destination;
        Assert.Equal(["a", "z"], order);
        Assert.Equal("z", source.Site);
        Assert.Equal("a", destination.Site);
    }

    [Fact]
    public async Task SecondBorrowFailure_ReturnsTheFirstLease()
    {
        using var a = new LeasePool("a");
        await Assert.ThrowsAsync<InvalidOperationException>(() => SpreadManager.BorrowDirectFxpPair(
            "a", a.Borrow, "b", _ => Task.FromException<Lease>(new InvalidOperationException("second failed")), default));
        Assert.Equal(1, a.Acquired);
        Assert.Equal(1, a.Returned);
    }

    [Fact]
    public async Task CallerCancellationWhileWaitingForSecond_ReturnsFirst_AndRemainsCancellation()
    {
        using var a = new LeasePool("a");
        using var cts = new CancellationTokenSource();
        var pair = SpreadManager.BorrowDirectFxpPair("a", a.Borrow, "b", Block, cts.Token);
        Assert.Equal(1, a.Acquired);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pair.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, a.Returned);
    }

    [Fact]
    public async Task DeadlineWhileWaitingForSecond_ReturnsFirst_AndReportsTimeout()
    {
        using var a = new LeasePool("a");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TimeoutException>(() => SpreadManager.BorrowDirectFxpPair(
            "a", a.Borrow, "b", Block, safety.Token, TimeSpan.FromMilliseconds(100)));
        Assert.False(safety.IsCancellationRequested);
        Assert.Equal(1, a.Acquired);
        Assert.Equal(1, a.Returned);
    }

    [Fact]
    public async Task IdenticalSite_IsRejectedBeforeAnyLeaseIsAcquired()
    {
        using var a = new LeasePool("a");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<ArgumentException>(() => SpreadManager.BorrowDirectFxpPair(
            "a", a.Borrow, "a", a.Borrow, safety.Token));
        Assert.Equal(0, a.Acquired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledGateWait_PreservesDeadlineOrCallerMeaning_AndReturnsFirst(bool callerCancellation)
    {
        using var a = new LeasePool("a");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pair = SpreadManager.BorrowDirectFxpPair("a", a.Borrow, "b", CancelledGateWait,
            cts.Token, callerCancellation ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100));
        if (callerCancellation)
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pair);
        }
        else
        {
            await Assert.ThrowsAsync<TimeoutException>(() => pair);
            Assert.False(cts.IsCancellationRequested);
        }
        Assert.Equal(1, a.Acquired);
        Assert.Equal(1, a.Returned);
    }

    private static async Task<Lease> CancelledGateWait(CancellationToken ct)
    {
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException)
        {
            // ServerLoginGate returns false on cancellation; AcquireAndConnect
            // then reports the unavailable permit using this exception shape.
            throw new InvalidOperationException("Account login cap reached — no login permit available");
        }
        throw new InvalidOperationException("Unreachable");
    }

    private static async Task<Lease> Block(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("Unreachable");
    }

    private static async Task Drain(Task<(Lease Source, Lease Destination)> task)
    {
        try
        {
            var pair = await task.WaitAsync(TimeSpan.FromSeconds(3));
            await pair.Source.DisposeAsync();
            await pair.Destination.DisposeAsync();
        }
        catch (OperationCanceledException) { }
    }

    private sealed class LeasePool(string site, Task? beforeReturn = null, ConcurrentQueue<string>? order = null) : IDisposable
    {
        private readonly SemaphoreSlim _available = new(1, 1);
        public int Acquired;
        public int Returned;

        public async Task<Lease> Borrow(CancellationToken ct)
        {
            await _available.WaitAsync(ct);
            Interlocked.Increment(ref Acquired);
            order?.Enqueue(site);
            var lease = new Lease(site, () => { Interlocked.Increment(ref Returned); _available.Release(); });
            try
            {
                if (beforeReturn != null) await beforeReturn.WaitAsync(ct);
                return lease;
            }
            catch { await lease.DisposeAsync(); throw; }
        }

        public void Dispose() => _available.Dispose();
    }

    private sealed class Lease(string site, Action release) : IAsyncDisposable
    {
        private Action? _release = release;
        public string Site => site;
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
