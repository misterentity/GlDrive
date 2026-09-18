using System;
using System.Net.Sockets;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;
using Kind = GlDrive.Ftp.ConnectFailureClassifier.ConnectFailure;
using State = GlDrive.Spread.BorrowStarvationDiagnoser.PoolState;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.123 — on 2026-09-17 the box lost DNS and its LAN route at 06:05. Both of the
/// day's hosts began failing at the TRANSPORT layer: superbnc with SocketError 11001
/// ("No such host is known") and the LAN server with 10065 ("unreachable host"). Every
/// one of those attempts was classified <see cref="Kind.ConnectFault"/>, the catch-all,
/// which arms no backoff and logs a full stack at Information. The consequences:
///
///   * 14,219 connect attempts in 105 minutes — a peak of 1,933/minute (~32/second),
///     with no pause between them, against hosts that could not be resolved at all.
///   * ~120,000 lines of byte-identical stack traces, rolling the log three times in
///     one day (36 MB vs 4.4 MB the day before) and evicting the history needed to
///     diagnose it — the precise damage ConnectFailureClassifier's own header already
///     recorded from 2026-08-13, recurring because that fix named one non-finding and
///     let everything else fall through to the catch-all.
///   * 14,061 copies of "ghost sessions are plausible here, try a !username login",
///     emitted for a hostname DNS could not resolve. A ghost session is a stale login
///     on a server we reached; the advice, if taken, severs the account's own live
///     sessions (the v3.10.105 damage).
///
/// Corpus check before the predicate was written: across 2026-09-15..17 the logs hold
/// 14,237 socket failures of the unreachable class and exactly 4 post-connect faults
/// (10054/10053), with ZERO socket exceptions on either quiet day — so this verdict
/// cannot fire in steady-state operation.
///
/// These pin the CLASSIFICATION and the STATE, not the wording.
/// </summary>
public class HostUnreachableAttributionTests
{
    private static Kind Classify(Exception ex) =>
        ConnectFailureClassifier.Classify(ex, callerCancelled: false,
            bncStatedLoginLimit: false, ghostKillAlreadySpent: false);

    // The two codes actually observed during the outage, plus the rest of the class.
    [Theory]
    [InlineData(SocketError.HostNotFound)]       // 11001 — superbnc, 7,127 times
    [InlineData(SocketError.HostUnreachable)]    // 10065 — LAN host, 7,110 times
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.NetworkDown)]
    [InlineData(SocketError.TryAgain)]
    [InlineData(SocketError.NoRecovery)]
    [InlineData(SocketError.NoData)]
    public void TransportFailureIsNotAGenericConnectFault(SocketError code)
        => Assert.Equal(Kind.HostUnreachable, Classify(new SocketException((int)code)));

    // The discriminator has to hold in the other direction too, or the new verdict
    // would swallow real findings about the server and park a pool that should retry.
    [Theory]
    [InlineData(SocketError.ConnectionReset)]    // 10054 — a host answered, then reset
    [InlineData(SocketError.ConnectionAborted)]  // 10053
    public void AFailureAfterTheHostAnsweredStaysAConnectFault(SocketError code)
        => Assert.Equal(Kind.ConnectFault, Classify(new SocketException((int)code)));

    [Fact]
    public void ConnectionRefusedIsStillAServerFindingNotUnreachable()
        => Assert.Equal(Kind.ServerRefused,
            Classify(new Exception("No connection could be made because the target machine actively refused it")));

    // FluentFTP rethrows the socket error inside its own exception type, exactly as in
    // the production stacks (FtpSocketStream.ConnectAsync -> AsyncFtpClient.Connect).
    [Fact]
    public void UnreachableIsFoundThroughAWrappingException()
        => Assert.Equal(Kind.HostUnreachable,
            Classify(new InvalidOperationException("connect failed",
                new SocketException((int)SocketError.HostNotFound))));

    // The caller's own deadline still outranks everything: an attempt torn down before
    // it concluded observed nothing, including whether the host was reachable.
    [Fact]
    public void CallerCancellationStillOutranksTheTransportVerdict()
        => Assert.Equal(Kind.CallerAbandoned,
            ConnectFailureClassifier.Classify(
                new OperationCanceledException("deadline",
                    new SocketException((int)SocketError.HostNotFound)),
                callerCancelled: true, bncStatedLoginLimit: false, ghostKillAlreadySpent: false));

    // It IS a finding — it must arm the backoff and be reported, unlike CallerAbandoned.
    [Fact]
    public void UnreachableIsARealFindingSoTheHandlerActsOnIt()
        => Assert.True(ConnectFailureClassifier.IsRealFinding(Kind.HostUnreachable));

    // ---- the diagnosis the operator reads ----

    // created=0 is what an emptied pool AND a never-reached host both look like. The
    // whole defect was reading the first from state that could equally mean the second.
    [Fact]
    public void UnreachableHostIsNotDiagnosedAsGhostSessions()
    {
        var text = BorrowStarvationDiagnoser.Describe(
            new State(IsInCooldown: false, IsExhausted: true, Created: 0, Active: 0,
                      MaxSize: 3, IsInLoginGateBackoff: false, IsHostUnreachable: true));

        Assert.DoesNotContain("ghost", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("!username", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unreachable", text, StringComparison.OrdinalIgnoreCase);
    }

    // ...and the ghost advice must survive for the case it was written for, or this
    // fix would just have deleted a true diagnosis along with the false one.
    [Fact]
    public void AGenuinelyEmptiedPoolStillGetsTheGhostAdvice()
    {
        var text = BorrowStarvationDiagnoser.Describe(
            new State(IsInCooldown: false, IsExhausted: true, Created: 0, Active: 0,
                      MaxSize: 3, IsInLoginGateBackoff: false, IsHostUnreachable: false));

        Assert.Contains("ghost", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnreachableHostCountsAsStarvedSoTheSideIsNamed()
        => Assert.True(BorrowStarvationDiagnoser.IsStarved(
            new State(IsInCooldown: false, IsExhausted: false, Created: 0, Active: 0,
                      MaxSize: 3, IsInLoginGateBackoff: false, IsHostUnreachable: true)));
}
