using GlDrive.Spread;
using Xunit;
using State = GlDrive.Spread.BorrowStarvationDiagnoser.PoolState;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.48 — the FXP borrow-timeout warning asserted "pool exhausted, server may
/// have ghost connections (try !username login to kill them)" for every timeout,
/// including the 194/day superbnc -> SYN cases whose own neighbouring log lines
/// read "created=2, max=3" and "Account login cap reached". These lock in that the
/// reported cause now follows the counters.
/// </summary>
public class BorrowStarvationDiagnoserTests
{
    // created=2 of max=3 — the exact shape logged on 2026-08-04 03:40:03.
    private static readonly State LoginCapped = new(
        IsInCooldown: false, IsExhausted: false, Created: 2, Active: 2, MaxSize: 3);

    private static readonly State Empty = new(
        IsInCooldown: false, IsExhausted: true, Created: 0, Active: 0, MaxSize: 3);

    private static readonly State AtCapacity = new(
        IsInCooldown: false, IsExhausted: false, Created: 3, Active: 3, MaxSize: 3);

    private static readonly State Cooling = new(
        IsInCooldown: true, IsExhausted: false, Created: 1, Active: 0, MaxSize: 3);

    private static readonly State Healthy = new(
        IsInCooldown: false, IsExhausted: false, Created: 3, Active: 1, MaxSize: 3);

    [Fact]
    public void LoginCapStall_IsNotReportedAsExhaustion()
    {
        var msg = BorrowStarvationDiagnoser.Describe(LoginCapped);

        Assert.Contains("login cap", msg);
        Assert.Contains("created=2/3", msg);
        Assert.DoesNotContain("empty", msg);
    }

    [Fact]
    public void LoginCapStall_DoesNotAdviseKillingGhosts()
    {
        // The regression that mattered: !username on this advice kills the
        // operator's own live sessions and leaves the contention in place.
        Assert.DoesNotContain("!username", BorrowStarvationDiagnoser.Describe(LoginCapped));
        Assert.DoesNotContain("ghost", BorrowStarvationDiagnoser.Describe(LoginCapped));
    }

    [Fact]
    public void GenuinelyEmptyPool_StillSuggestsGhostSessions()
    {
        var msg = BorrowStarvationDiagnoser.Describe(Empty);

        Assert.Contains("pool empty", msg);
        Assert.Contains("!username", msg);
    }

    [Fact]
    public void ExhaustedFlagAndZeroCounters_AgreeOnEmptyVerdict()
    {
        // IsExhausted is derived (IsConnected && created<=0 && active<=0); a pool
        // reporting the counters without the flag must not fall through to the
        // login-cap branch and claim a permit shortage that cannot exist at 0/N.
        var countersOnly = new State(
            IsInCooldown: false, IsExhausted: false, Created: 0, Active: 0, MaxSize: 3);

        Assert.Contains("pool empty", BorrowStarvationDiagnoser.Describe(countersOnly));
    }

    [Fact]
    public void FullBusyPool_IsReportedAsConcurrencyNotFault()
    {
        var msg = BorrowStarvationDiagnoser.Describe(AtCapacity);

        Assert.Contains("at capacity", msg);
        Assert.Contains("not a fault", msg);
        Assert.DoesNotContain("!username", msg);
    }

    [Fact]
    public void CooldownWins_OverEveryOtherCause()
    {
        // A cooling pool clears on a timer; naming any other cause sends the
        // reader after a problem that is already resolving itself.
        Assert.Contains("cooldown", BorrowStarvationDiagnoser.Describe(Cooling));

        var coolingAndEmpty = new State(
            IsInCooldown: true, IsExhausted: true, Created: 0, Active: 0, MaxSize: 3);
        Assert.Contains("cooldown", BorrowStarvationDiagnoser.Describe(coolingAndEmpty));
        Assert.DoesNotContain("!username", BorrowStarvationDiagnoser.Describe(coolingAndEmpty));
    }

    [Fact]
    public void OnlyTheStarvedSideIsNamed()
    {
        // superbnc had connections to spare; SYN did not. Naming superbnc would
        // repeat the original defect in a new place.
        var msg = BorrowStarvationDiagnoser.Describe("superbnc", Healthy, "SYN", LoginCapped);

        Assert.Contains("SYN", msg);
        Assert.DoesNotContain("superbnc", msg);
    }

    [Fact]
    public void BothStarvedSides_AreBothNamed()
    {
        var msg = BorrowStarvationDiagnoser.Describe("superbnc", Empty, "SYN", LoginCapped);

        Assert.Contains("superbnc", msg);
        Assert.Contains("SYN", msg);
    }

    [Fact]
    public void NeitherStarved_SaysSoInsteadOfInventingACulprit()
    {
        var msg = BorrowStarvationDiagnoser.Describe("superbnc", Healthy, "SYN", Healthy);

        Assert.Contains("neither pool is starved", msg);
        Assert.Contains("superbnc", msg);
        Assert.Contains("SYN", msg);
    }

    [Theory]
    [InlineData(true, false, 1, 0, 3)]   // cooldown
    [InlineData(false, true, 0, 0, 3)]   // empty
    [InlineData(false, false, 3, 3, 3)]  // at capacity, all busy
    [InlineData(false, false, 2, 2, 3)]  // login capped, all busy
    public void StarvedStates_AreRecognisedAsStarved(
        bool cooldown, bool exhausted, int created, int active, int max)
    {
        Assert.True(BorrowStarvationDiagnoser.IsStarved(
            new State(cooldown, exhausted, created, active, max)));
    }

    [Fact]
    public void PoolWithAnIdleConnection_IsNotStarved()
    {
        Assert.False(BorrowStarvationDiagnoser.IsStarved(Healthy));
    }

    [Fact]
    public void EveryCause_NamesTheCountersItWasDerivedFrom()
    {
        // The lesson from the original bug: a verdict printed without the numbers
        // behind it cannot be checked by the next reader.
        foreach (var s in new[] { LoginCapped, Empty, AtCapacity, Cooling, Healthy })
        {
            var msg = BorrowStarvationDiagnoser.Describe(s);
            Assert.True(
                msg.Contains("created=") || msg.Contains("connection(s) busy"),
                $"cause text carried no counters: {msg}");
        }
    }
}
