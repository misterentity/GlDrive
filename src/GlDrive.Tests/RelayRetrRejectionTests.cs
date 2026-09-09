using System.IO;
using FluentFTP;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class RelayRetrRejectionTests
{
    [Theory]
    [InlineData("425")]
    [InlineData("450")]
    [InlineData("550")]
    [InlineData("421")]
    public void Rejected_retr_preserves_unused_destination_and_quarantines_source(string code)
    {
        using var src = new AsyncFtpClient("example.invalid", "u", "p");
        using var dst = new AsyncFtpClient("example.invalid", "u", "p");
        CpsvDataHelper.BeginDataSequence(src);
        CpsvDataHelper.BeginDataSequence(dst);
        var transfer = new FxpTransfer();

        var error = Assert.Throws<IOException>(() => transfer.AcceptRelayRetrReply(dst,
            new FtpReply { Code = code, Message = "Transfer refused" }));

        Assert.Equal($"RETR failed: {code} Transfer refused", error.Message);
        Assert.False(CpsvDataHelper.HasPendingDataSequence(dst));
        Assert.True(CpsvDataHelper.HasPendingDataSequence(src));
        Assert.Equal(FxpFaultSide.Source, transfer.FaultSide);
        Assert.False(transfer.WasDupe);
    }

    [Theory]
    [InlineData("125")]
    [InlineData("150")]
    public void Accepted_retr_keeps_both_sequences_pending_until_transfer_completion(string code)
    {
        using var src = new AsyncFtpClient("example.invalid", "u", "p");
        using var dst = new AsyncFtpClient("example.invalid", "u", "p");
        CpsvDataHelper.BeginDataSequence(src);
        CpsvDataHelper.BeginDataSequence(dst);
        var transfer = new FxpTransfer();

        transfer.AcceptRelayRetrReply(dst, new FtpReply { Code = code });

        Assert.True(CpsvDataHelper.HasPendingDataSequence(src));
        Assert.True(CpsvDataHelper.HasPendingDataSequence(dst));
        Assert.Equal(FxpFaultSide.None, transfer.FaultSide);
    }

    [Fact]
    public void Relay_checks_retr_reply_before_issuing_stor_and_outside_finally()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "GlDrive", "Spread", "FxpTransfer.cs")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(dir.FullName, "src", "GlDrive", "Spread", "FxpTransfer.cs"));
        var relay = source.IndexOf("private async Task<bool> ExecuteRelay(", StringComparison.Ordinal);
        var retr = source.IndexOf("var retrReply = await src.Execute", relay, StringComparison.Ordinal);
        var check = source.IndexOf("AcceptRelayRetrReply(dst, retrReply);", retr, StringComparison.Ordinal);
        var stor = source.IndexOf("var storReply = await dst.Execute", retr, StringComparison.Ordinal);
        Assert.True(retr > relay && check > retr && check < stor,
            "Reuse is safe only after a RETR reply was received and before STOR is issued; transport exceptions must retain both marks.");
        var cleanup = source.IndexOf("finally", stor, StringComparison.Ordinal);
        var end = source.IndexOf("internal void AcceptRelayRetrReply", cleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("EndDataSequence", source[cleanup..end]);
    }
}
