using GlDrive.Ftp;

namespace GlDrive.Services;

/// <summary>A diagnostic snapshot, never an admission decision or permission to retry.</summary>
public sealed record WaitingState(string ReasonCode, string LimitingConstraint,
    DateTimeOffset? NextCheckAtUtc = null, DateTimeOffset? EstimatedReadyAtUtc = null,
    int? QueuePosition = null)
{
    public bool CanCheckNow => false;
    public string CheckNowUnavailableReason => "Automatic checks retain the existing queue, account holds and site retry schedule.";
    public string Description => $"{ReasonCode}: {LimitingConstraint}" +
        (QueuePosition is { } position ? $" · queue #{position}" : "") +
        (NextCheckAtUtc is { } next ? $" · next check {next.ToLocalTime():ddd HH:mm:ss}" :
            ReasonCode is "site-disabled" or "user-paused" ? " · user action required" : " · next check on capacity/state change") +
        (EstimatedReadyAtUtc is { } eta ? $" · estimated ready {eta.ToLocalTime():HH:mm}" : " · ETA unknown");

    internal static WaitingState Site(bool enabled, bool connected, bool authHeld, DateTimeOffset? authRetry,
        SiteHealthSnapshot health, bool healthEnabled, LoginBudgetSnapshot budget,
        DateTimeOffset? remountRetry, DateTimeOffset now, string capacity)
    {
        if (!enabled) return new("site-disabled", "Server is disabled");
        if (connected && !authHeld && !(budget.Enabled && budget.RetryAtUtc > now) &&
            !(healthEnabled && health.NextRetryUtc > now)) return new("ready", capacity);
        var deadlines = new[] { authHeld ? authRetry : null, budget.Enabled ? budget.RetryAtUtc : null,
            healthEnabled ? health.NextRetryUtc : null, remountRetry }.Where(d => d > now).ToArray();
        var next = deadlines.Length == 0 ? (DateTimeOffset?)null : deadlines.Max();
        var code = authHeld ? "authentication-hold" : budget.Enabled && budget.RetryAtUtc > now
            ? budget.HoldReason == LoginHoldReason.BncCooldown ? "bnc-cooldown" : "login-budget-hold"
            : healthEnabled && health.NextRetryUtc > now ? "site-health-backoff" : "site-retry";
        var reason = authHeld ? "Authentication is held" : code == "login-budget-hold"
            ? $"Account admission: {budget.HoldReason}; {budget.AttemptsInWindow}/{budget.Limit} login attempts, {budget.ReservedGhostSlots} reserved for recovery"
            : code == "bnc-cooldown" ? "Account BNC cooldown" : healthEnabled ? health.Message : "Awaiting the existing reconnect scheduler";
        return new(code, reason + "; " + capacity + (connected ? "; existing pooled connections remain usable" : ""), next);
    }
}
