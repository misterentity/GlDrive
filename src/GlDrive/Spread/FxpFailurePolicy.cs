namespace GlDrive.Spread;

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
