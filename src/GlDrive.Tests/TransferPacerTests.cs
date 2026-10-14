using GlDrive.Downloads.Segmented;
using Xunit;

namespace GlDrive.Tests;

public class TransferPacerTests
{
    /// <summary>Virtual clock: delays advance time instantly instead of sleeping.</summary>
    private sealed class FakeTime
    {
        public TimeSpan Now;
        public TimeSpan Waited;
        public TimeSpan Clock() => Now;
        public Task Delay(TimeSpan d, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Now += d;
            Waited += d;
            return Task.CompletedTask;
        }
    }

    private static TransferPacer Pacer(FakeTime t, Func<long> total, Func<string?, long> net) =>
        new(total, net, t.Clock, t.Delay);

    private static async Task Send(ITransferPacer pacer, string? net, long totalBytes, int chunk = 64 * 1024)
    {
        for (long sent = 0; sent < totalBytes; sent += chunk)
            await pacer.WaitAsync(net, chunk, CancellationToken.None);
    }

    [Fact]
    public async Task Unlimited_never_waits()
    {
        var t = new FakeTime();
        await Send(Pacer(t, () => 0, _ => 0), "eth", 100L * 1024 * 1024);
        Assert.Equal(TimeSpan.Zero, t.Waited);
    }

    [Fact]
    public async Task Total_limit_throttles_to_the_rate_after_one_second_of_burst()
    {
        var t = new FakeTime();
        const long rate = 1024 * 1024;
        await Send(Pacer(t, () => rate, _ => 0), "eth", 10 * rate);
        // 10 MB at 1 MB/s with a 1 MB burst ≈ 9 s.
        Assert.InRange(t.Waited.TotalSeconds, 8.8, 9.2);
    }

    [Fact]
    public async Task Per_network_limit_applies_only_to_that_network()
    {
        var t = new FakeTime();
        const long rate = 512 * 1024;
        var pacer = Pacer(t, () => 0, id => id == "tether" ? rate : 0);

        await Send(pacer, "eth", 20 * rate);
        Assert.Equal(TimeSpan.Zero, t.Waited);

        await Send(pacer, "tether", 5 * rate);
        Assert.InRange(t.Waited.TotalSeconds, 3.8, 4.2);
    }

    [Fact]
    public async Task Null_network_is_the_default_network()
    {
        var t = new FakeTime();
        string? seen = "unset";
        var pacer = Pacer(t, () => 0, id => { seen = id; return 0; });
        await pacer.WaitAsync(null, 10, CancellationToken.None);
        Assert.Equal(NetworkPath.DefaultId, seen);
    }

    [Fact]
    public async Task Limit_change_takes_effect_on_the_next_call()
    {
        var t = new FakeTime();
        long rate = 1024 * 1024;
        var pacer = Pacer(t, () => rate, _ => 0);

        await Send(pacer, "eth", 3 * rate);
        var before = t.Waited;
        rate = 0; // slow mode off
        await Send(pacer, "eth", 50 * 1024 * 1024);
        Assert.Equal(before, t.Waited);
    }

    [Fact]
    public async Task Each_delay_is_capped_at_one_second()
    {
        var t = new FakeTime();
        var slices = new List<TimeSpan>();
        var pacer = new TransferPacer(() => 1024, _ => 0, t.Clock,
            (d, ct) => { slices.Add(d); return t.Delay(d, ct); });

        await pacer.WaitAsync("eth", 10 * 1024, CancellationToken.None);
        Assert.All(slices, s => Assert.True(s <= TimeSpan.FromSeconds(1)));
        Assert.InRange(t.Waited.TotalSeconds, 8.9, 9.1);
    }

    [Fact]
    public async Task Cancellation_is_honored()
    {
        var t = new FakeTime();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var pacer = Pacer(t, () => 1024, _ => 0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pacer.WaitAsync("eth", 1024 * 1024, cts.Token).AsTask());
    }

    [Fact]
    public async Task Composite_applies_every_pacer()
    {
        var t = new FakeTime();
        var total = Pacer(t, () => 1024 * 1024, _ => 0);
        var server = Pacer(t, () => 256 * 1024, _ => 0);
        var pacer = new CompositePacer(total, null, server);

        await Send(pacer, "eth", 1024 * 1024);
        // The tighter 256 KB/s limit dominates: 1 MB with 256 KB burst ≈ 3 s.
        Assert.InRange(t.Waited.TotalSeconds, 2.8, 3.2);
    }
}
