using System.Net;
using System.Text.RegularExpressions;
using FluentFTP;
using FluentFTP.GnuTLS;
using FluentFTP.GnuTLS.Enums;
using FluentFTP.Proxy.AsyncProxy;
using GlDrive.Config;
using GlDrive.Tls;
using Serilog;

namespace GlDrive.Ftp;

public class FtpClientFactory
{
    private readonly ServerConfig _serverConfig;
    private readonly CertificateManager _certManager;

    public string Host => _serverConfig.Connection.Host;

    public FtpClientFactory(ServerConfig serverConfig, CertificateManager certManager)
    {
        _serverConfig = serverConfig;
        _certManager = certManager;
    }

    public AsyncFtpClient Create()
    {
        var conn = _serverConfig.Connection;
        var password = CredentialStore.GetPassword(conn.Host, conn.Port, conn.Username) ?? "";

        AsyncFtpClient client;
        var proxy = conn.Proxy;
        if (proxy is { Enabled: true } && !string.IsNullOrWhiteSpace(proxy.Host))
        {
            var proxyPassword = !string.IsNullOrEmpty(proxy.Username)
                ? CredentialStore.GetProxyPassword(proxy.Host, proxy.Port, proxy.Username) ?? ""
                : "";

            var profile = new FtpProxyProfile
            {
                ProxyHost = proxy.Host,
                ProxyPort = proxy.Port,
                ProxyCredentials = !string.IsNullOrEmpty(proxy.Username)
                    ? new NetworkCredential(proxy.Username, proxyPassword)
                    : null,
                FtpHost = conn.Host,
                FtpPort = conn.Port,
                FtpCredentials = new NetworkCredential(conn.Username, password),
            };
            client = new AsyncFtpClientSocks5Proxy(profile);
            Log.Information("Using SOCKS5 proxy for FTP connection");
        }
        else
        {
            client = new AsyncFtpClient(conn.Host, conn.Username, password, conn.Port);
        }

        // FTPS Explicit (AUTH TLS)
        client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
        client.Config.DataConnectionEncryption = true;

        // Use GnuTLS for TLS session reuse (critical for glftpd), via SerializedGnuTlsStream —
        // a wrapper that serializes the native recv against gnutls_deinit to close the long-running
        // GnuTlsInternalStream.Read use-after-free crash. See that type for the full root cause.
        client.Config.CustomStream = typeof(SerializedGnuTlsStream);

        // TLS configuration via GnuTLS
        var gnuConfig = new GnuConfig
        {
            SecuritySuite = GnuSuite.Secure128
        };

        // TLS 1.2 preferred (glftpd TLS 1.3 session ticket bug)
        if (_serverConfig.Tls.PreferTls12)
        {
            gnuConfig.AdvancedOptions = [GnuAdvanced.NoTickets];
        }

        client.Config.CustomStreamConfig = gnuConfig;

        // Try EPSV first (server advertises it in FEAT), fall back to PASVEX
        // EPSV works better with BNCs as it only returns the port, not IP
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        client.Config.ConnectTimeout = 30000;
        client.Config.ReadTimeout = 30000;
        client.Config.DataConnectionConnectTimeout = 30000;
        client.Config.DataConnectionReadTimeout = 30000;

        // glftpd compatibility: Unix LIST parsing, no MLSD
        client.Config.ListingParser = FtpParser.Unix;

        // Don't attempt MLSD (glftpd doesn't support it)
        client.Config.ListingCustomParser = null;

        // Disable stale data check — GnuTLS crashes during ReadStaleDataAsync
        // when connections are in a poisoned state (e.g., after failed FXP transfers)
        client.Config.StaleDataCheck = false;

        // FluentFTP's built-in NOOP daemon is DISABLED. It ran reads on its own
        // background thread, every NoopInterval, independent of who owned the
        // connection. When the pool neutralized/disposed a poisoned connection
        // (socket Close + m_customStream null in NeutralizeGnuTls) while a daemon
        // read was in flight, the read dereferenced freed state and threw an NRE
        // from GnuTlsInternalStream.Read that escaped to terminate the process —
        // 6 such crashes on 2026-06-02 (Event 1026), all stack
        // `GnuTlsInternalStream.Read` <- threadpool dispatch, NOT the NoopDaemon
        // frame our UnobservedTaskException handler suppresses.
        //
        // Keeping idle pool connections warm is now the pool's job via the
        // owner-exclusive FtpConnectionPool keepalive (ConfigureHealth): it reads
        // a connection OUT of the channel before NOOPing it, so a keepalive read
        // can never run concurrently with a borrow, quarantine, or dispose of the
        // same connection. No background thread ever reads a connection another
        // thread might be tearing down — the race is gone.
        client.Config.Noop = false;

        // Skip QUIT+read cycle during Disconnect — prevents GnuTLS from attempting
        // to read from poisoned streams during disposal, which crashes the process
        client.Config.DisconnectWithQuit = false;

        // Self-signed cert validation via TOFU
        // Run on a thread pool thread with no sync context to avoid deadlocks
        // when this callback fires on the WPF dispatcher thread
        client.ValidateCertificate += (control, e) =>
        {
            var prevCtx = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                e.Accept = _certManager.ValidateCertificate(conn.Host, conn.Port, e.Certificate)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevCtx);
            }
        };

        // Log FTP commands to Serilog
        client.Config.LogToConsole = false;
        client.Logger = new FtpLogAdapter();

        return client;
    }

    private class FtpLogAdapter : IFtpLogger
    {
        private static readonly Regex IpRegex = new(@"\d+\.\d+\.\d+\.\d+", RegexOptions.Compiled);

        // IPv6 literal — brackets optional, two or more hex groups separated by ':', at least one '::'
        // or four-plus groups. Deliberately conservative to avoid catching FTP control chatter.
        private static readonly Regex Ipv6Regex = new(
            @"\[?(?:[0-9a-fA-F]{1,4}:){2,7}[0-9a-fA-F]{1,4}\]?|::[0-9a-fA-F:]+|\[::\]",
            RegexOptions.Compiled);

        private static readonly Regex PassRegex = new(
            @"(?<=(^>?|\s)\s*)PASS\s+\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex UserRegex = new(
            @"(?<=(^>?|\s)\s*)USER\s+\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // glftpd admin commands that accept passwords as arguments.
        // SITE ADDUSER <nick> <password> [other args]
        // SITE CHPASS <nick> <password>
        // SITE CHANGE <nick> <setting> <password>
        private static readonly Regex SiteAdminRegex = new(
            @"(?<=SITE\s+)(ADDUSER|CHPASS|GADDUSER|CHANGE)\s+\S+\s+\S+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public void Log(FtpLogEntry entry)
        {
            var msg = entry.Message;
            msg = PassRegex.Replace(msg, "PASS [REDACTED]");
            msg = UserRegex.Replace(msg, "USER [REDACTED]");
            msg = SiteAdminRegex.Replace(msg, m => m.Groups[1].Value + " [REDACTED]");
            msg = IpRegex.Replace(msg, "*.*.*.*");
            msg = Ipv6Regex.Replace(msg, "[*:*]");
            Serilog.Log.Debug("[FTP] {Message}", msg);
        }
    }

    public async Task<AsyncFtpClient> CreateAndConnect(CancellationToken ct = default)
    {
        var client = Create();
        try
        {
            await client.Connect(ct);
            Log.Information("FTP connection established");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Kill ghost connections on the server by connecting with !username.
    /// glftpd BNC convention: login with ! prefix kills stale sessions for that user.
    /// The ghost-kill connection is immediately disconnected after login.
    /// </summary>
    public async Task KillGhosts(CancellationToken ct = default)
    {
        var conn = _serverConfig.Connection;
        var password = CredentialStore.GetPassword(conn.Host, conn.Port, conn.Username) ?? "";
        var ghostUser = "!" + conn.Username;

        // Create a minimal client with same TLS config but !username
        var client = new AsyncFtpClient(conn.Host, ghostUser, password, conn.Port);
        client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
        client.Config.DataConnectionEncryption = true;
        client.Config.CustomStream = typeof(SerializedGnuTlsStream);
        var gnuConfig = new GnuConfig { SecuritySuite = GnuSuite.Secure128 };
        if (_serverConfig.Tls.PreferTls12)
            gnuConfig.AdvancedOptions = [GnuAdvanced.NoTickets];
        client.Config.CustomStreamConfig = gnuConfig;
        client.Config.ConnectTimeout = 15000;
        client.Config.StaleDataCheck = false;
        client.Config.DisconnectWithQuit = false;
        // Use proper TOFU validation — ghost-kill sends the real password,
        // so a MitM with a fake cert could intercept credentials.
        client.ValidateCertificate += (control, e) =>
        {
            var conn = (AsyncFtpClient)control;
            e.Accept = _certManager.ValidateCertificate(conn.Host, conn.Port, e.Certificate)
                .ConfigureAwait(false).GetAwaiter().GetResult();
        };

        try
        {
            await client.Connect(ct);
            Log.Information("Ghost kill: connected as {User} to clear stale sessions", ghostUser);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Ghost kill: login as {User} failed (may not be supported)", ghostUser);
        }
        finally
        {
            try { await client.Disconnect(ct); } catch { }
            try { client.Dispose(); } catch { }
        }
    }
}
