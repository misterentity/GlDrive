using GlDrive.Downloads.Segmented;
using Xunit;

namespace GlDrive.Tests;

public class KeepAwakeTests
{
    [Fact]
    public void First_acquire_blocks_sleep_and_last_release_allows_it()
    {
        var calls = new List<bool>();
        var counter = new KeepAwakeCounter(calls.Add);

        var a = counter.Acquire();
        var b = counter.Acquire();
        Assert.Equal([true], calls);
        Assert.Equal(2, counter.Count);

        a.Dispose();
        Assert.Equal([true], calls);
        b.Dispose();
        Assert.Equal([true, false], calls);
        Assert.Equal(0, counter.Count);

        using (counter.Acquire()) { }
        Assert.Equal([true, false, true, false], calls);
    }

    [Fact]
    public void Double_dispose_releases_once()
    {
        var calls = new List<bool>();
        var counter = new KeepAwakeCounter(calls.Add);

        var a = counter.Acquire();
        var b = counter.Acquire();
        a.Dispose();
        a.Dispose();
        Assert.Equal(1, counter.Count);
        Assert.Equal([true], calls);
        b.Dispose();
        Assert.Equal([true, false], calls);
    }

    [Fact]
    public void Concurrent_acquire_release_balances()
    {
        var awake = 0;
        var transitions = 0;
        var counter = new KeepAwakeCounter(on => { awake = on ? 1 : 0; Interlocked.Increment(ref transitions); });

        Parallel.For(0, 1000, _ => { using (counter.Acquire()) { } });

        Assert.Equal(0, counter.Count);
        Assert.Equal(0, awake);
        Assert.True(transitions % 2 == 0);
    }

    [Fact]
    public void Static_acquire_returns_a_disposable_handle()
    {
        using var handle = KeepAwake.Acquire();
        Assert.NotNull(handle);
    }
}
