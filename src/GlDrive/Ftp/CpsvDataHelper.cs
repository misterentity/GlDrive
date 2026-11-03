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
/// With CPSV, glftpd does SSL_connect on the data channel (it acts as TLS client),
/// so we act as TLS server (SSL_accept); daemons that stay TLS server (ioFTPD) are
/// learned per data IP — see <see cref="NegotiateDataTls"/>. TLS negotiation happens
/// AFTER the data command (LIST/RETR/STOR) is sent on the control channel.
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
    /// still out of sync and the connection stays marked. With
    /// <paramref name="validate"/> the reply must be 226/250 or an IOException
    /// is thrown AFTER the mark is cleared: a reply that has been read is no
    /// longer on the wire, so the channel is in sync whatever the code says.
    /// v3.10.111 validated first and left the mark set on glftpd's
    /// `426 Data Connection: Success.` after every fully relayed STOR — one
    /// destination login per successful file (12 of 12 on 2026-09-09 22:28).
    /// Senders (STOR) and ABOR pass validate:false: their bytes were flushed
    /// before the reply, and ABOR's legitimate answer IS a 426.
    /// </summary>
    internal static async Task<FtpReply> CompleteDataSequence(AsyncFtpClient client, CancellationToken ct, bool validate = true)
    {
        var reply = await client.GetReply(ct);
        return AcceptCompletionReply(client, reply, validate);
    }

    /// <summary>Consuming the reply clears the mark; validating it is the caller's choice.</summary>
    internal static FtpReply AcceptCompletionReply(AsyncFtpClient client, FtpReply reply, bool validate)
    {
        EndDataSequence(client);
        if (validate) ValidateCompletion(reply);
        return reply;
    }

    internal static void ValidateCompletion(FtpReply reply)
    {
        if (reply.Code is not ("226" or "250"))
            throw new IOException($"FTP transfer did not complete: {reply.Code} {reply.Message}");
    }

    /// <summary>
    /// Clears the desync flag. Separate from <see cref="CompleteDataSequence"/> only
    /// so the mark/clear invariant is reachable without a live control channel.
    /// </summary>
    internal static void EndDataSequence(AsyncFtpClient client)
        => PendingSequences.Remove(client);

    /// <summary>
    /// The server answered the data command with something other than 150/125.
    /// A FINAL negative reply (550 No such file, 425 Can't build data connection,
    /// 553 path-filter deny, ...) means no transfer began and no 226 will follow:
    /// the control channel is in sync and the connection is reusable, so the
    /// pending mark is cleared. A 421 (session closing) or a non-negative code
    /// (a stale reply — the desync signature) keeps the mark, and the pool
    /// discards the connection on return. Returns the exception for the caller to
    /// throw so the attribution lives next to the throw site.
    ///
    /// Before v3.10.112 every such rejection kept the mark: each hourly search-index
    /// build discarded two healthy connections, and a burst of `RETR 550` from a
    /// source that had just moved a release cost one login per reply against a
    /// 4-login cap (2026-09-08 03:02: ghost-kills, a 90 s BNC cooldown, four
    /// "Pool exhausted" transfer errors).
    /// </summary>
    internal static DataCommandRejectedException RejectDataCommand(AsyncFtpClient client, string verb, FtpReply reply)
    {
        var inSync = FtpCommandRejection.LeavesChannelInSync(reply.Code);
        if (inSync) EndDataSequence(client);
        return new DataCommandRejectedException(verb, reply, inSync);
    }

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
        AsyncFtpClient client, CancellationToken ct, System.Net.IPAddress? localBind = null)
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
        // localBind pins the DATA socket to one network adapter (multi-network downloads). The
        // control channel stays on the default route; glftpd already accepts data from an IP
        // other than the control one, because behind a BNC the control IP is the BNC's.
        return await ConnectDataSocket(client.Host, cpsvIp, port, localBind, ct);
    }

    /// <summary>
    /// Connects the data socket and records which control host it belongs to, so the TLS
    /// handshake can attribute what it observes about the peer to the right site.
    /// </summary>
    internal static async Task<TcpClient> ConnectDataSocket(string controlHost, string dataIp, int port,
        System.Net.IPAddress? localBind, CancellationToken ct)
    {
        var tcp = localBind == null
            ? new TcpClient()
            : new TcpClient(new System.Net.IPEndPoint(localBind, 0));
        try
        {
            await WithDataDeadline(async t => { await tcp.ConnectAsync(dataIp, port, t); return true; },
                "data connection", DataSetupDeadline, ct);
            AttributeDataSocket(tcp.Client, controlHost);
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

    // ---------------------------------------------------------------------
    // Data-channel TLS role.
    //
    // glftpd answers CPSV by doing SSL_connect on the data channel (it is the TLS client),
    // so we SSL_accept. ioFTPD also advertises CPSV in FEAT but stays the TLS SERVER on
    // the data channel; with both ends waiting for the other's ClientHello every transfer
    // hung for the full deadline (sitea, 2026-11-02: 0 of 35+ LISTs succeeded).
    //
    // Answering such a peer as TLS client from here is not an option: v3.19.7 tried it and
    // ioFTPD reset every SChannel ClientHello (3 of 3) while accepting OpenSSL's and GnuTLS's.
    // CPSV buys nothing on a daemon that stays server anyway, so the control HOST is marked
    // and FtpConnectionPool.UseCpsv turns false for it: the next transfer uses FluentFTP's
    // standard PASV path, where GnuTLS is the TLS client (the stack the control channel
    // already proves works). FXP mode detection follows the same flag.
    //
    // The role is a property of the PEER, so it is observed rather than inferred from FEAT
    // or a banner: a peer that speaks first is a client, a peer that stays silent through the
    // probe window is a server. Misreading a stalled glftpd backend as a TLS server would
    // move a working site off CPSV, so the evidence is graded:
    //   * a data IP or control host that has ever completed a glftpd-style handshake is never
    //     guessed about again - it waits the full deadline as before (a learned IP is
    //     forgotten if a handshake in that role fails);
    //   * an unproven host needs SilentStrikesToSwitch consecutive silent data channels;
    //   * a switch expires after CpsvSwitchLifetime without a validated standard listing.
    //     Successful PASV use renews it so healthy ioFTPD sites do not deliberately fail
    //     another CPSV transfer every six hours merely to rediscover the same TLS role.
    internal enum DataTlsPeerRole { Unknown, Client, Server }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<System.Net.IPAddress, DataTlsPeerRole>
        LearnedRoles = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>
        TlsServerCpsvHosts = new(StringComparer.OrdinalIgnoreCase);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte>
        ProvenClientHosts = new(StringComparer.OrdinalIgnoreCase);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int>
        SilentStrikes = new(StringComparer.OrdinalIgnoreCase);

    internal const int SilentStrikesToSwitch = 2;
    internal static readonly TimeSpan CpsvSwitchLifetime = TimeSpan.FromHours(6);

    // Data socket -> control host, so the handshake can attribute what it observes.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Socket, string> DataSocketHosts = new();

    /// <summary>How long a peer of unknown role gets to start the handshake.</summary>
    internal static readonly TimeSpan DataTlsRoleProbeWindow = TimeSpan.FromSeconds(3);

    internal static DataTlsPeerRole LearnedDataTlsRole(System.Net.IPAddress ip) =>
        LearnedRoles.TryGetValue(RoleKey(ip), out var role) ? role : DataTlsPeerRole.Unknown;

    internal static void ForgetDataTlsRole(System.Net.IPAddress ip) => LearnedRoles.TryRemove(RoleKey(ip), out _);

    /// <summary>True once this host's CPSV data peer was observed to stay TLS server.</summary>
    internal static bool IsCpsvUnusable(string? controlHost) => IsCpsvUnusable(controlHost, DateTime.UtcNow);

    internal static bool IsCpsvUnusable(string? controlHost, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(controlHost)) return false;
        while (TlsServerCpsvHosts.TryGetValue(controlHost, out var since))
        {
            if (nowUtc - since < CpsvSwitchLifetime) return true;
            // A listing can renew the lease between the read and removal. Re-read
            // after a lost compare/remove so a fresh lease still selects PASV.
            if (!TlsServerCpsvHosts.TryRemove(new KeyValuePair<string, DateTime>(controlHost, since)))
                continue;
            Log.Information("CPSV: re-trying CPSV data connections on {Host} after {Hours}h without a successful standard listing",
                controlHost, CpsvSwitchLifetime.TotalHours);
            return false;
        }
        return false;
    }

    /// <summary>Test hook: clears everything learned about one host or data IP.</summary>
    internal static void ForgetHost(string controlHost)
    {
        TlsServerCpsvHosts.TryRemove(controlHost, out _);
        ProvenClientHosts.TryRemove(controlHost, out _);
        SilentStrikes.TryRemove(controlHost, out _);
    }

    internal static void MarkCpsvUnusableAt(string controlHost, DateTime sinceUtc) =>
        TlsServerCpsvHosts[controlHost] = sinceUtc;

    /// <summary>
    /// Retain an existing fallback while validated standard data transfers keep working.
    /// This never classifies a host or recreates an entry removed by an expiry check.
    /// </summary>
    internal static void RecordStandardListingSuccess(string? controlHost) =>
        RecordStandardListingSuccess(controlHost, DateTime.UtcNow);

    internal static void RecordStandardListingSuccess(string? controlHost, DateTime completedUtc)
    {
        if (string.IsNullOrEmpty(controlHost)) return;
        while (TlsServerCpsvHosts.TryGetValue(controlHost, out var previous) && completedUtc > previous)
        {
            // A slower completion or a backwards wall-clock adjustment must not
            // shorten newer evidence. Compare/update also cannot resurrect a removed entry.
            if (TlsServerCpsvHosts.TryUpdate(controlHost, completedUtc, previous)) return;
        }
    }

    internal static void AttributeDataSocket(Socket socket, string controlHost) =>
        DataSocketHosts.AddOrUpdate(socket, controlHost);

    // Data sockets are dual-mode, so an IPv4 peer reports as ::ffff:a.b.c.d.
    private static System.Net.IPAddress RoleKey(System.Net.IPAddress ip) =>
        ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    /// <summary>
    /// Negotiates TLS on the data stream after the data command was sent. glftpd (and BNCs
    /// in front of it) SSL_connect to us, so we act as TLS server. A peer that never sends a
    /// ClientHello is a TLS server: its control host is marked CPSV-unusable and this throws
    /// <see cref="CpsvPeerIsTlsServerException"/> so the caller's next transfer uses PASV.
    /// </summary>
    internal static async Task<SslStream> NegotiateDataTls(NetworkStream networkStream, CancellationToken ct,
        TimeSpan? deadline = null, TimeSpan? roleProbeWindow = null)
    {
        System.Net.IPAddress? peerIp = null;
        string? controlHost = null;
        try
        {
            if (networkStream.Socket.RemoteEndPoint is System.Net.IPEndPoint ep) peerIp = RoleKey(ep.Address);
            DataSocketHosts.TryGetValue(networkStream.Socket, out controlHost);
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }

        var provenClient = peerIp is not null && LearnedDataTlsRole(peerIp) == DataTlsPeerRole.Client;
        var provenHost = controlHost is not null && ProvenClientHosts.ContainsKey(controlHost);

        // We are the TLS server; ClientCertificateRequired is false, so the remote
        // cert callback is not security-relevant here.
        var ssl = new SslStream(networkStream, true, (_, _, _, _) => true);
        try
        {
            await WithDataDeadline(async t =>
            {
                if (!provenClient && !provenHost
                    && !await PeerSpeaksFirst(networkStream.Socket, roleProbeWindow ?? DataTlsRoleProbeWindow, t))
                {
                    var strikes = controlHost is null ? 0 : SilentStrikes.AddOrUpdate(controlHost, 1, (_, n) => n + 1);
                    var switched = strikes >= SilentStrikesToSwitch
                                   && TlsServerCpsvHosts.TryAdd(controlHost!, DateTime.UtcNow);
                    if (switched)
                        Log.Warning("CPSV unusable on {Host}: {Strikes} consecutive data channels stayed silent as TLS server " +
                                    "(not glftpd, e.g. ioFTPD) — using standard PASV; CPSV retries after {Hours}h without a successful standard listing",
                            controlHost, strikes, CpsvSwitchLifetime.TotalHours);
                    throw new CpsvPeerIsTlsServerException(controlHost, peerIp, switched);
                }

                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = SelfSignedCert.Value,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, t);
                return true;
            }, "data channel TLS handshake", deadline ?? DataSetupDeadline, ct);
        }
        catch (Exception ex)
        {
            ssl.Dispose();
            // Only the remembered role is evidence against itself; a probe that failed
            // proves nothing about which side the peer plays.
            if (provenClient && ex is not OperationCanceledException && peerIp is not null
                && LearnedRoles.TryRemove(peerIp, out _))
                Log.Information("Data TLS: handshake with {Peer} failed in its learned client role — re-probing next time",
                    peerIp);
            throw;
        }

        if (peerIp is not null && !provenClient) LearnedRoles[peerIp] = DataTlsPeerRole.Client;
        // A proven host never accrues strikes again, so its count needs no reset.
        if (controlHost is not null) ProvenClientHosts.TryAdd(controlHost, 0);
        Log.Debug("Data TLS: server mode ({Protocol}, {Cipher})", ssl.SslProtocol, ssl.NegotiatedCipherSuite);
        return ssl;
    }

    /// <summary>
    /// True once the peer has sent anything (or closed) — a TLS client's ClientHello.
    /// Polls instead of a cancellable receive so abandoning the wait leaves the socket intact.
    /// </summary>
    private static async Task<bool> PeerSpeaksFirst(Socket socket, TimeSpan window, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (socket.Poll(0, SelectMode.SelectRead)) return true;
            if (sw.Elapsed >= window) return false;
            await Task.Delay(20, ct);
        }
    }

    internal static readonly TimeSpan DataSetupDeadline = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs one data-channel setup step under its own deadline. That deadline expiring is a
    /// TRANSPORT failure, so it is thrown as <see cref="DataChannelTimeoutException"/>
    /// (an IOException); only <paramref name="ct"/> produces an OperationCanceledException.
    /// A bare linked-token timeout used to leak out as a cancellation, which every caller
    /// read as "the caller stopped me": SpreadJob called it the 180s transfer ceiling,
    /// FxpTransfer's telemetry said "cancelled", DownloadManager marked the item Cancelled.
    /// </summary>
    internal static async Task<T> WithDataDeadline<T>(Func<CancellationToken, Task<T>> step,
        string phase, TimeSpan deadline, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(deadline);
        try
        {
            return await step(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DataChannelTimeoutException(phase, deadline);
        }
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
                throw RejectDataCommand(client, "LIST", listReply);

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
                throw RejectDataCommand(client, "RETR", retrReply);

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
                throw RejectDataCommand(client, "RETR", retrReply);

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
                throw RejectDataCommand(client, "STOR", storReply);

            var ssl = await NegotiateDataTls(tcp.GetStream(), ct);

            try
            {
                await ssl.WriteAsync(data, ct);
                await ssl.FlushAsync(ct);

                ssl.Close();
                tcp.Close();

                var completeReply = await CompleteDataSequence(client, ct, validate: false);
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
                throw RejectDataCommand(client, "STOR", storReply);

            var ssl = await NegotiateDataTls(tcp.GetStream(), ct);
            try
            {
                stream.Position = 0;
                await stream.CopyToAsync(ssl, ct);
                ssl.Close();
                tcp.Close();
                var completeReply = await CompleteDataSequence(client, ct, validate: false);
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
