using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GlDrive.Config;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Services;

public sealed class SiteRetryDeferredException(string message, DateTimeOffset? retryAtUtc) : InvalidOperationException(message)
{
    public DateTimeOffset? RetryAtUtc { get; } = retryAtUtc;
}

public partial class ServerManager
{
    private SiteHealthTracker _siteHealth = null!;
    private readonly ConcurrentDictionary<string, (SiteFailureKind Kind, DateTime At)> _latestLoginFailures = new();
    public event Action<SiteHealthNotice>? SiteHealthNotice;
    internal Func<ConnectionConfig, CancellationToken, Task<bool>> TcpProbe { get; set; } =
        (connection, ct) => SiteTcpProbe.ConnectAsync(connection, ct);

    private void InitializeSiteHealth()
    {
        _siteHealth = new SiteHealthTracker(Path.Combine(ConfigManager.AppDataPath, "site-health.json"));
        GuardedFtpLogin.LoginObserved += ObserveLoginHealth;
        GuardedFtpLogin.RetryNotBeforeRequested += AccountRetryNotBefore;
    }

    private void StopSiteHealth()
    {
        GuardedFtpLogin.LoginObserved -= ObserveLoginHealth;
        GuardedFtpLogin.RetryNotBeforeRequested -= AccountRetryNotBefore;
    }

    private DateTime? AccountRetryNotBefore(FtpLoginAccount account)
    {
        if (_disposed || !_config.Health.Enabled) return null;
        DateTime? latest = null;
        foreach (var server in _config.Servers.Where(s => FtpLoginAccount.From(s.Connection).Key == account.Key))
        {
            var due = SiteRetryNotBefore(server);
            if (due > latest || latest == null) latest = due;
        }
        return latest;
    }

    private static string EndpointIdentity(ServerConfig server)
    {
        var connection = server.Connection;
        var proxy = connection.Proxy;
        var route = proxy is { Enabled: true } ? $"{proxy.Host.Trim().ToLowerInvariant()}:{proxy.Port}:{proxy.Username}" : "direct";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{FtpLoginAccount.From(connection).Key}\n{route}")));
    }

    public SiteHealthSnapshot GetSiteHealth(string serverId)
    {
        var server = _config.Servers.FirstOrDefault(s => s.Id == serverId);
        return _siteHealth.Get(serverId, server == null ? "removed" : EndpointIdentity(server));
    }

    public IReadOnlyList<SiteHealthSnapshot> SiteHealthStates => _config.Servers.Select(server => GetSiteHealth(server.Id)).ToArray();

    private void ObserveLoginHealth(FtpLoginObservation observation)
    {
        if (_disposed || !_config.Health.Enabled || observation.Ghost ||
            observation.Failure is ConnectFailureClassifier.ConnectFailure.CallerAbandoned or ConnectFailureClassifier.ConnectFailure.TrustRejected) return;
        var kind = observation.BncCooldown ? SiteFailureKind.BncCooldown : observation.TimedOut ? SiteFailureKind.Timeout :
            KindFor(observation.Failure);
        foreach (var server in _config.Servers.Where(s => FtpLoginAccount.From(s.Connection).Key == observation.Account.Key))
        {
            if (observation.Succeeded) RecordSiteRecovery(server, observation.ObservedAtUtc);
            else
            {
                _latestLoginFailures.AddOrUpdate(server.Id, (kind, observation.ObservedAtUtc),
                    (_, previous) => previous.At >= observation.ObservedAtUtc ? previous : (kind, observation.ObservedAtUtc));
                DeliverSiteHealthNotice(_siteHealth.Failed(server.Id, EndpointIdentity(server), kind, observation.ObservedAtUtc));
            }
        }
    }

    private static SiteFailureKind KindFor(ConnectFailureClassifier.ConnectFailure? failure) => failure switch
    {
        ConnectFailureClassifier.ConnectFailure.HostUnreachable => SiteFailureKind.Unreachable,
        ConnectFailureClassifier.ConnectFailure.AuthRejected => SiteFailureKind.Authentication,
        ConnectFailureClassifier.ConnectFailure.ServerRefused => SiteFailureKind.Refused,
        ConnectFailureClassifier.ConnectFailure.BncLoginLimit => SiteFailureKind.LoginLimit,
        ConnectFailureClassifier.ConnectFailure.AccountLoginCapped => SiteFailureKind.LocalCapacity,
        _ => SiteFailureKind.Unknown
    };

    private SiteFailureKind FailureKind(string serverId, Exception exception, DateTime attemptStartedUtc, bool connectAttempt)
    {
        if (exception is FtpLoginHeldException { AuthenticationHold: true }) return SiteFailureKind.Authentication;
        if (connectAttempt && _latestLoginFailures.TryGetValue(serverId, out var observation) && observation.At >= attemptStartedUtc)
            return observation.Kind;
        // An explicit server cooldown is recorded at the login boundary (observation.BncCooldown).
        // A bare 421/450 here is usually "too many users": never a two-hour quarantine by itself.
        if (connectAttempt && LoginAttemptGovernor.IsTimeout(exception)) return SiteFailureKind.Timeout;
        return KindFor(ConnectFailureClassifier.Classify(exception, false,
            ConnectFailureClassifier.HasLoginLimitError(exception), false));
    }

    private void RecordSiteFailure(ServerConfig server, Exception exception, DateTime started, bool connectAttempt)
    {
        if (!_config.Health.Enabled || exception is SiteRetryDeferredException || _disposed) return;
        // A dropped live connection (keepalive NOOP) is not evidence the site is down; the
        // reconnect that follows is a real login and is observed at the login boundary. Arming
        // the ladder here deferred that reconnect and every race login on the account.
        if (!connectAttempt) return;
        if (ConnectFailureClassifier.Classify(exception, false, false, false) == ConnectFailureClassifier.ConnectFailure.TrustRejected) return;
        // Admission holds and local capacity waits are not new remote evidence.
        // Actual connects were already observed at the single login boundary.
        for (var cause = exception; cause != null; cause = cause.InnerException!)
            if (cause is FtpLoginHeldException held)
            {
                if (held.AuthenticationHold && GuardedFtpLogin.IsAuthenticationHeld(FtpLoginAccount.From(server.Connection)))
                    DeliverSiteHealthNotice(_siteHealth.AuthenticationHeld(server.Id, EndpointIdentity(server),
                        DateTimeOffset.UtcNow, held.RetryAtUtc));
                return;
            }
        if (_latestLoginFailures.TryGetValue(server.Id, out var observed) && observed.At >= started) return;
        var notice = _siteHealth.Failed(server.Id, EndpointIdentity(server), FailureKind(server.Id, exception, started, connectAttempt), DateTimeOffset.UtcNow);
        DeliverSiteHealthNotice(notice);
    }

    private void RecordSiteRecovery(ServerConfig server, DateTimeOffset? observedAt = null)
    {
        if (!_config.Health.Enabled || _disposed) return;
        // An old authenticated lease can answer NOOP while new authentication
        // remains rejected. Only a successful guarded login proves that recovery.
        if (observedAt == null && !SiteHealthPolicy.ExistingConnectionCanRecover(GetSiteHealth(server.Id).FailureClass)) return;
        var notice = _siteHealth.Recovered(server.Id, EndpointIdentity(server), observedAt ?? DateTimeOffset.UtcNow);
        DeliverSiteHealthNotice(notice);
    }

    private void DeliverSiteHealthNotice(SiteHealthNotice? notice)
    {
        if (notice == null || SiteHealthNotice is not { } listeners) return;
        foreach (Action<SiteHealthNotice> listener in listeners.GetInvocationList())
            try { listener(notice); }
            catch (Exception ex) { Log.Warning(ex, "Site health notification could not be delivered"); }
    }

    private DateTime? SiteRetryNotBefore(ServerConfig server)
    {
        if (!_config.Health.Enabled) return null;
        return GetSiteHealth(server.Id).NextRetryUtc?.UtcDateTime;
    }

    private async Task CheckAutomaticSiteRetry(ServerConfig server, CancellationToken ct, bool manualRetry)
    {
        _certManager.ThrowIfPending(server.Connection.Host, server.Connection.Port);
        if (!_config.Health.Enabled || manualRetry) return;
        // Let A9 atomically admit its single changed-credential recovery probe.
        // This read-only comparison grants no login and never bypasses ordinary
        // configuration edits or the persisted TCP probe budget.
        var connection = server.Connection;
        if (GuardedFtpLogin.HasChangedHeldCredentials(FtpLoginAccount.From(connection),
            CredentialStore.GetPassword(connection.Host, connection.Port, connection.Username) ?? "")) return;
        var identity = EndpointIdentity(server);
        var now = DateTimeOffset.UtcNow;
        var snapshot = _siteHealth.Get(server.Id, identity);
        if (!_siteHealth.CanAttempt(server.Id, identity, now))
            throw new SiteRetryDeferredException(snapshot.Message, snapshot.NextRetryUtc);
        if (!_config.Health.TcpProbesEnabled || snapshot.FailureClass is not (SiteFailureKind.Unreachable or SiteFailureKind.Timeout) ||
            snapshot.FirstFailureUtc is not { } first || now - first < TimeSpan.FromMinutes(30)) return;
        if (!_siteHealth.TryReserveProbe(server.Id, identity, now))
            throw new SiteRetryDeferredException("TCP probe is waiting for its persisted 15-minute budget", snapshot.LastProbeUtc?.AddMinutes(15));
        if (await TcpProbe(server.Connection, ct).ConfigureAwait(false)) return;
        var notice = _siteHealth.Failed(server.Id, identity, snapshot.FailureClass, DateTimeOffset.UtcNow);
        DeliverSiteHealthNotice(notice);
        throw new SiteRetryDeferredException("TCP route probe did not connect; full FTP retry remains deferred", GetSiteHealth(server.Id).NextRetryUtc);
    }
}
