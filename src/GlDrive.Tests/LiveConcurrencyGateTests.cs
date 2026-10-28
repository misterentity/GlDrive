using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class LiveConcurrencyGateTests
{
    [Fact]
    public async Task Increasing_limit_wakes_waiting_work_without_restarting_active_work()
    {
        using var gate = new LiveConcurrencyGate(1);
        await gate.WaitAsync();
        var second = gate.WaitAsync();
        Assert.False(second.IsCompleted);
        gate.Resize(2);
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, gate.CurrentCount);
        gate.Release(); gate.Release();
        Assert.Equal(2, gate.CurrentCount);
    }

    [Fact]
    public async Task Lowering_limit_drains_active_work_before_admitting_more()
    {
        using var gate = new LiveConcurrencyGate(3);
        await gate.WaitAsync(); await gate.WaitAsync(); await gate.WaitAsync();
        gate.Resize(1);
        var next = gate.WaitAsync();
        gate.Release(); gate.Release();
        Assert.False(next.IsCompleted);
        Assert.Equal(0, gate.CurrentCount);
        gate.Release();
        await next.WaitAsync(TimeSpan.FromSeconds(2));
        gate.Release();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task Cancelled_wait_does_not_leak_or_claim_capacity()
    {
        using var gate = new LiveConcurrencyGate(1);
        await gate.WaitAsync();
        using var cts = new CancellationTokenSource();
        var wait = gate.WaitAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        gate.Release();
        Assert.Equal(1, gate.CurrentCount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.WaitAsync(cts.Token));
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task Contenders_never_exceed_resized_limit()
    {
        using var gate = new LiveConcurrencyGate(1);
        await gate.WaitAsync();
        var waits = Enumerable.Range(0, 12).Select(_ => gate.WaitAsync()).ToArray();
        gate.Resize(4);
        var active = 1;
        await Task.WhenAll(waits.Select(async wait =>
        {
            await wait;
            Assert.InRange(Interlocked.Increment(ref active), 1, 4);
            await Task.Yield();
            Interlocked.Decrement(ref active);
            gate.Release();
        })).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(3, gate.CurrentCount);
        gate.Release();
        Assert.Equal(4, gate.CurrentCount);
        Assert.Throws<InvalidOperationException>(() => gate.Release());
    }
}
