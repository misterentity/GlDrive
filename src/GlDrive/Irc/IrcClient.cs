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

        await _tcp.ConnectAsync(host, port, _cts.Token);

        if (useTls)
        {
            var sslStream = new SslStream(_tcp.GetStream(), false, (sender, cert, chain, errors) =>
            {
                if (cert == null) return false;
                if (certManager != null)
                    return Task.Run(() => certManager.ValidateCertificate(host, port, cert)).GetAwaiter().GetResult();
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

        _reader = new StreamReader(_stream, Encoding.UTF8);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

        IsConnected = true;
        Connected?.Invoke();

        _readTask = Task.Run(() => ReadLoop(_cts.Token), _cts.Token);
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _reader != null)
            {
                var line = await _reader.ReadLineAsync(ct);
                if (line == null) break;

                Log.Verbose("[IRC <] {Line}", line);
                var msg = IrcMessage.Parse(line);

                // Auto PONG
                if (msg.Command == "PING")
                {
                    await SendRawAsync($"PONG :{msg.Trailing ?? msg.Params.FirstOrDefault() ?? ""}");
                    continue;
                }

                MessageReceived?.Invoke(msg);
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

    public async Task SendRawAsync(string line)
    {
        if (_writer == null) return;
        try
        {
            line = line.Replace("\r", "").Replace("\n", "");
            var logLine = line.StartsWith("PASS ", StringComparison.OrdinalIgnoreCase) ? "PASS [REDACTED]" : line;
            Log.Verbose("[IRC >] {Line}", logLine);
            await _writer.WriteLineAsync(line);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to send IRC line");
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
