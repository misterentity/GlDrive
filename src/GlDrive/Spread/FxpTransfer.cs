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
        CancellationToken ct)
    {
        try
        {
            // When both support CPSV, try CpsvPasv first (source CPSV, dest regular PASV).
            // If that fails (BNC backend can't reach dest), fall back to Relay (pipe through local).
            if (mode == FxpMode.Relay)
            {
                try
                {
                    return await ExecuteCpsvPasv(source.Client, dest.Client, srcPath, dstPath, transferTimeoutSeconds, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Debug("CpsvPasv failed ({Error}), trying Relay mode", ex.Message);
                }
                return await ExecuteRelay(source.Client, dest.Client, srcPath, dstPath, transferTimeoutSeconds, ct);
            }

            return mode switch
            {
                FxpMode.PasvPasv => await ExecutePasvPasv(source.Client, dest.Client, srcPath, dstPath, transferTimeoutSeconds, ct),
                FxpMode.CpsvPasv => await ExecuteCpsvPasv(source.Client, dest.Client, srcPath, dstPath, transferTimeoutSeconds, ct),
                FxpMode.PasvCpsv => await ExecutePasvCpsv(source.Client, dest.Client, srcPath, dstPath, transferTimeoutSeconds, ct),
                FxpMode.Relay => await ExecuteRelay(source.Client, dest.Client, srcPath, dstPath, transferTimeoutSeconds, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetState(TransferState.Error);
            ErrorMessage = ex.Message;
            Log.Warning(ex, "FXP transfer failed: {Src} -> {Dst}", srcPath, dstPath);
            return false;
        }
    }

    private async Task<bool> ExecutePasvPasv(
        AsyncFtpClient src, AsyncFtpClient dst,
        string srcPath, string dstPath,
        int timeoutSec, CancellationToken ct)
    {
        SetState(TransferState.NegotiatingPassive);

        await src.Execute("TYPE I", ct);
        await dst.Execute("TYPE I", ct);

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

        SetState(TransferState.Transferring);

        // Wait for completion with timeout
        return await WaitForTransferComplete(src, dst, timeoutSec, ct);
    }

    private async Task<bool> ExecuteCpsvPasv(
        AsyncFtpClient src, AsyncFtpClient dst,
        string srcPath, string dstPath,
        int timeoutSec, CancellationToken ct)
    {
        // Source is CPSV (behind BNC, can't PASV). Dest does PASV, source PORTs to it.
        SetState(TransferState.NegotiatingPassive);

        await src.Execute("TYPE I", ct);
        await dst.Execute("TYPE I", ct);

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

        SetState(TransferState.Transferring);
        return await WaitForTransferComplete(src, dst, timeoutSec, ct);
    }

    private async Task<bool> ExecutePasvCpsv(
        AsyncFtpClient src, AsyncFtpClient dst,
        string srcPath, string dstPath,
        int timeoutSec, CancellationToken ct)
    {
        // Dest is CPSV. Source does PASV, dest PORTs to source.
        SetState(TransferState.NegotiatingPassive);

        await src.Execute("TYPE I", ct);
        await dst.Execute("TYPE I", ct);

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

        SetState(TransferState.Transferring);
        return await WaitForTransferComplete(src, dst, timeoutSec, ct);
    }

    private async Task<bool> ExecuteRelay(
        AsyncFtpClient src, AsyncFtpClient dst,
        string srcPath, string dstPath,
        int timeoutSec, CancellationToken ct)
    {
        // Both CPSV — neither backend IP is routable. Relay through local memory.
        SetState(TransferState.NegotiatingPassive);

        await src.Execute("TYPE I", ct);
        await dst.Execute("TYPE I", ct);

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
