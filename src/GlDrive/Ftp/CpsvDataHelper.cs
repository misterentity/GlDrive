using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using FluentFTP;
using Serilog;

namespace GlDrive.Ftp;

/// <summary>
/// Handles CPSV-based data connections for glftpd BNCs.
/// With CPSV, the server does SSL_connect on the data channel (it acts as TLS client),
/// so we must act as TLS server (SSL_accept). TLS negotiation happens AFTER the
/// data command (LIST/RETR/STOR) is sent on the control channel.
/// </summary>
public static class CpsvDataHelper
{
    /// <summary>
    /// Strips CRLF and null bytes from FTP paths to prevent command injection.
    /// </summary>
    internal static string SanitizeFtpPath(string path)
        => path.Replace("\r", "").Replace("\n", "").Replace("\0", "");

    // ---------------------------------------------------------------------
    // Control-channel desync tracking.
    //
    // Every CPSV op is a MULTI-REPLY sequence on the control channel:
    //   CPSV -> 227 ... ; LIST/RETR/STOR -> 150 ; <data transfer> ; -> 226
    // If the sequence aborts anywhere between OpenDataTcp and that final 226,
    // the control channel is left holding an unread reply. The next command on
    // that connection then reads the STALE one — which is exactly how
    // "Failed to parse CPSV response: Type set to A." happens: CPSV read back
    // TYPE A's leftover 200.
    //
    // FtpOperations already poisoned its own borrowed connection on failure,
    // but that guard was attached to ONE call site rather than to the thing it
    // protects. Every other caller (NewReleaseMonitor, SpreadJob, SpreadManager,
    // FtpSearchService, StreamingDownloader, MediaStreamServer) returned the
    // desynced connection to the pool clean, so the next borrower inherited it
    // (observed 2026-08-11: three consecutive NewReleaseMonitor polls, 60s apart,
    // all failing on the same stale reply).
    //
    // So the flag lives on the CLIENT, keyed on the property that defines the
    // hazard — "this control channel owes us a reply" — set when the sequence
    // starts and cleared only when it provably completes. Any other exit path
    // leaves it set and PooledConnection.DisposeAsync discards the connection.
    // ConditionalWeakTable so a client that is never returned is not retained.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AsyncFtpClient, object> PendingSequences = new();
    private static readonly object PendingMarker = new();

    /// <summary>
    /// Marks this control channel as owing a reply. Called when a CPSV data
    /// sequence begins; until <see cref="CompleteDataSequence"/> clears it, the
    /// connection must not be reused by anyone else.
    /// </summary>
    internal static void BeginDataSequence(AsyncFtpClient client)
        => PendingSequences.AddOrUpdate(client, PendingMarker);

    /// <summary>
    /// Reads the transfer-completion reply and clears the desync flag. Only a
    /// successful read clears it — if GetReply throws, the channel really is
    /// still out of sync and the connection stays marked.
    /// </summary>
    internal static async Task<FtpReply> CompleteDataSequence(AsyncFtpClient client, CancellationToken ct)
    {
        var reply = await client.GetReply(ct);
        EndDataSequence(client);
        return reply;
    }

    /// <summary>
    /// Clears the desync flag. Separate from <see cref="CompleteDataSequence"/> only
    /// so the mark/clear invariant is reachable without a live control channel.
    /// </summary>
    internal static void EndDataSequence(AsyncFtpClient client)
        => PendingSequences.Remove(client);

    /// <summary>
    /// True when a CPSV data sequence on this client did not run to completion,
    /// leaving an unread reply on the control channel. Such a connection is
    /// unusable and must be discarded rather than returned to the pool.
    /// </summary>
    public static bool HasPendingDataSequence(AsyncFtpClient client)
        => PendingSequences.TryGetValue(client, out _);

    private static readonly Regex PasvRegex = new(@"\((\d+),(\d+),(\d+),(\d+),(\d+),(\d+)\)", RegexOptions.Compiled);
    private static readonly Regex UnixListRegex = new(@"^([dlcbps-])[rwxsStT-]{9}\s+\d+\s+\S+\s+\S+\s+(\d+)\s+(\w{3}\s+\d+\s+[\d:]+)\s+(.+)$", RegexOptions.Compiled);

    private static readonly string[] DateFormats = ["MMM dd HH:mm", "MMM dd yyyy", "MMM  d HH:mm", "MMM  d yyyy"];

    private static readonly Lazy<X509Certificate2> SelfSignedCert = new(() =>
    {
        // RSA-2048 self-signed cert used only as the data-channel TLS-server identity
        // in CPSV mode. glftpd doesn't validate our cert, so the validity window is
        // process-lifetime; 10 years guarantees it never expires during a realistic
        // process run.
        //
        // Load with DefaultKeySet (no flags) instead of Exportable: the private key
        // is stored in the user CNG container and is still accessible to SChannel
        // for the server-auth handshake, but the managed X509Certificate2.Export()
        // API cannot dump the private key. This closes the memory-dump exfil vector
        // from finding H-11 without the compat break that EphemeralKeySet caused
        // (SChannel cannot use EphemeralKeySet certs for server authentication —
        // error 0x8009030E "No credentials are available in the security package").
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=GlDrive", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(10));
        var pfx = cert.Export(X509ContentType.Pfx, "");
        return X509CertificateLoader.LoadPkcs12(pfx, "", X509KeyStorageFlags.DefaultKeySet);
    });

    /// <summary>
    /// Sends CPSV and establishes a TCP connection to the data port.
    /// Does NOT negotiate TLS — that happens after the data command is sent.
    /// The CPSV reply contains a server-controlled IP; we refuse loopback,
    /// link-local, and unroutable ranges to block SSRF-like redirection attempts.
    /// </summary>
    internal static async Task<TcpClient> OpenDataTcp(
        AsyncFtpClient client, CancellationToken ct)
    {
        // From here until the completion reply is read, this control channel owes
        // us replies. Mark before CPSV is even sent: if CPSV itself reads a stale
        // reply (the desync signature), the connection is already unusable and
        // must not go back to the pool for the next caller to inherit.
        BeginDataSequence(client);

        var cpsvReply = await client.Execute("CPSV", ct);
        if (!cpsvReply.Success)
            throw new IOException($"CPSV failed: {cpsvReply.Code} {cpsvReply.Message}");

        var (cpsvIp, port) = ParsePasvResponse(cpsvReply.Message);
        Log.Debug("CPSV -> data endpoint parsed");

        // Reject unsafe target IPs (loopback, link-local, RFC 5735 unroutable)
        // before TCP connect. A hostile BNC cannot redirect our data channel
        // to internal-network addresses we wouldn't otherwise reach.
        if (!IsSafeDataTarget(cpsvIp))
        {
            Log.Warning("CPSV returned unsafe target {Ip}:{Port} — refusing data connection", cpsvIp, port);
            throw new IOException($"CPSV returned unsafe target address — refusing connection");
        }

        // Connect to the CPSV-returned IP:port directly.
        // With BNCs, the CPSV IP is the backend data address.
        var tcp = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(cpsvIp, port, connectCts.Token);
            Log.Debug("Data connection established");
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns true if the CPSV-returned IP is acceptable as a data-connection target.
    /// Rejects loopback (127.0.0.0/8), link-local (169.254.0.0/16), "this-network"
    /// (0.0.0.0/8), and multicast/broadcast ranges. Private RFC 1918 ranges are
    /// permitted because BNCs on the same LAN are a legitimate deployment.
    /// </summary>
    private static bool IsSafeDataTarget(string ipString)
    {
        if (!System.Net.IPAddress.TryParse(ipString, out var ip))
            return false;

        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false; // IPv6 not currently produced by CPSV; reject defensively

        var bytes = ip.GetAddressBytes();
        // 0.0.0.0/8 — "this network"
        if (bytes[0] == 0) return false;
        // 127.0.0.0/8 — loopback
        if (bytes[0] == 127) return false;
        // 169.254.0.0/16 — link-local (AWS metadata lives at 169.254.169.254)
        if (bytes[0] == 169 && bytes[1] == 254) return false;
        // 224.0.0.0/4 — multicast
        if (bytes[0] >= 224 && bytes[0] <= 239) return false;
        // 240.0.0.0/4 — reserved / future use
        if (bytes[0] >= 240) return false;
        return true;
    }

    /// <summary>
    /// Negotiates TLS on the data stream as TLS server.
    /// The glftpd server/BNC does SSL_connect (TLS client) on data channels after the
    /// data command is sent, so we must SSL_accept (TLS server).
    /// </summary>
    internal static async Task<SslStream> NegotiateDataTls(NetworkStream networkStream, CancellationToken ct)
    {
        // We are the TLS server; glftpd connects to us as TLS client.
        // ClientCertificateRequired is false, so remote cert callback is not security-relevant here.
        var ssl = new SslStream(networkStream, true, (_, _, _, _) => true);
        using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        tlsCts.CancelAfter(TimeSpan.FromSeconds(10));
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = SelfSignedCert.Value,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, tlsCts.Token);
        Log.Debug("Data TLS: server mode ({Protocol}, {Cipher})", ssl.SslProtocol, ssl.NegotiatedCipherSuite);
        return ssl;
    }

    public static async Task<FtpListItem[]> ListDirectory(
        AsyncFtpClient client, string remotePath, string controlHost, CancellationToken ct = default)
    {
        Log.Debug("CPSV LIST {Path}", remotePath);

        BeginDataSequence(client);
        var typeReply = await client.Execute("TYPE A", ct);
        if (typeReply.Code != "200")
            throw new IOException($"TYPE A failed: {typeReply.Code} {typeReply.Message}");

        var tcp = await OpenDataTcp(client, ct);

        try
        {
            // Send LIST command BEFORE TLS — server initiates SSL_connect after this
            var listReply = await client.Execute($"LIST -a {SanitizeFtpPath(remotePath)}", ct);
            if (listReply.Code != "150" && listReply.Code != "125")
                throw new IOException($"LIST failed: {listReply.Code} {listReply.Message}");

            // Now negotiate TLS (server should be doing SSL_connect)
            var ssl = await NegotiateDataTls(tcp.GetStream(), ct);

            try
            {
                using var reader = new StreamReader(ssl, Encoding.UTF8);
                var listing = await reader.ReadToEndAsync(ct);

                ssl.Close();
                tcp.Close();

                var completeReply = await CompleteDataSequence(client, ct);
                Log.Debug("LIST complete: {Code} {Message}", completeReply.Code, completeReply.Message);

                return ParseUnixListing(listing, remotePath);
            }
            finally
            {
                ssl.Dispose();
            }
        }
        finally
        {
            tcp.Dispose();
        }
    }

    public static async Task<byte[]> DownloadFile(
        AsyncFtpClient client, string remotePath, string controlHost, CancellationToken ct = default)
    {
        Log.Debug("CPSV RETR {Path}", remotePath);

        BeginDataSequence(client);
        await client.Execute("TYPE I", ct);

        var tcp = await OpenDataTcp(client, ct);

        try
        {
            var retrReply = await client.Execute($"RETR {SanitizeFtpPath(remotePath)}", ct);
            if (retrReply.Code != "150" && retrReply.Code != "125")
                throw new IOException($"RETR failed: {retrReply.Code} {retrReply.Message}");

            var ssl = await NegotiateDataTls(tcp.GetStream(), ct);

            try
            {
                var ms = new MemoryStream();
                await ssl.CopyToAsync(ms, ct);

                ssl.Close();
                tcp.Close();

                var completeReply = await CompleteDataSequence(client, ct);
                Log.Debug("RETR complete: {Code} {Message}", completeReply.Code, completeReply.Message);

                // Always copy to exactly Length bytes — TryGetBuffer's underlying array can be over-allocated.
                return ms.ToArray();
            }
            finally
            {
                ssl.Dispose();
            }
        }
        finally
        {
            tcp.Dispose();
        }
    }

    /// <summary>
    /// Downloads a file via CPSV RETR, streaming to the provided output stream.
    /// Calls onProgress with bytes read so far after each chunk.
    /// </summary>
    public static async Task DownloadFileToStream(
        AsyncFtpClient client, string remotePath, Stream output,
        Action<long>? onProgress = null, CancellationToken ct = default)
    {
        Log.Debug("CPSV RETR (stream) {Path}", remotePath);

        BeginDataSequence(client);
        await client.Execute("TYPE I", ct);

        var tcp = await OpenDataTcp(client, ct);

        try
        {
            var retrReply = await client.Execute($"RETR {SanitizeFtpPath(remotePath)}", ct);
            if (retrReply.Code != "150" && retrReply.Code != "125")
                throw new IOException($"RETR failed: {retrReply.Code} {retrReply.Message}");

            var ssl = await NegotiateDataTls(tcp.GetStream(), ct);

            try
            {
                var buf = new byte[256 * 1024];
                long totalRead = 0;
                int rd;
                while ((rd = await ssl.ReadAsync(buf, ct)) > 0)
                {
                    await output.WriteAsync(buf.AsMemory(0, rd), ct);
                    totalRead += rd;
                    onProgress?.Invoke(totalRead);
                }

                ssl.Close();
                tcp.Close();

                var completeReply = await CompleteDataSequence(client, ct);
                Log.Debug("RETR complete: {Code} {Message}", completeReply.Code, completeReply.Message);
            }
            finally
            {
                ssl.Dispose();
            }
        }
        finally
        {
            tcp.Dispose();
        }
    }

    public static async Task UploadFile(
        AsyncFtpClient client, string remotePath, byte[] data, string controlHost, CancellationToken ct = default)
    {
        Log.Debug("CPSV STOR {Path} ({Bytes} bytes)", remotePath, data.Length);

        BeginDataSequence(client);
        await client.Execute("TYPE I", ct);

        var tcp = await OpenDataTcp(client, ct);

        try
        {
            var storReply = await client.Execute($"STOR {SanitizeFtpPath(remotePath)}", ct);
            if (storReply.Code != "150" && storReply.Code != "125")
                throw new IOException($"STOR failed: {storReply.Code} {storReply.Message}");

            var ssl = await NegotiateDataTls(tcp.GetStream(), ct);

            try
            {
                await ssl.WriteAsync(data, ct);
                await ssl.FlushAsync(ct);

                ssl.Close();
                tcp.Close();

                var completeReply = await CompleteDataSequence(client, ct);
                Log.Debug("STOR complete: {Code} {Message}", completeReply.Code, completeReply.Message);
            }
            finally
            {
                ssl.Dispose();
            }
        }
        finally
        {
            tcp.Dispose();
        }
    }

    public static async Task UploadFileStream(
        AsyncFtpClient client, string remotePath, Stream stream, string controlHost, CancellationToken ct = default)
    {
        // If stream is a small MemoryStream, use the fast byte[] path
        if (stream is MemoryStream ms2 && ms2.TryGetBuffer(out var seg))
        {
            await UploadFile(client, remotePath, seg.Count == seg.Array!.Length
                ? seg.Array : ms2.ToArray(), controlHost, ct);
            return;
        }

        // Stream directly to SSL — avoids double-buffering for large files
        BeginDataSequence(client);
        await client.Execute("TYPE I", ct);
        var tcp = await OpenDataTcp(client, ct);
        try
        {
            var storReply = await client.Execute($"STOR {SanitizeFtpPath(remotePath)}", ct);
            if (storReply.Code != "150" && storReply.Code != "125")
                throw new IOException($"STOR failed: {storReply.Code} {storReply.Message}");

            var ssl = await NegotiateDataTls(tcp.GetStream(), ct);
            try
            {
                stream.Position = 0;
                await stream.CopyToAsync(ssl, ct);
                ssl.Close();
                tcp.Close();
                var completeReply = await CompleteDataSequence(client, ct);
                Log.Debug("STOR stream complete: {Code} {Message}", completeReply.Code, completeReply.Message);
            }
            finally
            {
                ssl.Dispose();
            }
        }
        finally
        {
            tcp.Dispose();
        }
    }

    private static (string ip, int port) ParsePasvResponse(string message)
    {
        var match = PasvRegex.Match(message);
        if (!match.Success)
            throw new IOException($"Failed to parse CPSV response: {message}");

        var ip = $"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}.{match.Groups[4].Value}";
        var port = int.Parse(match.Groups[5].Value) * 256 + int.Parse(match.Groups[6].Value);
        return (ip, port);
    }

    private static FtpListItem[] ParseUnixListing(string listing, string parentPath)
    {
        var items = new List<FtpListItem>(64);
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("total"))
                continue;
            var item = ParseUnixLine(trimmed, parentPath);
            if (item != null)
                items.Add(item);
        }
        return items.ToArray();
    }

    private static FtpListItem? ParseUnixLine(string line, string parentPath)
    {
        // drwxr-xr-x  2 user group  4096 Jan 15 10:30 dirname
        // -rw-r--r--  1 user group 12345 Feb 20  2024 filename
        // lrwxrwxrwx  1 user group     8 Mar  1 12:00 link -> target
        var match = UnixListRegex.Match(line);
        if (!match.Success)
            return null;

        var typeChar = match.Groups[1].Value[0];
        var size = long.Parse(match.Groups[2].Value);
        var dateStr = match.Groups[3].Value;
        var name = match.Groups[4].Value;

        // Handle symlinks
        if (typeChar == 'l' && name.Contains(" -> "))
            name = name[..name.IndexOf(" -> ")];

        if (name is "." or "..")
            return null;

        DateTime modified = DateTime.MinValue;
        if (DateTime.TryParseExact(dateStr, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            modified = dt;
            if (modified.Year == 1 && dateStr.Contains(':'))
                modified = new DateTime(DateTime.Now.Year, modified.Month, modified.Day, modified.Hour, modified.Minute, 0);
            if (modified > DateTime.Now)
                modified = modified.AddYears(-1);
        }

        return new FtpListItem
        {
            Name = name,
            FullName = parentPath.TrimEnd('/') + "/" + name,
            Size = size,
            Type = typeChar switch
            {
                'd' => FtpObjectType.Directory,
                'l' => FtpObjectType.Link,
                _ => FtpObjectType.File
            },
            Modified = modified
        };
    }
}
