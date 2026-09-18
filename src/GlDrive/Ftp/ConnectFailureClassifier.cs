namespace GlDrive.Ftp;

/// <summary>
/// Decides what a failed new-connection attempt inside <c>FtpConnectionPool.Borrow</c>
/// actually MEANS, before the pool acts on it.
///
/// Borrow's failure handler was a bare <c>catch (Exception ex)</c>, so it could not
/// tell a finding about the server or the account apart from the caller simply
/// running out of patience. Every Borrow call site wraps the token in a deadline
/// (2s in the media streamer, 5s in FtpOperations, 15–45s across the spread engine),
/// and when that deadline expires mid-connect the resulting
/// <see cref="System.OperationCanceledException"/> was processed as though the
/// connection had failed. Three separate consequences followed, all wrong:
///
///   * the once-per-episode ghost-kill budget was burned by a request that never
///     reached the BNC — the CompareExchange sets the flag before KillGhosts runs,
///     and KillGhosts was then handed the already-cancelled token. KillGhosts does
///     not rethrow — its catch-all swallows the failure and logs only at Debug —
///     so the budget was spent and nothing came back to show for it. On 2026-08-13
///     that produced ZERO ghost kills against 287 login-cap events: the mitigation
///     was silently disabled all day.
///   * the borrow then fell through to <c>_created &lt;= 0</c> and reported
///     "Pool exhausted: all connections discarded and new connections failed" —
///     669 of the day's 703 exhaustion throws had a cancellation immediately
///     behind them. The operator was sent to look at a pool that was fine.
///   * each one logged a full stack at Information: 2,545 of them, roughly 25,000
///     lines, evicting real history from a rolling log.
///
/// A caller abandoning its own borrow teaches us nothing about the server or the
/// account, so it must not arm a cooldown, must not spend a mitigation, and must
/// not be counted as exhaustion. It is the one verdict here that is not a finding.
/// </summary>
public static class ConnectFailureClassifier
{
    public enum ConnectFailure
    {
        /// <summary>The caller's own borrow deadline expired. Not a finding.</summary>
        CallerAbandoned,

        /// <summary>TCP-level refusal, or a 530 that persisted past our one ghost kill.</summary>
        ServerRefused,

        /// <summary>The BNC's reply explicitly stated a simultaneous-login limit.</summary>
        BncLoginLimit,

        /// <summary>The local account login gate had no permit to hand out.</summary>
        AccountLoginCapped,

        /// <summary>
        /// The attempt never reached the server at all: DNS did not resolve the host,
        /// or the network/host was unreachable. Defined by the TRANSPORT-LAYER outcome,
        /// not by any reply — because there was no reply.
        /// </summary>
        HostUnreachable,

        /// <summary>A genuine connect fault — timeout, TLS failure, reset.</summary>
        ConnectFault,
    }

    /// <summary>
    /// Socket outcomes that mean the attempt never reached the server.
    ///
    /// Membership is decided by ONE property — did a packet ever get to the far end? —
    /// not by enumerating codes we have happened to observe. Name resolution failures
    /// and unreachable-network/host errors qualify; <c>ConnectionRefused</c> (10061),
    /// <c>ConnectionReset</c> (10054) and <c>ConnectionAborted</c> (10053) deliberately
    /// do NOT, because in every one of those a host answered — that is a statement about
    /// the server, and the existing refusal/fault verdicts own it.
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<System.Net.Sockets.SocketError> Unreachable =
        new()
        {
            System.Net.Sockets.SocketError.HostNotFound,        // 11001 — DNS: no such host
            System.Net.Sockets.SocketError.TryAgain,            // 11002 — DNS: non-authoritative, retry
            System.Net.Sockets.SocketError.NoRecovery,          // 11003 — DNS: non-recoverable
            System.Net.Sockets.SocketError.NoData,              // 11004 — DNS: valid name, no address
            System.Net.Sockets.SocketError.NetworkDown,         // 10050
            System.Net.Sockets.SocketError.NetworkUnreachable,  // 10051
            System.Net.Sockets.SocketError.HostUnreachable,     // 10065
        };

    /// <param name="ex">The exception the connect attempt threw.</param>
    /// <param name="callerCancelled">Whether the token the caller passed to Borrow is cancelled.</param>
    /// <param name="bncStatedLoginLimit">Whether the reply carried the BNC's login-limit text.</param>
    /// <param name="ghostKillAlreadySpent">Whether this pressure episode already used its ghost kill.</param>
    public static ConnectFailure Classify(
        System.Exception ex,
        bool callerCancelled,
        bool bncStatedLoginLimit,
        bool ghostKillAlreadySpent)
    {
        // Ordered first deliberately. A cancellation can carry any inner message —
        // including one that looks like a refusal — and none of it was observed,
        // because the attempt was torn down before it concluded. Walks the chain
        // like IndicatesRefusal below: a caller-cancelled attempt often surfaces as
        // some wrapper (e.g. TaskCanceledException from an awaited library call)
        // around the real OperationCanceledException, not the exception itself.
        if (callerCancelled && IsOrWrapsCancellation(ex))
            return ConnectFailure.CallerAbandoned;

        // The pre-classifier handler ORed three things: a top-level "actively
        // refused" match, "target machine actively refused" (a strict substring of
        // the first — matching it always matches the first too, so folding it in
        // here changed no behavior), and a login limit that survived our one ghost
        // kill. That second clause is why this isn't literally verbatim, but it was
        // never a distinct check to begin with.
        //
        // IndicatesRefusal now also walks the InnerException chain — the original
        // read only the top-level ex.Message, so a wrapped SocketException carrying
        // the refusal text missed its 90s BNC cooldown. That's the expensive
        // direction to get wrong: the real BNC lockout runs ~2 hours.
        // Before any verdict that describes the SERVER or the ACCOUNT. If the packet
        // never arrived, nothing about logins, ghost sessions or BNC state was
        // observed, so none of those verdicts may be asserted. On 2026-09-17 the box
        // lost DNS and its LAN route at 06:05; every one of the 14,219 resulting
        // attempts landed in ConnectFault, which arms no backoff — so the pool
        // re-attempted at ~32/second for 105 minutes and wrote ~14,200 identical
        // stacks at Information, rolling the log three times. The classifier's own
        // header already described that exact damage from 2026-08-13; the fix then
        // named a single non-finding and let everything else fall through here.
        if (IsUnreachable(ex))
            return ConnectFailure.HostUnreachable;

        if (IndicatesRefusal(ex) || (bncStatedLoginLimit && ghostKillAlreadySpent))
            return ConnectFailure.ServerRefused;

        if (bncStatedLoginLimit)
            return ConnectFailure.BncLoginLimit;

        if (ex is System.InvalidOperationException
            && ex.Message?.Contains("login cap reached", System.StringComparison.OrdinalIgnoreCase) == true)
            return ConnectFailure.AccountLoginCapped;

        return ConnectFailure.ConnectFault;
    }

    /// <summary>
    /// Whether the verdict says something about the server or the account. Everything
    /// the pool does in its failure handler — arming a cooldown, spending the ghost
    /// kill, counting an exhaustion, logging a stack — is gated on this being true.
    /// </summary>
    public static bool IsRealFinding(ConnectFailure f) => f != ConnectFailure.CallerAbandoned;

    /// <summary>
    /// Walk the exception chain for a socket outcome in <see cref="Unreachable"/>.
    /// Keys on <see cref="System.Net.Sockets.SocketException.SocketErrorCode"/> rather
    /// than message text: the text is localized by the OS, so a substring match would
    /// silently stop working on a non-English machine.
    /// </summary>
    public static bool IsUnreachable(System.Exception? ex)
    {
        if (ex is null) return false;
        if (ex is System.Net.Sockets.SocketException se && Unreachable.Contains(se.SocketErrorCode))
            return true;
        return IsUnreachable(ex.InnerException);
    }

    /// <summary>
    /// Walk the exception chain for a message stating the server actively refused the
    /// connection. Mirrors <c>ScanFailureClassifier.IsContention</c>'s shape — a cause
    /// wrapped by a caller (e.g. a library rethrowing a SocketException inside its own
    /// exception type) is still that cause.
    /// </summary>
    private static bool IndicatesRefusal(System.Exception? ex)
    {
        if (ex is null) return false;
        if (ex.Message?.Contains("actively refused", System.StringComparison.OrdinalIgnoreCase) == true)
            return true;
        return IndicatesRefusal(ex.InnerException);
    }

    /// <summary>
    /// Walk the exception chain for an <see cref="System.OperationCanceledException"/>.
    /// A cancellation thrown deep in FluentFTP/the socket layer often arrives at Borrow
    /// wrapped in another exception type, so checking only the top level would miss it —
    /// the same gap <see cref="IndicatesRefusal"/> had for the refusal text.
    /// </summary>
    private static bool IsOrWrapsCancellation(System.Exception? ex)
    {
        if (ex is null) return false;
        if (ex is System.OperationCanceledException) return true;
        return IsOrWrapsCancellation(ex.InnerException);
    }
}
