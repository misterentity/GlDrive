using System;
using GlDrive.Ftp;
using Xunit;
using Kind = GlDrive.Ftp.ConnectFailureClassifier.ConnectFailure;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.59 — <c>FtpConnectionPool.Borrow</c> could not distinguish a finding about
/// the server from the caller running out of patience, because its failure handler
/// was a bare <c>catch (Exception ex)</c>. Every Borrow call site bounds the token
/// (2s..45s), so an expired caller deadline arrived as an
/// <see cref="OperationCanceledException"/> and was processed as a connection failure.
///
/// Production evidence, 2026-08-13 (one day, one machine):
///   * 3,651 "Pool: new connection failed" — 2,545 of them cancellations.
///   * 703 "Pool exhausted: all connections discarded"; 669 had a cancellation
///     immediately behind them, i.e. the diagnosis was wrong ~95% of the time.
///   * 0 ghost kills against 287 login-cap events — the once-per-episode budget
///     was burned by cancellations that never reached the BNC.
///
/// These tests pin the classification, not the wording, so the distinction survives
/// a rewrite of the log lines.
/// </summary>
public class ConnectFailureClassifierTests
{
    private static Kind Classify(
        Exception ex,
        bool callerCancelled = false,
        bool bncStatedLoginLimit = false,
        bool ghostKillAlreadySpent = false)
        => ConnectFailureClassifier.Classify(ex, callerCancelled, bncStatedLoginLimit, ghostKillAlreadySpent);

    [Fact]
    public void ExpiredCallerDeadline_IsNotAConnectionFailure()
    {
        // The whole defect in one assertion: 2,545 of these a day were treated
        // as though the server had failed to accept a connection.
        Assert.Equal(
            Kind.CallerAbandoned,
            Classify(new OperationCanceledException(), callerCancelled: true));
    }

    [Fact]
    public void CallerAbandonment_IsTheOnlyVerdictThatIsNotAFinding()
    {
        Assert.False(ConnectFailureClassifier.IsRealFinding(Kind.CallerAbandoned));

        foreach (var real in new[] { Kind.ServerRefused, Kind.BncLoginLimit, Kind.AccountLoginCapped, Kind.ConnectFault })
            Assert.True(ConnectFailureClassifier.IsRealFinding(real));
    }

    [Fact]
    public void CallerAbandonment_OutranksTextThatLooksLikeARefusal()
    {
        // A cancellation tears the attempt down before it concludes, so whatever
        // text it carries was never actually observed from the server. If a
        // refusal could win here, one cancelled borrow would park new connections
        // for the full 90s BNC cooldown on no evidence at all.
        var ex = new OperationCanceledException(
            "No connection could be made because the target machine actively refused it.");

        Assert.Equal(Kind.CallerAbandoned, Classify(ex, callerCancelled: true));
    }

    [Fact]
    public void CallerAbandonment_OutranksAnExhaustedGhostKillBudget()
    {
        // The precise path that produced 0 ghost kills in 24h: had this classified
        // as a real finding, it would keep consuming the once-per-episode budget.
        var v = Classify(
            new OperationCanceledException(),
            callerCancelled: true,
            bncStatedLoginLimit: true,
            ghostKillAlreadySpent: true);

        Assert.Equal(Kind.CallerAbandoned, v);
    }

    [Fact]
    public void CancellationWithoutAnExpiredCallerToken_IsStillARealFault()
    {
        // Not every OperationCanceledException is the caller's. An internal
        // deadline firing while the caller still waits IS something we learned
        // about the connection, and must keep its mitigations.
        var v = Classify(new OperationCanceledException(), callerCancelled: false);

        Assert.NotEqual(Kind.CallerAbandoned, v);
        Assert.True(ConnectFailureClassifier.IsRealFinding(v));
    }

    [Fact]
    public void WrappedCancellation_StillClassifiesAsCallerAbandoned()
    {
        // FluentFTP/the socket layer can rewrap a cancellation inside its own
        // exception type before it reaches Borrow's catch — a top-level-only
        // `ex is OperationCanceledException` check would miss it and treat a
        // caller abandonment as a real connect failure.
        var wrapped = new InvalidOperationException("Connect failed", new OperationCanceledException());

        Assert.Equal(Kind.CallerAbandoned, Classify(wrapped, callerCancelled: true));
    }

    [Fact]
    public void ACancelledTokenDoesNotSwallowAGenuineFailure()
    {
        // The token may cancel a moment after a real fault surfaced. Only an
        // actual cancellation exception is an abandonment; a TimeoutException is
        // a finding no matter what the token says.
        var v = Classify(new TimeoutException("Timed out trying to connect!"), callerCancelled: true);

        Assert.Equal(Kind.ConnectFault, v);
    }

    // ---- regression guards: the pre-existing verdicts must be unchanged ----

    [Fact]
    public void TcpRefusal_IsAServerRefusal()
    {
        var ex = new Exception("No connection could be made because the target machine actively refused it.");

        Assert.Equal(Kind.ServerRefused, Classify(ex));
    }

    [Fact]
    public void WrappedRefusalText_StillClassifiesAsServerRefused()
    {
        // The refusal text arrives on whatever exception the socket layer threw,
        // which a caller (FluentFTP, CpsvDataHelper) can rewrap in its own type.
        // Reading only the top-level ex.Message would miss it and skip the 90s
        // BNC cooldown — the expensive direction, since the real lockout is ~2h.
        var inner = new Exception("No connection could be made because the target machine actively refused it.");
        var wrapped = new InvalidOperationException("Connect failed", inner);

        Assert.Equal(Kind.ServerRefused, Classify(wrapped));
    }

    [Fact]
    public void LoginLimitSurvivingOurGhostKill_IsAServerRefusal()
    {
        // Unchanged rule: we already killed ghosts this episode and the BNC still
        // says no, so the sessions are not ours to reclaim.
        var ex = new Exception("530 Sorry, your account is restricted to 4 simultaneous logins.");

        Assert.Equal(Kind.ServerRefused, Classify(ex, bncStatedLoginLimit: true, ghostKillAlreadySpent: true));
    }

    [Fact]
    public void LoginLimitBeforeAnyGhostKill_IsWorthTheOneGhostKill()
    {
        var ex = new Exception("530 Sorry, your account is restricted to 4 simultaneous logins.");

        Assert.Equal(Kind.BncLoginLimit, Classify(ex, bncStatedLoginLimit: true));
    }

    [Fact]
    public void LocalGatePermitMiss_IsTheAccountCapNotTheServer()
    {
        // v3.10.55's lesson: this is local contention. It must never be reported
        // as anything the remote server did.
        var ex = new InvalidOperationException("Account login cap reached — no login permit available");

        Assert.Equal(Kind.AccountLoginCapped, Classify(ex));
    }

    [Fact]
    public void AnOrdinaryConnectTimeout_IsAPlainConnectFault()
    {
        Assert.Equal(Kind.ConnectFault, Classify(new TimeoutException("Timed out trying to connect!")));
    }
}
