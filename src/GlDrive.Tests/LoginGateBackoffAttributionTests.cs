using GlDrive.Spread;
using Xunit;
using State = GlDrive.Spread.BorrowStarvationDiagnoser.PoolState;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.55 — the borrow-timeout message was misattributing a LOCAL stall to the
/// remote server, because <c>FtpConnectionPool</c> stored two structurally different
/// causes in one field (<c>_refusedUntilTicks</c>):
///
///   * a real BNC refusal        -> 90s cooldown
///   * a login-GATE permit miss  -> 20s backoff
///
/// <c>IsInCooldown</c> read that shared field, so the diagnoser's first branch —
/// "in cooldown (server refused a recent login)" — fired for both. Production
/// evidence on 2026-08-10/11: 1 real refusal against 1,009 gate backoffs, and
/// 357 of 359 borrow timeouts in a single day naming the server for a stall it
/// had no part in. The diagnoser's own "account login cap" branch, written for
/// exactly this case, fired twice.
///
/// This is the v3.10.48 defect returning through the STATE the message reads
/// rather than the message itself, so these tests pin the attribution at the
/// state layer, not just the wording.
/// </summary>
public class LoginGateBackoffAttributionTests
{
    private static readonly State GateBackoff = new(
        IsInCooldown: false, IsExhausted: false, Created: 1, Active: 1, MaxSize: 3,
        IsInLoginGateBackoff: true);

    private static readonly State ServerRefused = new(
        IsInCooldown: true, IsExhausted: false, Created: 1, Active: 1, MaxSize: 3,
        IsInLoginGateBackoff: false);

    private static readonly State Healthy = new(
        IsInCooldown: false, IsExhausted: false, Created: 3, Active: 1, MaxSize: 3);

    [Fact]
    public void GateBackoff_DoesNotBlameTheServer()
    {
        // The whole defect in one assertion: 357/359 timeouts said this.
        var msg = BorrowStarvationDiagnoser.Describe(GateBackoff);

        Assert.DoesNotContain("server refused", msg);
        Assert.Contains("login-gate backoff", msg);
    }

    [Fact]
    public void GateBackoff_NamesLocalContentionAsTheCause()
    {
        var msg = BorrowStarvationDiagnoser.Describe(GateBackoff);

        Assert.Contains("local contention", msg);
        Assert.Contains("created=1/3", msg);
    }

    [Fact]
    public void GateBackoff_DoesNotAdviseKillingGhosts()
    {
        // Same trap as v3.10.48: !username kills the operator's own live logins,
        // which is precisely the resource already in short supply here.
        var msg = BorrowStarvationDiagnoser.Describe(GateBackoff);

        Assert.DoesNotContain("!username", msg);
        Assert.DoesNotContain("ghost", msg);
    }

    [Fact]
    public void RealServerRefusal_StillReportsAsARefusal()
    {
        // The fix must not swing the other way — an actual refusal is still the
        // server's doing and the reader needs to know the 90s window applies.
        var msg = BorrowStarvationDiagnoser.Describe(ServerRefused);

        Assert.Contains("server refused", msg);
        Assert.DoesNotContain("login-gate backoff", msg);
    }

    [Fact]
    public void RefusalWins_WhenBothFlagsAreSet()
    {
        // A pool can be refused AND gate-capped; the refusal is the harder
        // constraint (90s vs 20s) and the one that needs operator attention.
        var both = new State(
            IsInCooldown: true, IsExhausted: false, Created: 1, Active: 1, MaxSize: 3,
            IsInLoginGateBackoff: true);

        Assert.Contains("server refused", BorrowStarvationDiagnoser.Describe(both));
    }

    [Fact]
    public void GateBackoff_CountsAsStarved()
    {
        // Behaviour parity with the pre-split code: the pool will not open a new
        // connection, so it is a legitimate culprit for the timeout. If this
        // regressed, the paired Describe would start reporting "neither pool is
        // starved" for the single most common real stall.
        Assert.True(BorrowStarvationDiagnoser.IsStarved(GateBackoff));
    }

    [Fact]
    public void GateBackedOffSide_IsNamedInAPairedTimeout()
    {
        var msg = BorrowStarvationDiagnoser.Describe("superbnc", Healthy, "SYN", GateBackoff);

        Assert.Contains("SYN", msg);
        Assert.DoesNotContain("superbnc", msg);
        Assert.DoesNotContain("server refused", msg);
    }

    [Fact]
    public void DefaultedState_BehavesExactlyAsBeforeTheSplit()
    {
        // The new field is optional so existing 5-arg construction still compiles;
        // it must also still MEAN "no gate backoff" rather than silently starving
        // every pool that was built the old way.
        var old = new State(
            IsInCooldown: false, IsExhausted: false, Created: 3, Active: 1, MaxSize: 3);

        Assert.False(old.IsInLoginGateBackoff);
        Assert.False(BorrowStarvationDiagnoser.IsStarved(old));
    }

    [Fact]
    public void GateBackoff_CarriesTheCountersBehindItsVerdict()
    {
        // Same invariant the v3.10.48 suite pinned: a cause the next reader
        // cannot check against numbers is how this class of bug survives.
        var msg = BorrowStarvationDiagnoser.Describe(GateBackoff);

        Assert.Contains("created=", msg);
        Assert.Contains("active=", msg);
    }
}
