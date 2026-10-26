using System.Reflection;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class BorrowTimeoutAttributionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_borrowed_peer_is_not_blamed_for_its_own_active_connection(bool sourceAcquired)
    {
        var empty = new BorrowStarvationDiagnoser.PoolState(false, true, 0, 0, 1);
        var held = new BorrowStarvationDiagnoser.PoolState(false, false, 1, 1, 1);
        var message = BorrowStarvationDiagnoser.DescribeBorrowFailure(
            "source", sourceAcquired ? held : empty, sourceAcquired,
            "destination", sourceAcquired ? empty : held, !sourceAcquired);

        Assert.Contains(sourceAcquired ? "destination" : "source", message);
        Assert.DoesNotContain(sourceAcquired ? "source" : "destination", message);
        Assert.Contains("pool empty", message);
        Assert.DoesNotContain("!username", message);
    }

    [Fact]
    public void Two_failed_borrows_retain_both_endpoint_diagnostics()
    {
        var empty = new BorrowStarvationDiagnoser.PoolState(false, true, 0, 0, 1);
        var message = BorrowStarvationDiagnoser.DescribeBorrowFailure("source", empty, false,
            "destination", empty, false);
        Assert.Contains("source", message);
        Assert.Contains("destination", message);
    }

    [Fact]
    public void Two_successful_borrows_do_not_invent_a_pool_failure()
    {
        var held = new BorrowStarvationDiagnoser.PoolState(false, false, 1, 1, 1);
        var message = BorrowStarvationDiagnoser.DescribeBorrowFailure("source", held, true,
            "destination", held, true);
        Assert.Contains("both pool connections were acquired", message);
    }

    [Fact]
    public void Unborrowed_or_connecting_capacity_is_not_reported_as_fully_busy()
    {
        var state = new BorrowStarvationDiagnoser.PoolState(false, false, 1, 0, 1);
        Assert.Contains("unborrowed or connecting capacity", BorrowStarvationDiagnoser.Describe(state));
        Assert.DoesNotContain("all 1 connection(s) busy", BorrowStarvationDiagnoser.Describe(state));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transfer_diagnostic_uses_the_account_gate_capacity(bool priority)
    {
        var gate = new ServerLoginGate("diagnostic", limit: 2, maxLimit: 2, reserved: 1);
        await using var pool = new FtpConnectionPool(null!, 3, gate, priority);
        var snapshot = typeof(SpreadJob).GetMethod("SnapshotPool", BindingFlags.NonPublic | BindingFlags.Static)!;
        var state = (BorrowStarvationDiagnoser.PoolState)snapshot.Invoke(null, [pool])!;

        Assert.Equal(1, state.MaxSize);
        var message = BorrowStarvationDiagnoser.Describe(state with { Created = 1, Active = 1 });
        Assert.Contains("at capacity", message);
        Assert.DoesNotContain("another pool", message);
    }
}
