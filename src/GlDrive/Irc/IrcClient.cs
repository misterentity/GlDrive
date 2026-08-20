using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using GlDrive.Tls;
using Serilog;

namespace GlDrive.Irc;

/// <summary>
/// Low-level IRC client using TcpClient + SslStream.
/// Handles connection, read loop, and raw message send/receive.
/// </summary>
public class IrcClient : IDisposable
{
    private TcpClient? _tcp;
    private Stream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _readTask;
    private CancellationTokenSource? _cts;

    public event Action<IrcMessage>? MessageReceived;
    public event Action? Connected;
    public event Action<string>? Disconnected;

    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(string host, int port, bool useTls, CertificateManager? certManager, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _tcp = new TcpClient();
        _tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        _tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 60);
        _tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 15);
        _tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);

        await _tcp.ConnectAsync(host, port, _cts.Token);

        if (useTls)
        {
            var sslStream = new SslStream(_tcp.GetStream(), false, (sender, cert, chain, errors) =>
            {
                if (cert == null) return false;
                if (certManager != null)
                {
                    // Clear sync context to avoid deadlock when blocking on async TOFU validation
                    var prevCtx = SynchronizationContext.Current;
                    SynchronizationContext.SetSynchronizationContext(null);
                    try
                    {
                        return certManager.ValidateCertificate(host, port, cert)
                            .ConfigureAwait(false).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(prevCtx);
                    }
                }
                return false;
            });

            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host
            }, _cts.Token);

            _stream = sslStream;
        }
        else
        {
            _stream = _tcp.GetStream();
        }

        _reader = new StreamReader(_stream, Encoding.Latin1);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

        IsConnected = true;
        Connected?.Invoke();

        _readTask = Task.Run(() => ReadLoop(_cts.Token), _cts.Token);
    }

    /// <summary>
    /// Test seam: wire the writer to an arbitrary stream without a socket, so the send path can
    /// be exercised against a stream that enforces SslStream's one-write-at-a-time contract.
    /// Mirrors exactly how <see cref="ConnectAsync"/> builds the writer.
    /// </summary>
    internal void AttachStreamForTests(Stream stream)
    {
        _stream = stream;
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
        IsConnected = true;
    }

    // RFC 1459 is 512 bytes; IRCv3 message tags extend modestly. Nothing legitimate exceeds 16 KB.
    private const int MaxLineLength = 16 * 1024;

    private async Task ReadLoop(CancellationToken ct)
    {
        try
        {
            var buffer = new char[MaxLineLength];
            while (!ct.IsCancellationRequested && _reader != null)
            {
                // Only a failure of the READ itself (socket/IO error, cancellation, bounded-line
                // overflow) means the connection is gone and the loop should exit → reconnect.
                var line = await ReadLineBoundedAsync(_reader, buffer, ct);
                if (line == null) break;

                // Parsing and handler dispatch run in an isolated try so that a single
                // malformed message or a bug in a message handler (e.g. a FiSH decrypt
                // throwing on a bad key) is logged and the message dropped — it must NEVER
                // tear down the whole IRC connection. This was the root cause of spurious
                // IRC disconnects: a DH1080-derived over-length Blowfish key threw out of
                // the FiSH decrypt path, unwound the read loop, and forced a full reconnect.
                try
                {
                    // Inbound needs redaction too: the server echoes the channel key back
                    // in MODE and in 324 RPL_CHANNELMODEIS.
                    Log.Verbose("[IRC <] {Line}", IrcLineRedactor.Redact(line));
                    var msg = IrcMessage.Parse(line);

                    // Auto-reply to server PING immediately (works even before/without a handler
                    // attached). Do NOT `continue` — the PING must still flow to the handler so the
                    // liveness tracker counts it as inbound traffic. Otherwise a quiet connection
                    // whose only inbound data is periodic server PINGs looks "dead" to the 180s
                    // liveness check and gets a needless disconnect+reconnect (which can trip a
                    // ~2h BNC reconnect cooldown).
                    if (msg.Command == "PING")
                        await SendRawAsync($"PONG :{msg.Trailing ?? msg.Params.FirstOrDefault() ?? ""}");

                    MessageReceived?.Invoke(msg);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "IRC message handling error — message dropped, connection kept");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex)
        {
            Log.Debug(ex, "IRC read loop IO error");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "IRC read loop error");
        }
        finally
        {
            IsConnected = false;
            Disconnected?.Invoke("Connection closed");
        }
    }

    /// <summary>
    /// Reads a single line from the reader, bounded to MaxLineLength characters.
    /// Hostile IRC servers could stream unterminated lines; this prevents memory exhaustion.
    /// Throws IOException if a line exceeds the bound.
    /// </summary>
    private static async Task<string?> ReadLineBoundedAsync(StreamReader reader, char[] buffer, CancellationToken ct)
    {
        var pos = 0;
        while (pos < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(pos, 1), ct);
            if (read == 0)
                return pos == 0 ? null : new string(buffer, 0, pos);

            var c = buffer[pos];
            if (c == '\n')
            {
                var end = pos;
                if (end > 0 && buffer[end - 1] == '\r') end--;
                return new string(buffer, 0, end);
            }
            pos++;
        }
        throw new IOException($"IRC line exceeds {MaxLineLength} bytes — possible hostile server");
    }

    /// <summary>
    /// Serializes the send path. SslStream permits exactly ONE in-flight write and throws
    /// NotSupportedException on a second, and StreamWriter's internal char buffer is not
    /// thread-safe either — so an unguarded send both drops lines and can splice two commands
    /// into one corrupt line. Every IRC command funnels through <see cref="SendRawAsync"/> from
    /// genuinely concurrent callers: the read loop's PONG reply, the keepalive PING timer, the
    /// invite-only JOIN retry timer, and user/wishlist PRIVMSGs.
    ///
    /// Same class of defect, same remedy as <c>SerializedGnuTlsStream</c> (v3.10.22): one
    /// semaphore across every write to the shared stream.
    /// </summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public async Task SendRawAsync(string line)
    {
        // Capture once: DisconnectAsync nulls and disposes _writer concurrently, so re-reading
        // the field after the check could hand the write a disposed or null writer.
        var writer = _writer;
        if (writer == null) return;

        line = line.Replace("\r", "").Replace("\n", "");

        await _sendLock.WaitAsync();
        try
        {
            // Redact credential parameters (channel keys, services passwords) — NOT just
            // PASS, which is what this used to check. See IrcLineRedactor.
            Log.Verbose("[IRC >] {Line}", IrcLineRedactor.Redact(line));
            await writer.WriteLineAsync(line);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to send IRC line");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task NickAsync(string nick) => SendRawAsync($"NICK {nick}");
    public Task UserAsync(string username, string realname) => SendRawAsync($"USER {username} 0 * :{realname}");
    public Task PassAsync(string password) => SendRawAsync($"PASS {password}");
    public Task JoinAsync(string channel, string? key = null) =>
        key != null ? SendRawAsync($"JOIN {channel} {key}") : SendRawAsync($"JOIN {channel}");
    public Task PartAsync(string channel, string? message = null) =>
        message != null ? SendRawAsync($"PART {channel} :{message}") : SendRawAsync($"PART {channel}");
    public Task PrivmsgAsync(string target, string text) => SendRawAsync($"PRIVMSG {target} :{text}");
    public Task NoticeAsync(string target, string text) => SendRawAsync($"NOTICE {target} :{text}");
    public Task QuitAsync(string? message = null) =>
        SendRawAsync(message != null ? $"QUIT :{message}" : "QUIT :GlDrive");
    public Task PongAsync(string token) => SendRawAsync($"PONG :{token}");

    /// <summary>
    /// Signals the read loop to stop by cancelling the internal CTS without
    /// immediately disposing streams. The read loop exit path fires Disconnected,
    /// allowing RunAsync to handle cleanup and reconnect uniformly.
    /// </summary>
    public void SignalDisconnect() => _cts?.Cancel();

    public async Task DisconnectAsync()
    {
        try
        {
            if (IsConnected)
                await QuitAsync();
        }
        catch { }

        Cleanup();
    }

    private void Cleanup()
    {
        IsConnected = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _reader?.Dispose();
        _writer?.Dispose();
        _stream?.Dispose();
        _tcp?.Dispose();
        _reader = null;
        _writer = null;
        _stream = null;
        _tcp = null;
    }

    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }
}
