using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using FluentFTP;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Spread;

public enum TransferState { Idle, NegotiatingPassive, NegotiatingActive, Transferring, Complete, Error }

public class FxpTransfer
{
    private static readonly Regex PasvRegex = new(@"\((\d+),(\d+),(\d+),(\d+),(\d+),(\d+)\)", RegexOptions.Compiled);

    public event Action<TransferState>? StateChanged;
    public event Action<long>? BytesTransferred;

    public TransferState State { get; private set; } = TransferState.Idle;
    public long TotalBytes { get; private set; }
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Optional callback invoked just before STOR to create destination directories.
    /// Only called after PASV/PORT negotiation succeeds, preventing empty dir creation on failure.
    /// </summary>
    public Func<CancellationToken, Task>? BeforeStore { get; set; }

    public async Task<bool> ExecuteAsync(
        PooledConnection source, PooledConnection dest,
        string srcPath, string dstPath,
        FxpMode mode, int transferTimeoutSeconds,
        CancellationToken ct,
        string raceId = "", string srcServerId = "", string dstServerId = "",
        long fileSizeBytes = 0)
    {
        var totalSw = Stopwatch.StartNew();
        // pasvLatencyMs: time from method entry until data transfer starts (negotiation phase).
        // We measure this as the time until SetState(Transferring) is called inside each mode method,
        // which captures PASV/CPSV command round-trips + PORT/connection setup.
        // ttfbMs: FluentFTP server-to-server transfers don't expose a first-byte hook — set to 0.
        // For Relay mode, we could measure the first ReadAsync, but for consistency across all
        // modes we set ttfbMs=0 and put the full negotiation cost into pasvLatencyMs.
        var pasvSw = Stopwatch.StartNew();
        bool ok;
        string? abortReason = null;

        try
        {
            // When both support CPSV, try CpsvPasv first (source CPSV, dest regular PASV).
            // If that fails (BNC backend can't reach dest), fall back to Relay (pipe through local).
            // IMPORTANT: After CpsvPasv fails, the GnuTLS session may be corrupted internally
            // (FluentFTP bug: negative error codes used as array indices in Write).
            // Poison both connections so they're discarded after this transfer attempt.
            if (mode == FxpMode.Relay)
            {
                try
                {
                    ok = await ExecuteCpsvPasv(source.Client, dest.Client, srcPath, dstPath, transferTimeoutSeconds, ct, pasvSw);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Debug("CpsvPasv failed ({Error}), trying Relay mode — connections will be poisoned", ex.Message);
                    source.Poisoned = true;
                    dest.Poisoned = true;
                    pasvSw.Restart();
                    ok = await ExecuteRelay(source.Client, dest.Client, srcPath, dstPath, transferTimeoutSeconds, ct, pasvSw);
                }
            }
            else
            {
                ok = mode switch
                {
                    FxpMode.PasvPasv => await ExecutePasvPasv(source.Client, dest.Client, srcPath, dstPath, transferTimeoutSeconds, ct, pasvSw),
                    FxpMode.CpsvPasv => await ExecuteCpsvPasv(source.Client, dest.Client, srcPath, dstPath, transferTimeoutSeconds, ct, pasvSw),
                    FxpMode.PasvCpsv => await ExecutePasvCpsv(source.Client, dest.Client, srcPath, dstPath, transferTimeoutSeconds, ct, pasvSw),
                    FxpMode.Relay    => await ExecuteRelay(source.Client, dest.Client, srcPath, dstPath, transferTimeoutSeconds, ct, pasvSw),
                    _ => throw new ArgumentOutOfRangeException(nameof(mode))
                };
            }

            if (!ok) abortReason = ErrorMessage ?? "transfer failed";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pasvSw.Stop();   // stop here so PasvLatencyMs reflects negotiation phase, not full failed attempt
            SetState(TransferState.Error);
            ErrorMessage = ex.Message;
            abortReason = ex.Message;
            ok = false;
            Log.Warning(ex, "FXP transfer failed: {Src} -> {Dst}", srcPath, dstPath);
        }

        totalSw.Stop();

        try
        {
            var recorder = App.TelemetryRecorder;
            recorder?.Record(GlDrive.AiAgent.TelemetryStream.Transfers, new GlDrive.AiAgent.FileTransferEvent
            {
                RaceId        = raceId,
                SrcServer     = srcServerId,
                DstServer     = dstServerId,
                File          = srcPath,
                Bytes         = TotalBytes > 0 ? TotalBytes : fileSizeBytes,  // Measured (Relay) else expected (size was passed at call site)
                ElapsedMs     = totalSw.ElapsedMilliseconds,
                TtfbMs        = 0,   // not measurable via FluentFTP server-to-server API
                PasvLatencyMs = pasvSw.ElapsedMilliseconds,
                AbortReason   = abortReason
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "FXP telemetry emit failed (non-fatal)");
        }

        return ok;
    }

    /// <summary>
    /// Send TYPE I and verify the response before proceeding.
    /// FluentFTP's Execute() reads the next response from the wire — if we don't
    /// verify, a stale/delayed response can desync the entire command queue.
    /// </summary>
    private static async Task SendTypeI(AsyncFtpClient client, string label, CancellationToken ct)
    {
        var reply = await client.Execute("TYPE I", ct);
        if (!reply.Success)
            Log.Warning("TYPE I unexpected response on {Label}: {Code} {Msg}", label, reply.Code, reply.Message);
    }

    /// <summary>
    /// Enable Secure Server-to-Server Connection Negotiation (SSCN ON) for secure FXP.
    /// Required by most glftpd sites — without this, the data channel between servers
    /// is unencrypted and will be rejected by sites requiring secure transfers.
    /// SSCN ON tells the server to negotiate TLS on the FXP data channel.
    /// Only needs to be sent once per session but is safe to re-send.
    /// </summary>
    private static async Task EnableSscn(AsyncFtpClient client, string label, CancellationToken ct)
    {
        var reply = await client.Execute("SSCN ON", ct);
        if (reply.Code == "200")
            Log.Debug("SSCN ON enabled on {Label}", label);
        else
            Log.Debug("SSCN ON response on {Label}: {Code} {Msg} (may not be supported)",
                label, reply.Code, reply.Message);
    }

    private async Task<bool> ExecutePasvPasv(
        AsyncFtpClient src, AsyncFtpClient dst,
        string srcPath, string dstPath,
        int timeoutSec, CancellationToken ct,
        Stopwatch pasvSw)
    {
        SetState(TransferState.NegotiatingPassive);

        await SendTypeI(src, "source", ct);
        await SendTypeI(dst, "dest", ct);

        // Enable secure FXP (SSCN) — passive side accepts TLS, active side initiates
        await EnableSscn(dst, "dest-passive", ct);
        await EnableSscn(src, "source-active", ct);

        // Dest enters PASV
        var pasvReply = await dst.Execute("PASV", ct);
        if (!pasvReply.Success)
            throw new IOException($"PASV failed on dest: {pasvReply.Code} {pasvReply.Message}");

        var pasvAddr = ExtractPasvAddress(pasvReply.Message);

        SetState(TransferState.NegotiatingActive);

        // Source connects to dest's PASV address via PORT
        var portReply = await src.Execute($"PORT {pasvAddr}", ct);
        if (!portReply.Success)
            throw new IOException($"PORT failed on source: {portReply.Code} {portReply.Message}");

        // Create dest directory now that connection is established (prevents empty dirs on failure)
        if (BeforeStore != null) await BeforeStore(ct);

        // Start STOR on dest, then RETR on source
        var storReply = await dst.Execute($"STOR {Ftp.CpsvDataHelper.SanitizeFtpPath(dstPath)}", ct);
        if (storReply.Code != "150" && storReply.Code != "125")
            throw new IOException($"STOR failed: {storReply.Code} {storReply.Message}");

        var retrReply = await src.Execute($"RETR {Ftp.CpsvDataHelper.SanitizeFtpPath(srcPath)}", ct);
        if (retrReply.Code != "150" && retrReply.Code != "125")
            throw new IOException($"RETR failed: {retrReply.Code} {retrReply.Message}");

        pasvSw.Stop(); // negotiation complete — data is now flowing
        SetState(TransferState.Transferring);

        // Wait for completion with timeout
        return await WaitForTransferComplete(src, dst, timeoutSec, ct);
    }

    private async Task<bool> ExecuteCpsvPasv(
        AsyncFtpClient src, AsyncFtpClient dst,
        string srcPath, string dstPath,
        int timeoutSec, CancellationToken ct,
        Stopwatch pasvSw)
    {
        // Source is CPSV (behind BNC, can't PASV). Dest does PASV, source PORTs to it.
        SetState(TransferState.NegotiatingPassive);

        await SendTypeI(src, "source", ct);
        await SendTypeI(dst, "dest", ct);
        await EnableSscn(dst, "dest-passive", ct);
        await EnableSscn(src, "source-active", ct);

        var pasvReply = await dst.Execute("PASV", ct);
        if (!pasvReply.Success)
            throw new IOException($"PASV failed on dest: {pasvReply.Code} {pasvReply.Message}");

        var pasvAddr = ExtractPasvAddress(pasvReply.Message);

        SetState(TransferState.NegotiatingActive);

        var portReply = await src.Execute($"PORT {pasvAddr}", ct);
        if (!portReply.Success)
            throw new IOException($"PORT failed on source: {portReply.Code} {portReply.Message}");

        if (BeforeStore != null) await BeforeStore(ct);

        var storReply = await dst.Execute($"STOR {Ftp.CpsvDataHelper.SanitizeFtpPath(dstPath)}", ct);
        if (storReply.Code != "150" && storReply.Code != "125")
            throw new IOException($"STOR failed: {storReply.Code} {storReply.Message}");

        var retrReply = await src.Execute($"RETR {Ftp.CpsvDataHelper.SanitizeFtpPath(srcPath)}", ct);
        if (retrReply.Code != "150" && retrReply.Code != "125")
            throw new IOException($"RETR failed: {retrReply.Code} {retrReply.Message}");

        pasvSw.Stop(); // negotiation complete — data is now flowing
        SetState(TransferState.Transferring);
        return await WaitForTransferComplete(src, dst, timeoutSec, ct);
    }

    private async Task<bool> ExecutePasvCpsv(
        AsyncFtpClient src, AsyncFtpClient dst,
        string srcPath, string dstPath,
        int timeoutSec, CancellationToken ct,
        Stopwatch pasvSw)
    {
        // Dest is CPSV. Source does PASV, dest PORTs to source.
        SetState(TransferState.NegotiatingPassive);

        await SendTypeI(src, "source", ct);
        await SendTypeI(dst, "dest", ct);
        await EnableSscn(src, "source-passive", ct);
        await EnableSscn(dst, "dest-active", ct);

        var pasvReply = await src.Execute("PASV", ct);
        if (!pasvReply.Success)
            throw new IOException($"PASV failed on source: {pasvReply.Code} {pasvReply.Message}");

        var pasvAddr = ExtractPasvAddress(pasvReply.Message);

        SetState(TransferState.NegotiatingActive);

        var portReply = await dst.Execute($"PORT {pasvAddr}", ct);
        if (!portReply.Success)
            throw new IOException($"PORT failed on dest: {portReply.Code} {portReply.Message}");

        if (BeforeStore != null) await BeforeStore(ct);

        var storReply = await dst.Execute($"STOR {Ftp.CpsvDataHelper.SanitizeFtpPath(dstPath)}", ct);
        if (storReply.Code != "150" && storReply.Code != "125")
            throw new IOException($"STOR failed: {storReply.Code} {storReply.Message}");

        var retrReply = await src.Execute($"RETR {Ftp.CpsvDataHelper.SanitizeFtpPath(srcPath)}", ct);
        if (retrReply.Code != "150" && retrReply.Code != "125")
            throw new IOException($"RETR failed: {retrReply.Code} {retrReply.Message}");

        pasvSw.Stop(); // negotiation complete — data is now flowing
        SetState(TransferState.Transferring);
        return await WaitForTransferComplete(src, dst, timeoutSec, ct);
    }

    private async Task<bool> ExecuteRelay(
        AsyncFtpClient src, AsyncFtpClient dst,
        string srcPath, string dstPath,
        int timeoutSec, CancellationToken ct,
        Stopwatch pasvSw)
    {
        // Both CPSV — neither backend IP is routable. Relay through local memory.
        SetState(TransferState.NegotiatingPassive);

        await SendTypeI(src, "source", ct);
        await SendTypeI(dst, "dest", ct);

        // Open CPSV data connections to both
        var srcTcp = await CpsvDataHelper.OpenDataTcp(src, ct);
        TcpClient? dstTcp = null;
        SslStream? srcSsl = null;
        SslStream? dstSsl = null;

        try
        {
            dstTcp = await CpsvDataHelper.OpenDataTcp(dst, ct);

            SetState(TransferState.NegotiatingActive);

            if (BeforeStore != null) await BeforeStore(ct);

            // Send data commands
            var retrReply = await src.Execute($"RETR {Ftp.CpsvDataHelper.SanitizeFtpPath(srcPath)}", ct);
            if (retrReply.Code != "150" && retrReply.Code != "125")
                throw new IOException($"RETR failed: {retrReply.Code} {retrReply.Message}");

            var storReply = await dst.Execute($"STOR {Ftp.CpsvDataHelper.SanitizeFtpPath(dstPath)}", ct);
            if (storReply.Code != "150" && storReply.Code != "125")
                throw new IOException($"STOR failed: {storReply.Code} {storReply.Message}");

            // Negotiate TLS as server on both (glftpd does SSL_connect)
            srcSsl = await CpsvDataHelper.NegotiateDataTls(srcTcp.GetStream(), ct);
            dstSsl = await CpsvDataHelper.NegotiateDataTls(dstTcp.GetStream(), ct);

            pasvSw.Stop(); // TCP+TLS setup done — relay loop about to start
            SetState(TransferState.Transferring);

            // Double-buffered relay: read next chunk while writing current
            var buf1 = new byte[256 * 1024];
            var buf2 = new byte[256 * 1024];
            long totalRelayed = 0;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec > 0 ? timeoutSec * 20 : 24000));

            var rd = await srcSsl.ReadAsync(buf1, timeoutCts.Token);
            while (rd > 0)
            {
                // Start next read into buf2 while writing buf1
                var nextRead = srcSsl.ReadAsync(buf2, timeoutCts.Token);
                await dstSsl.WriteAsync(buf1.AsMemory(0, rd), timeoutCts.Token);
                totalRelayed += rd;
                TotalBytes = totalRelayed;
                BytesTransferred?.Invoke(totalRelayed);

                rd = await nextRead;
                // Swap buffers
                (buf1, buf2) = (buf2, buf1);
            }

            await dstSsl.FlushAsync(CancellationToken.None);

            // Close data connections
            srcSsl.Close();
            dstSsl.Close();
            srcTcp.Close();
            dstTcp.Close();

            // Get completion replies
            var srcComplete = await src.GetReply(ct);
            var dstComplete = await dst.GetReply(ct);
            Log.Debug("Relay complete: src={SrcCode}, dst={DstCode}", srcComplete.Code, dstComplete.Code);

            TotalBytes = totalRelayed;
            SetState(TransferState.Complete);
            return true;
        }
        finally
        {
            srcSsl?.Dispose();
            dstSsl?.Dispose();
            srcTcp.Dispose();
            dstTcp?.Dispose();
        }
    }

    private async Task<bool> WaitForTransferComplete(
        AsyncFtpClient src, AsyncFtpClient dst,
        int timeoutSec, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec > 0 ? timeoutSec : 60));

        try
        {
            // Source sends 226 when RETR is done
            var srcReply = await src.GetReply(timeoutCts.Token);
            if (srcReply.Code != "226" && srcReply.Code != "250")
                Log.Warning("FXP source unexpected reply: {Code} {Msg}", srcReply.Code, srcReply.Message);

            // Dest sends 226 when STOR is done
            var dstReply = await dst.GetReply(timeoutCts.Token);
            if (dstReply.Code != "226" && dstReply.Code != "250")
                Log.Warning("FXP dest unexpected reply: {Code} {Msg}", dstReply.Code, dstReply.Message);

            SetState(TransferState.Complete);
            return true;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            SetState(TransferState.Error);
            ErrorMessage = "Transfer timed out";
            return false;
        }
    }

    private static string ExtractPasvAddress(string message)
    {
        var match = PasvRegex.Match(message);
        if (!match.Success)
            throw new IOException($"Failed to parse PASV response: {message}");
        return $"{match.Groups[1].Value},{match.Groups[2].Value},{match.Groups[3].Value},{match.Groups[4].Value},{match.Groups[5].Value},{match.Groups[6].Value}";
    }

    private void SetState(TransferState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}
