namespace GlDrive.Spread;

internal enum FxpCancellation
{
    /// <summary>The race itself stopped (user stop, race ceiling, dispose).</summary>
    JobCancelled,
    /// <summary>A deadline fired after TYPE/PASV/CPSV/STOR/RETR began — a real transfer failure.</summary>
    TransferDeadline,
    /// <summary>The per-transfer ceiling fired while still waiting on gates/borrow.</summary>
    SetupDeadline,
    /// <summary>The 30s borrow timeout fired — pool congestion, not a file problem.</summary>
    BorrowTimeout,
}

/// <summary>
/// Failure policy for the boundary between acquiring an FXP pair and issuing FTP
/// transfer commands. A connection can only have a corrupt GnuTLS/control-channel
/// state after protocol begins; a peer borrowed before the other pool fails is clean.
/// </summary>
internal static class FxpFailurePolicy
{
    internal static bool ShouldPoisonPeers(bool transferProtocolStarted)
        => transferProtocolStarted;

    /// <summary>
    /// Hard ceiling for one file transfer. The spread loop cancels the transfer's
    /// token at this point; that is the transfer's deadline, not the job's.
    /// </summary>
    internal static TimeSpan TransferCeiling(int transferTimeoutSeconds)
        => TimeSpan.FromSeconds(transferTimeoutSeconds > 0 ? transferTimeoutSeconds * 3 : 180);

    /// <summary>
    /// Attribute an OperationCanceledException from ExecuteTransfer. The transfer token
    /// is linked to the job token, so "transfer token cancelled" alone cannot tell a
    /// stopped race from a transfer that hit its own ceiling — that ambiguity reported
    /// every ceiling hit as "FXP job cancelled mid-transfer" and never counted it.
    /// </summary>
    internal static FxpCancellation ClassifyCancellation(
        bool jobCancelled, bool transferTokenCancelled, bool transferProtocolStarted)
    {
        if (jobCancelled) return FxpCancellation.JobCancelled;
        // After protocol start any deadline (our ceiling or FxpTransfer's own relay
        // timeout) leaves the sessions mid-command: never a pristine borrow timeout.
        if (transferProtocolStarted) return FxpCancellation.TransferDeadline;
        return transferTokenCancelled ? FxpCancellation.SetupDeadline : FxpCancellation.BorrowTimeout;
    }

    /// <summary>
    /// Setup failures already accounted for by pool/gate diagnostics. They are retried
    /// by normal scoring and should not be duplicated as transfer-level warnings.
    /// </summary>
    internal static bool IsExpectedSetupDeferral(System.Exception? ex)
    {
        if (ex == null) return false;
        if (ScanFailureClassifier.IsContention(ex)) return true;
        if (ex is System.InvalidOperationException
            && ex.Message.Contains("Pool exhausted", System.StringComparison.OrdinalIgnoreCase))
            return true;
        return IsExpectedSetupDeferral(ex.InnerException);
    }
}
