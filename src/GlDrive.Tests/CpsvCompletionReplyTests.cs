using System.IO;
using FluentFTP;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.111 made <c>CompleteDataSequence</c> validate the completion reply — and
/// did so BEFORE clearing the pending mark. A reply that has been read is a reply
/// that is no longer on the wire: the control channel is in sync whatever its code
/// says. glftpd answers a fully relayed STOR with <c>426 Data Connection: Success.</c>
/// (its data-close quirk), so the first production run of that code (v3.10.112,
/// 2026-09-09 22:28) poisoned the destination after 12 of 12 successful relays and
/// the source after 10 of 12 dupe-skip ABORs (whose legitimate reply IS a 426) —
/// versus 8 of 1,536 and 1 of 91 on the previous build. Exactly the v3.10.14 shape.
///
/// Rule: consuming the reply clears the mark; validating it is a separate, per-caller
/// decision. Receivers (RETR) validate; senders (STOR, whose bytes were flushed
/// before the reply) and ABOR accept any completion reply.
/// </summary>
public sealed class CpsvCompletionReplyTests
{
    private static AsyncFtpClient NewClient() => new("example.invalid", "u", "p");

    [Theory]
    [InlineData("426")]
    [InlineData("451")]
    [InlineData("550")]
    public void A_consumed_non_success_reply_clears_the_mark_and_still_throws_when_validating(string code)
    {
        using var c = NewClient();
        CpsvDataHelper.BeginDataSequence(c);

        var ex = Assert.Throws<IOException>(() =>
            CpsvDataHelper.AcceptCompletionReply(c, new FtpReply { Code = code, Message = "Data Connection: Success." }, validate: true));

        Assert.Contains(code, ex.Message);
        Assert.False(CpsvDataHelper.HasPendingDataSequence(c), "the reply was read — the channel is in sync and the login must not be spent");
    }

    [Theory]
    [InlineData("426")]
    [InlineData("226")]
    public void A_lenient_completion_clears_the_mark_and_returns_the_reply(string code)
    {
        using var c = NewClient();
        CpsvDataHelper.BeginDataSequence(c);

        var reply = CpsvDataHelper.AcceptCompletionReply(c, new FtpReply { Code = code }, validate: false);

        Assert.Equal(code, reply.Code);
        Assert.False(CpsvDataHelper.HasPendingDataSequence(c));
    }

    [Fact]
    public void A_successful_completion_clears_the_mark()
    {
        using var c = NewClient();
        CpsvDataHelper.BeginDataSequence(c);

        CpsvDataHelper.AcceptCompletionReply(c, new FtpReply { Code = "226" }, validate: true);

        Assert.False(CpsvDataHelper.HasPendingDataSequence(c));
    }

    [Fact]
    public void CompleteDataSequence_routes_through_AcceptCompletionReply_and_never_validates_first()
    {
        var src = CpsvDataCommandRejectionTests.ReadSource("Ftp", "CpsvDataHelper.cs");
        var start = src.IndexOf("internal static async Task<FtpReply> CompleteDataSequence(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = src.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = src[start..end];
        Assert.Contains("AcceptCompletionReply(client, reply, validate)", body);
        Assert.DoesNotContain("ValidateCompletion(", body);
    }

    [Fact]
    public void Senders_and_abort_accept_any_completion_reply_receivers_validate()
    {
        var helper = CpsvDataCommandRejectionTests.ReadSource("Ftp", "CpsvDataHelper.cs");
        foreach (var sender in new[] { "public static async Task UploadFile(", "public static async Task UploadFileStream(" })
        {
            var s = helper.IndexOf(sender, StringComparison.Ordinal);
            Assert.True(s > 0, sender);
            var call = helper.IndexOf("CompleteDataSequence(client, ct", s, StringComparison.Ordinal);
            var line = helper.Substring(call, helper.IndexOf('\n', call) - call);
            Assert.Contains("validate: false", line);
        }
        foreach (var receiver in new[] { "public static async Task<byte[]> DownloadFile(", "public static async Task DownloadFileToStream(" })
        {
            var s = helper.IndexOf(receiver, StringComparison.Ordinal);
            Assert.True(s > 0, receiver);
            var call = helper.IndexOf("CompleteDataSequence(client, ct", s, StringComparison.Ordinal);
            var line = helper.Substring(call, helper.IndexOf('\n', call) - call);
            Assert.DoesNotContain("validate: false", line);
        }

        var fxp = CpsvDataCommandRejectionTests.ReadSource("Spread", "FxpTransfer.cs");
        var relay = fxp.IndexOf("private async Task<bool> ExecuteRelay(", StringComparison.Ordinal);
        var abor = fxp.IndexOf("await src.Execute(\"ABOR\", ct);", relay, StringComparison.Ordinal);
        var aborComplete = fxp.IndexOf("CompleteDataSequence(src, ct", abor, StringComparison.Ordinal);
        Assert.Contains("validate: false", fxp.Substring(aborComplete, fxp.IndexOf('\n', aborComplete) - aborComplete));
        var dstComplete = fxp.IndexOf("CompleteDataSequence(dst, ct", abor, StringComparison.Ordinal);
        Assert.True(dstComplete > aborComplete);
        Assert.Contains("validate: false", fxp.Substring(dstComplete, fxp.IndexOf('\n', dstComplete) - dstComplete));
        var srcComplete = fxp.IndexOf("var srcComplete = await CpsvDataHelper.CompleteDataSequence(src, ct", relay, StringComparison.Ordinal);
        Assert.DoesNotContain("validate: false", fxp.Substring(srcComplete, fxp.IndexOf('\n', srcComplete) - srcComplete));
    }
}
