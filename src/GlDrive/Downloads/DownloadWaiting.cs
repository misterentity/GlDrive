using GlDrive.Services;

namespace GlDrive.Downloads;

public static class DownloadWaiting
{
    internal static string RetryReason(Exception error) => error switch
    {
        GlDrive.Ftp.FtpLoginHeldException { AuthenticationHold: true } => "authentication-hold",
        GlDrive.Ftp.FtpLoginHeldException { SiteHealthHold: true } => "site-health-backoff",
        GlDrive.Ftp.FtpLoginHeldException { LoginRateHold: true } => "login-budget-hold",
        GlDrive.Services.SiteRetryDeferredException => "site-health-backoff",
        _ => "download-retry"
    };

    public static WaitingState? Describe(DownloadItem item) => item.Status switch
    {
        DownloadStatus.Queued => item.RuntimeWait ?? new WaitingState(
            item.SourceCompletionPending ? "source-incomplete" : "download-queue",
            (!string.IsNullOrWhiteSpace(item.ErrorMessage) ? item.ErrorMessage : item.StatusDetail) ?? (item.SourceCompletionPending
                ? "Source completion has not yet been verified" : "Awaiting a download slot and normal admission")),
        DownloadStatus.Paused => new("user-paused", "Resume is required"),
        _ => null
    };
}
