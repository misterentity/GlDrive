using System.Security.Cryptography;
using System.Text;
using FluentFTP;
using GlDrive.Config;
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

public sealed class FtpLoginHeldException(string message, bool authenticationHold, DateTime? retryAtUtc)
    : InvalidOperationException(message)
{
    public bool AuthenticationHold { get; } = authenticationHold;
    public DateTime? RetryAtUtc { get; } = retryAtUtc;
}

/// <summary>One explicit login boundary for pooled, UI, and ghost connections.</summary>
public static class GuardedFtpLogin
{
    private static readonly Lazy<AuthenticationCircuitBreaker> Shared = new(() => new(
        store: new AuthHoldStore(System.IO.Path.Combine(ConfigManager.AppDataPath, "ftp-auth-holds.json")))
    {
        NotificationSink = DeliverNotice
    });

    public static event Func<FtpAuthenticationNotice, bool>? AuthenticationRejected;

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
    public static DateTime? AuthenticationRetryAtUtc(FtpLoginAccount account) => Shared.Value.RetryAt(account);
    public static void RequestManualRetry(FtpLoginAccount account) => Shared.Value.RequestManualRetry(account);

    public static Task ConnectAsync(AsyncFtpClient client, FtpLoginAccount account,
        CancellationToken ct = default, bool manualRetry = false, bool allowRecoveryProbe = true)
    {
        // A later Execute/LIST must not silently reconnect outside this boundary.
        FtpClientFactory.ApplyOwnerExclusiveConfig(client.Config);
        return RunAsync(Shared.Value, account, client.Credentials.Password ?? "", client.Connect,
            ct, manualRetry, allowRecoveryProbe);
    }

    internal static async Task RunAsync(AuthenticationCircuitBreaker circuit, FtpLoginAccount account,
        string password, Func<CancellationToken, Task> connect, CancellationToken ct = default,
        bool manualRetry = false, bool allowRecoveryProbe = true)
    {
        ct.ThrowIfCancellationRequested();
        var attempt = circuit.Begin(account, password, manualRetry, allowRecoveryProbe);
        try
        {
            ct.ThrowIfCancellationRequested();
            await connect(ct).ConfigureAwait(false);
            circuit.Succeeded(attempt);
        }
        catch (Exception ex)
        {
            var failure = ConnectFailureClassifier.Classify(ex, ct.IsCancellationRequested,
                ConnectFailureClassifier.HasLoginLimitError(ex), ghostKillAlreadySpent: false);
            circuit.Failed(attempt, failure);
            throw;
        }
    }
}
