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

    // One throttle per factory == one per account/site: MountService builds a single
    // factory and hands the same instance to SpreadManager, so every pool that can
    // request a ghost kill for this account shares this interval.
    private readonly GhostKillThrottle _ghostKillThrottle;

    public FtpClientFactory(ServerConfig serverConfig, CertificateManager certManager,
        GhostKillThrottle? ghostKillThrottle = null)
    {
        _serverConfig = serverConfig;
        _certManager = certManager;
        _ghostKillThrottle = ghostKillThrottle ?? new GhostKillThrottle();
    }

    /// <summary>
    /// Settings that forbid FluentFTP from acting on a control channel behind the
    /// back of whoever currently owns it. The pool hands a connection to exactly one
    /// borrower at a time, and every one of these defaults breaks that exclusivity.
    ///
    /// <c>Noop = false</c> — the library's NOOP daemon read on its own background
    /// thread, racing the pool's neutralize/dispose and crashing the process from
    /// <c>GnuTlsInternalStream.Read</c> (6x Event 1026 on 2026-06-02). Keeping idle
    /// connections warm is the pool's owner-exclusive keepalive instead.
    ///
    /// <c>DisconnectWithQuit = false</c> — the QUIT+read cycle during Disconnect
    /// read from poisoned GnuTLS streams during disposal and crashed the process.
    ///
    /// <c>SelfConnectMode = Never</c> — the library default is
    /// <see cref="FtpSelfConnectMode.OnConnectionLost"/>, which makes any
    /// <c>Execute</c> on a control channel FluentFTP believes has dropped perform a
    /// full reconnect (AUTH TLS, USER, PASS) transparently, inside the command. That
    /// login is issued by the library, so <see cref="ServerLoginGate"/> never sees it
    /// and cannot reserve against it — on a 4-login account the cap is then overshot
    /// by a login nothing in our accounting knows exists, the BNC answers 530, and
    /// the pool reports a server fault while its own counter still reads
    /// <c>created=0</c>. On 2026-09-18 (v3.10.124) 62 exception stacks carried the
    /// signature <c>Execute -> Connect(reConnect) -> HandshakeAsync</c>, rising from
    /// 31 two days earlier — and those are only the silent re-logins that FAILED; a
    /// successful one logs nothing at all, which is why the cost was attributed to
    /// borrow timeouts and ghost sessions for as long as it was.
    ///
    /// With <c>Never</c> a dropped connection makes the command throw, the caller
    /// disposes the <c>PooledConnection</c>, the pool discards it, and the
    /// replacement is created through the gate — the path the pool already takes
    /// whenever a silent reconnect fails today. Explicit <c>Connect</c> still works:
    /// this factory calls it directly, and the pool has no other way in.
    /// </summary>
    internal static void ApplyOwnerExclusiveConfig(FtpConfig config)
    {
        config.Noop = false;
        config.DisconnectWithQuit = false;
        config.SelfConnectMode = FtpSelfConnectMode.Never;
        // Execute also requests a reconnect on a HEALTHY encrypted connection
        // after the default 750 socket transactions. With Never that becomes a
        // command failure, potentially halfway through our custom CPSV sequence.
        // Disable library-owned recycling as well as library-owned reconnects;
        // real connection loss still fails and is recovered through the pool.
        config.SslSessionLength = 0;
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

        ApplyOwnerExclusiveConfig(client.Config);

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
    /// <summary>
    /// Log in as <c>!username</c> so glftpd drops this account's other sessions.
    /// Returns false — without touching the network — when a kill was already issued
    /// within <see cref="GhostKillThrottle.MinInterval"/>: the sessions the BNC is
    /// counting are then either our own live connections or quarantined ones still
    /// inside their deferred-teardown window, and a second <c>!user</c> login would
    /// sever the live ones and read to the BNC as a reconnect storm.
    /// </summary>
    public async Task<bool> KillGhosts(CancellationToken ct = default)
    {
        var conn = _serverConfig.Connection;
        if (!_ghostKillThrottle.TryAcquire(DateTime.UtcNow, out var sinceLast))
        {
            Log.Information(
                "Ghost kill suppressed for {Host}: last !{User} login was {Ago:F1}s ago (min interval {Min}s) — the sessions the BNC counts are our own",
                conn.Host, conn.Username, sinceLast.TotalSeconds, (int)_ghostKillThrottle.MinInterval.TotalSeconds);
            return false;
        }
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
        return true;
    }
}
