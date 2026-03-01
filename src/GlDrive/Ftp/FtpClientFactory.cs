using System.Net;
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
            Log.Information("Using SOCKS5 proxy {ProxyHost}:{ProxyPort} for {FtpHost}",
                proxy.Host, proxy.Port, conn.Host);
        }
        else
        {
            client = new AsyncFtpClient(conn.Host, conn.Username, password, conn.Port);
        }

        // FTPS Explicit (AUTH TLS)
        client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
        client.Config.DataConnectionEncryption = true;

        // Use GnuTLS for TLS session reuse (critical for glftpd)
        client.Config.CustomStream = typeof(GnuTlsStream);

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

        // Self-signed cert validation via TOFU
        client.ValidateCertificate += (control, e) =>
        {
            var result = _certManager.ValidateCertificate(conn.Host, conn.Port, e.Certificate).Result;
            e.Accept = result;
        };

        // Log FTP commands to Serilog
        client.Config.LogToConsole = false;
        client.Logger = new FtpLogAdapter();

        return client;
    }

    private class FtpLogAdapter : IFtpLogger
    {
        public void Log(FtpLogEntry entry)
        {
            // Redact passwords
            var msg = entry.Message;
            if (msg.Contains("PASS", StringComparison.OrdinalIgnoreCase))
                msg = "PASS [REDACTED]";
            Serilog.Log.Debug("[FTP] {Message}", msg);
        }
    }

    public async Task<AsyncFtpClient> CreateAndConnect(CancellationToken ct = default)
    {
        var client = Create();
        try
        {
            await client.Connect(ct);
            Log.Information("Connected to {Host}:{Port} as {User}",
                _serverConfig.Connection.Host, _serverConfig.Connection.Port, _serverConfig.Connection.Username);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
