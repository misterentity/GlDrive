using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using FluentFTP;
using FluentFTP.Exceptions;
using FluentFTP.Exceptions;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Downloads.Segmented;

/// <summary>
/// One FTP connection used as a download lane: the main pool's connection, or the site's race
/// login borrowed while the site is not racing.
///
/// Stopping mid-file is only offered on CPSV sites, whose data channel is a .NET SslStream:
/// closing it costs nothing but a 426 on the control channel, which we read and then prove in
/// sync with NOOP→200. That clean finish is what lets a borrowed race login go back through
/// <c>Return</c> — the only path that wakes a race already waiting on the spread pool (a
/// <c>Discard</c> does not). Standard-PASV data is GnuTLS, where an early stop is the crash
/// lineage in Ftp/CLAUDE.md, so those lanes only take ranges that run to EOF.
/// </summary>
internal sealed class FtpLane : ISegmentLane
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);

    private readonly FtpConnectionPool? _reborrowPool;
    private readonly NetworkPath _network;
    private readonly Func<bool>? _yieldWhen;
    private readonly Func<NetworkPath, bool>? _overAllowance;
    private readonly Action? _onDisposed;
    private PooledConnection? _conn;
    private bool _warnedUnboundData;

    /// <param name="reborrowPool">Pool to re-borrow from after a fault; null for a borrowed race login (it leaves instead).</param>
    /// <param name="yieldWhen">Borrowed race login: true once the site wants to race.</param>
    public FtpLane(PooledConnection conn, FtpConnectionPool? reborrowPool, bool useCpsv, NetworkPath network,
        string label, Func<bool>? yieldWhen = null, Func<NetworkPath, bool>? overAllowance = null, Action? onDisposed = null)
    {
        _conn = conn;
        _reborrowPool = reborrowPool;
        CanStopEarly = useCpsv;
        _network = network;
        Label = label;
        _yieldWhen = yieldWhen;
        _overAllowance = overAllowance;
        _onDisposed = onDisposed;
    }

    public string Label { get; }
    public string? NetworkId => _network.Id;
    public bool CanStopEarly { get; }
    // A borrowed race login that lost its connection cannot reconnect — leave rather than retry.
    // The race check takes SpreadManager's lock, so sample it at most every 250ms instead of on
    // every buffer read; that still hands the login back well inside a second.
    public bool ShouldYield
    {
        get
        {
            if (_conn == null && _reborrowPool == null) return true;
            if (_yieldWhen == null) return false;
            var now = Environment.TickCount64;
            if (now - _lastYieldCheck >= 250)
            {
                _lastYieldCheck = now;
                _yieldCached = _yieldWhen();
            }
            return _yieldCached;
        }
    }
    private long _lastYieldCheck = long.MinValue / 2;
    private bool _yieldCached;

    /// <summary>
    /// Remote modification time (MDTM, "yyyyMMddHHmmss"), or null if the server won't say. With the
    /// size it fingerprints the remote version, so a resume never splices two versions together.
    /// </summary>
    public async Task<string?> GetModifiedStampAsync(string remotePath, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var reply = await client.Execute($"MDTM {CpsvDataHelper.SanitizeFtpPath(remotePath)}", ct);
        var stamp = reply.Message.Trim();
        return reply.Code == "213" && stamp.Length >= 14 ? stamp[..14] : null;
    }

    /// <summary>Size of a remote file over this lane's control connection, -1 if the server won't say.</summary>
    public async Task<long> GetSizeAsync(string remotePath, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        if (!CanStopEarly) return await client.GetFileSize(remotePath, -1, ct);
        var reply = await client.Execute($"SIZE {CpsvDataHelper.SanitizeFtpPath(remotePath)}", ct);
        return reply.Success && long.TryParse(reply.Message.Trim(), out var size) ? size : -1;
    }

    public async Task<ISegmentReader> OpenAsync(TransferFile file, long offset, long end, CancellationToken ct)
    {
        if (_overAllowance?.Invoke(_network) == true)
            throw new NetworkAllowanceExhaustedException(_network.DisplayName);

        var client = await ClientAsync(ct);
        if (!CanStopEarly)
        {
            if (!_network.IsDefault && !_warnedUnboundData)
            {
                _warnedUnboundData = true;
                Log.Information("Download: {Server} is not CPSV — its data connections use the default route, not {Network}",
                    client.Host, _network.DisplayName);
            }
            Stream stream;
            try
            {
                stream = offset > 0
                    ? await client.OpenRead(file.Key, FtpDataType.Binary, offset, token: ct)
                    : await client.OpenRead(file.Key, token: ct);
            }
            catch (FtpCommandException ex) when (offset > 0 && ex.CompletionCode is "500" or "501" or "502" or "504")
            {
                // FluentFTP sends REST before RETR; a syntax/not-implemented refusal means this
                // server can't resume mid-file.
                throw new RangeNotSupportedException($"REST {offset} refused: {ex.CompletionCode} {ex.Message}");
            }
            return new StandardReader(client, stream);
        }

        // Each CPSV RETR is one complete data sequence; OpenDataTcp marks the control channel
        // as owing replies and only a read completion reply clears it.
        await client.Execute("TYPE I", ct);
        var tcp = await CpsvDataHelper.OpenDataTcp(client, ct, _network.BindAddress);
        try
        {
            if (offset > 0)
            {
                var rest = await client.Execute($"REST {offset}", ct);
                if (!rest.Success)
                    throw new RangeNotSupportedException($"REST {offset} refused: {rest.Code} {rest.Message}");
            }
            var retr = await client.Execute($"RETR {CpsvDataHelper.SanitizeFtpPath(file.Key)}", ct);
            if (retr.Code is not ("150" or "125"))
                throw CpsvDataHelper.RejectDataCommand(client, "RETR", retr);

            var ssl = await CpsvDataHelper.NegotiateDataTls(tcp.GetStream(), ct);
            return new CpsvReader(client, tcp, ssl);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public void Fault(Exception ex)
    {
        var conn = Interlocked.Exchange(ref _conn, null);
        if (conn == null) return;
        conn.Poison($"download lane {Label}: {ex.GetType().Name}");
        _ = conn.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        var conn = Interlocked.Exchange(ref _conn, null);
        if (conn != null) await conn.DisposeAsync();
        _onDisposed?.Invoke();
    }

    private async Task<AsyncFtpClient> ClientAsync(CancellationToken ct)
    {
        if (_conn != null) return _conn.Client;
        if (_reborrowPool == null)
            throw new IOException($"{Label}: connection was lost and this lane cannot reconnect");
        _conn = await _reborrowPool.Borrow(ct);
        return _conn.Client;
    }

    /// <summary>
    /// Await a control-channel read without ever cancelling it: cancelling FluentFTP's read
    /// abandons a native GnuTLS recv, which SEGVs on teardown (see FxpTransfer.ReadReplyManagedTimeout).
    /// On timeout the read is left draining and we throw, so the engine faults (poisons) the lane.
    /// </summary>
    internal static async Task<FtpReply> ManagedReply(Task<FtpReply> read, string what)
    {
        var winner = await Task.WhenAny(read, Task.Delay(ReplyTimeout));
        if (winner == read) return await read;
        _ = read.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
        throw new IOException($"{what}: no reply within {ReplyTimeout.TotalSeconds:0}s");
    }

    /// <summary>
    /// After an early data close the server answers 426 (or 226 if it had already sent
    /// everything). Read that, then require NOOP→200 so a straggling second reply can't be left
    /// for the next borrower — a borrowed race login goes back to racing after this.
    /// </summary>
    internal static async Task ResyncAfterAbortAsync(AsyncFtpClient client)
    {
        await ManagedReply(CpsvDataHelper.CompleteDataSequence(client, CancellationToken.None, validate: false),
            "RETR abort reply");

        var noop = await ManagedReply(client.Execute("NOOP", CancellationToken.None), "NOOP after abort");
        for (var i = 0; i < 2 && noop.Code != "200"; i++)
        {
            if (noop.Code is not ("226" or "426" or "450" or "451"))
                break;
            noop = await ManagedReply(client.GetReply(CancellationToken.None), "NOOP after abort");
        }
        if (noop.Code != "200")
            throw new IOException($"control channel not in sync after abort: {noop.Code} {noop.Message}");
    }

    private sealed class CpsvReader(AsyncFtpClient client, TcpClient tcp, SslStream ssl) : ISegmentReader
    {
        private bool _closed;

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => ssl.ReadAsync(buffer, ct);

        public async Task FinishAsync(bool reachedEof, CancellationToken ct)
        {
            if (reachedEof)
            {
                var probe = new byte[1];
                if (await ssl.ReadAsync(probe, ct) != 0)
                    throw new IOException("server sent more bytes than the file size");
                Close();
                await ManagedReply(CpsvDataHelper.CompleteDataSequence(client, CancellationToken.None), "RETR completion");
                return;
            }

            Close();
            await ResyncAfterAbortAsync(client);
        }

        private void Close()
        {
            if (_closed) return;
            _closed = true;
            try { ssl.Dispose(); } catch { }
            try { tcp.Dispose(); } catch { }
        }

        public ValueTask DisposeAsync()
        {
            Close();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StandardReader(AsyncFtpClient client, Stream stream) : ISegmentReader
    {
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => stream.ReadAsync(buffer, ct);

        public async Task FinishAsync(bool reachedEof, CancellationToken ct)
        {
            if (!reachedEof)
                throw new IOException("standard-PASV lane cannot stop mid-file");
            var probe = new byte[1];
            if (await stream.ReadAsync(probe, ct) != 0)
                throw new IOException("server sent more bytes than the file size");
            stream.Close();
            var reply = await client.GetReply(ct);
            if (!reply.Success)
                throw new IOException($"RETR did not complete: {reply.Code} {reply.Message}");
        }

        public async ValueTask DisposeAsync() => await stream.DisposeAsync();
    }
}

/// <summary>Every selected network has used its data allowance for the current period.</summary>
public sealed class NetworkAllowanceExhaustedException(string network)
    : IOException($"Data allowance reached on {network}")
{
    public string Network { get; } = network;
}
