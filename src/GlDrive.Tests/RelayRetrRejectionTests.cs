using System.IO;
using FluentFTP;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Relay issues RETR first. When the source answers with a FINAL negative reply
/// (`550 No such file` after the source moved the release, `425`, `450`) nothing
/// has started on either side: STOR was never sent, and the source's own data
/// sequence never began — the server closed its data socket with the rejection.
/// v3.10.110 preserved the destination; the source was still quarantined, so a
/// burst of three 550s (2026-09-08 03:02) still cost three of the account's four
/// logins for 20 s and tipped the pool into a 90 s BNC cooldown.
/// Only a `421` (server is closing the control connection) or a reply that is not
/// a negative completion at all (the desync signature) keeps the source quarantined.
/// </summary>
public sealed class RelayRetrRejectionTests
{
    [Theory]
    [InlineData("425")]
    [InlineData("450")]
    [InlineData("550")]
    public void Final_retr_rejection_leaves_both_channels_in_sync_and_entangles_neither(string code)
    {
        using var src = new AsyncFtpClient("example.invalid", "u", "p");
        using var dst = new AsyncFtpClient("example.invalid", "u", "p");
        CpsvDataHelper.BeginDataSequence(src);
        CpsvDataHelper.BeginDataSequence(dst);
        var transfer = new FxpTransfer();

        var error = Assert.Throws<IOException>(() => transfer.AcceptRelayRetrReply(src, dst,
            new FtpReply { Code = code, Message = "Transfer refused" }));

        Assert.Equal($"RETR failed: {code} Transfer refused", error.Message);
        Assert.False(CpsvDataHelper.HasPendingDataSequence(dst));
        Assert.False(CpsvDataHelper.HasPendingDataSequence(src));
        Assert.Equal(FxpFaultSide.Neither, transfer.FaultSide);
        Assert.False(transfer.WasDupe);
    }

    [Theory]
    [InlineData("421")]
    [InlineData("200")]
    public void Closing_or_unexpected_reply_preserves_dest_but_keeps_source_quarantined(string code)
    {
        using var src = new AsyncFtpClient("example.invalid", "u", "p");
        using var dst = new AsyncFtpClient("example.invalid", "u", "p");
        CpsvDataHelper.BeginDataSequence(src);
        CpsvDataHelper.BeginDataSequence(dst);
        var transfer = new FxpTransfer();

        var error = Assert.Throws<IOException>(() => transfer.AcceptRelayRetrReply(src, dst,
            new FtpReply { Code = code, Message = "Transfer refused" }));

        Assert.Equal($"RETR failed: {code} Transfer refused", error.Message);
        Assert.False(CpsvDataHelper.HasPendingDataSequence(dst));
        Assert.True(CpsvDataHelper.HasPendingDataSequence(src));
        Assert.Equal(FxpFaultSide.Source, transfer.FaultSide);
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

        transfer.AcceptRelayRetrReply(src, dst, new FtpReply { Code = code });

        Assert.True(CpsvDataHelper.HasPendingDataSequence(src));
        Assert.True(CpsvDataHelper.HasPendingDataSequence(dst));
        Assert.Equal(FxpFaultSide.None, transfer.FaultSide);
    }

    [Fact]
    public void Relay_checks_retr_reply_before_issuing_stor_and_outside_finally()
    {
        var source = CpsvDataCommandRejectionTests.ReadSource("Spread", "FxpTransfer.cs");
        var relay = source.IndexOf("private async Task<bool> ExecuteRelay(", StringComparison.Ordinal);
        var retr = source.IndexOf("var retrReply = await src.Execute", relay, StringComparison.Ordinal);
        var check = source.IndexOf("AcceptRelayRetrReply(src, dst, retrReply);", retr, StringComparison.Ordinal);
        var stor = source.IndexOf("var storReply = await dst.Execute", retr, StringComparison.Ordinal);
        Assert.True(retr > relay && check > retr && check < stor,
            "Reuse is safe only after a RETR reply was received and before STOR is issued; transport exceptions must retain both marks.");
        var cleanup = source.IndexOf("finally", stor, StringComparison.Ordinal);
        var end = source.IndexOf("internal void AcceptRelayRetrReply", cleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("EndDataSequence", source[cleanup..end]);
    }

    [Fact]
    public void SpreadJob_poisons_nothing_for_a_Neither_attribution()
    {
        var job = CpsvDataCommandRejectionTests.ReadSource("Spread", "SpreadJob.cs");
        var sw = job.IndexOf("switch (transfer.FaultSide)", StringComparison.Ordinal);
        Assert.True(sw > 0);
        var neither = job.IndexOf("case FxpFaultSide.Neither:", sw, StringComparison.Ordinal);
        Assert.True(neither > sw, "ApplyPoisonAttribution must handle Neither explicitly — the default arm poisons both.");
        var armEnd = job.IndexOf("break;", neither, StringComparison.Ordinal);
        Assert.DoesNotContain("Poisoned = true", job[neither..armEnd]);
    }
}
