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

        /// <summary>A genuine connect fault — timeout, TLS failure, reset.</summary>
        ConnectFault,
    }

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
