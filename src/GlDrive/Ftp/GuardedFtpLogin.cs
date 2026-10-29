using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Tls;
using Serilog;

namespace GlDrive.Ftp;

/// <summary>The configured FTP account, never a SOCKS proxy or a !username login.</summary>
public readonly record struct FtpLoginAccount(string Host, int Port, string Username)
{
    public static FtpLoginAccount From(ConnectionConfig connection) => new(connection.Host, connection.Port, connection.Username);
    internal string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        ServerLoginGateRegistry.KeyFor(Host.Trim(), Port, Username))));
}

public sealed record FtpAuthenticationNotice(FtpLoginAccount Account, DateTime NextProbeAtUtc);

public sealed class FtpLoginHeldException(string message, bool authenticationHold, DateTime? retryAtUtc,
    bool loginRateHold = false, bool siteHealthHold = false)
    : InvalidOperationException(message)
{
    public bool AuthenticationHold { get; } = authenticationHold;
    public DateTime? RetryAtUtc { get; } = retryAtUtc;
    public bool LoginRateHold { get; } = loginRateHold;
    public bool SiteHealthHold { get; } = siteHealthHold;
}

public sealed record FtpLoginObservation(FtpLoginAccount Account, bool Succeeded,
    ConnectFailureClassifier.ConnectFailure? Failure, bool TimedOut, bool BncCooldown, bool Ghost, DateTime ObservedAtUtc);

/// <summary>One explicit login boundary for pooled, UI, and ghost connections.</summary>
public static class GuardedFtpLogin
{
    private sealed record TrustGuard(CertificateManager Certificates, string Host, int Port, string? Identity);
    private static readonly ConditionalWeakTable<AsyncFtpClient, TrustGuard> TrustGuards = new();
    internal static void RegisterTrustGuard(AsyncFtpClient client, CertificateManager certificates, string host, int port, string? identity = null)
    {
        TrustGuards.Remove(client);
        TrustGuards.Add(client, new(certificates, host, port, identity));
    }
    private static readonly Lazy<AuthenticationCircuitBreaker> Shared = new(() => new(
        store: new AuthHoldStore(System.IO.Path.Combine(ConfigManager.AppDataPath, "ftp-auth-holds.json")))
    {
        NotificationSink = DeliverNotice
    });

    public static event Func<FtpAuthenticationNotice, bool>? AuthenticationRejected;
    public static event Action<FtpLoginObservation>? LoginObserved;
    /// <summary>Passive supervisors may extend automatic admission; the strictest account deadline wins.</summary>
    public static event Func<FtpLoginAccount, DateTime?>? RetryNotBeforeRequested;
    private static readonly Lazy<LoginAttemptGovernor> Governor = new(() => new(
        stateFilePath: System.IO.Path.Combine(ConfigManager.AppDataPath, "ftp-login-governor.json")));

    private static bool DeliverNotice(FtpAuthenticationNotice notice)
    {
        var listeners = AuthenticationRejected;
        if (listeners == null) return false;
        var delivered = false;
        foreach (Func<FtpAuthenticationNotice, bool> listener in listeners.GetInvocationList())
        {
            try { delivered |= listener(notice); }
            catch (Exception ex) { Log.Warning(ex, "Authentication hold notification could not be delivered"); }
        }
        return delivered;
    }

    public static bool IsAuthenticationHeld(FtpLoginAccount account) => Shared.Value.IsHeld(account);
    /// <summary>Read-only preflight hint; Begin still atomically admits at most one credential recovery probe.</summary>
    public static bool HasChangedHeldCredentials(FtpLoginAccount account, string password) =>
        Shared.Value.HasChangedHeldCredentials(account, password);
    public static DateTime? AuthenticationRetryAtUtc(FtpLoginAccount account) => Shared.Value.RetryAt(account);
    public static void RequestManualRetry(FtpLoginAccount account)
    {
        Shared.Value.RequestManualRetry(account);
        Governor.Value.RequestManualRetry(account);
    }
    public static LoginBudgetSnapshot GetLoginBudget(FtpLoginAccount account) => Governor.Value.Snapshot(account);

    public static Task ConnectAsync(AsyncFtpClient client, FtpLoginAccount account,
        CancellationToken ct = default, bool manualRetry = false, bool allowRecoveryProbe = true, PoolConfig? policy = null)
    {
        // A later Execute/LIST must not silently reconnect outside this boundary.
        FtpClientFactory.ApplyOwnerExclusiveConfig(client.Config);
        TrustGuards.TryGetValue(client, out var trust);
        trust?.Certificates.ThrowIfPending(trust.Host, trust.Port, trust.Identity);
        return RunAsync(Shared.Value, account, client.Credentials.Password ?? "", Connect,
            ct, manualRetry, allowRecoveryProbe, Governor.Value, policy, DeliverObservation, AdmissionDeadline);

        async Task Connect(CancellationToken token)
        {
            try { await client.Connect(token).ConfigureAwait(false); }
            catch
            {
                // Translate the library TLS exception before account/health observers
                // see it. A local trust decision is not an outage or rejected password.
                trust?.Certificates.ThrowIfPending(trust.Host, trust.Port, trust.Identity);
                throw;
            }
        }
    }

    private static DateTime? AdmissionDeadline(FtpLoginAccount account)
    {
        var listeners = RetryNotBeforeRequested;
        if (listeners == null) return null;
        DateTime? latest = null;
        foreach (Func<FtpLoginAccount, DateTime?> listener in listeners.GetInvocationList())
        {
            DateTime? deadline;
            try { deadline = listener(account); }
            catch (Exception ex)
            {
                Log.Warning(ex, "FTP site-health admission state could not be read");
                throw new FtpLoginHeldException("Site health admission state is unavailable; retry manually", false, null,
                    loginRateHold: true, siteHealthHold: true);
            }
            if (deadline > latest || latest == null) latest = deadline;
        }
        return latest;
    }

    private static void DeliverObservation(FtpLoginObservation observation)
    {
        var listeners = LoginObserved;
        if (listeners == null) return;
        foreach (Action<FtpLoginObservation> listener in listeners.GetInvocationList())
        {
            try { listener(observation); }
            catch (Exception ex) { Log.Warning(ex, "FTP login observation could not be delivered"); }
        }
    }

    internal static async Task RunAsync(AuthenticationCircuitBreaker circuit, FtpLoginAccount account,
        string password, Func<CancellationToken, Task> connect, CancellationToken ct = default,
        bool manualRetry = false, bool allowRecoveryProbe = true, LoginAttemptGovernor? governor = null,
        PoolConfig? policy = null, Action<FtpLoginObservation>? observe = null,
        Func<FtpLoginAccount, DateTime?>? admissionDeadline = null)
    {
        ct.ThrowIfCancellationRequested();
        var requested = governor?.ConsumeManualRetry(account) == true;
        var manual = manualRetry || requested;
        var attempt = circuit.Begin(account, password, manual, allowRecoveryProbe);
        GovernedLoginAttempt? governed = null;
        var started = false;
        try
        {
            // A changed password or explicit retry may test the account immediately.
            // Scheduled A9 probes must still respect a longer persisted site outage.
            // A !user ghost kill runs only after the site answered with a login limit, so
            // the site is up; the governor's ghost slot and minute gap still bound it.
            if (!manual && allowRecoveryProbe && !attempt.CredentialChanged && admissionDeadline?.Invoke(account) is { } retryAt &&
                retryAt > (governor?.UtcNow ?? DateTime.UtcNow))
                throw new FtpLoginHeldException("Site health supervisor is deferring automatic new logins", false, retryAt,
                    loginRateHold: true, siteHealthHold: true);
            // A9 already serializes recovery probes; an older successful burst must
            // not prevent the one admitted probe from testing changed credentials.
            governed = governor?.Begin(account, policy, manual || attempt.RecoveryProbe, !allowRecoveryProbe);
            ct.ThrowIfCancellationRequested();
            started = true;
            await connect(ct).ConfigureAwait(false);
            circuit.Succeeded(attempt);
            if (governed != null) governor!.Complete(governed, null, false, false);
            Notify(new(account, true, null, false, false, !allowRecoveryProbe, governor?.UtcNow ?? DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            if (!started)
            {
                if (governed != null) governor!.CancelBeforeConnect(governed);
                circuit.Failed(attempt, ConnectFailureClassifier.ConnectFailure.CallerAbandoned);
                throw; // Local admission denied; no server finding, ghost kill or health observation.
            }
            var failure = ConnectFailureClassifier.Classify(ex, ct.IsCancellationRequested,
                ConnectFailureClassifier.HasLoginLimitError(ex), ghostKillAlreadySpent: false);
            circuit.Failed(attempt, failure);
            var timedOut = failure != ConnectFailureClassifier.ConnectFailure.CallerAbandoned && LoginAttemptGovernor.IsTimeout(ex);
            var cooldown = failure != ConnectFailureClassifier.ConnectFailure.CallerAbandoned && LoginAttemptGovernor.IsBncCooldown(ex);
            if (governed != null) governor!.Complete(governed, failure, timedOut, cooldown);
            if (failure == ConnectFailureClassifier.ConnectFailure.TrustRejected) throw;
            Notify(new(account, false, failure, timedOut, cooldown, !allowRecoveryProbe, governor?.UtcNow ?? DateTime.UtcNow));
            throw;
        }

        void Notify(FtpLoginObservation observation)
        {
            try { observe?.Invoke(observation); }
            catch (Exception ex) { Log.Warning(ex, "FTP login observation failed"); }
        }
    }
}
