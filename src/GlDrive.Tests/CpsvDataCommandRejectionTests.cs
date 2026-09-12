using System.IO;
using System.Text.RegularExpressions;
using FluentFTP;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// A CPSV data sequence marks the control channel as "owing a reply" before the
/// data command is sent (CpsvControlChannelDesyncTests). But when the server
/// answers the data command itself with a FINAL negative reply — `LIST 550 No such
/// file`, `RETR 550`, `STOR 425 Can't build data connection` — no transfer began
/// and no 226 will follow: the channel is in sync, exactly like the dupe-skip and
/// STOR-rejection branches the Relay path already reuses hundreds of times a day.
///
/// Production 2026-09-07..09: every hourly search-index build discarded two
/// connections (39 of 102 daily poisoned-discards on 09-09), and on 09-08 03:02 three
/// `RETR 550` replies from a source that had just moved the release cost six logins
/// against a 4-login cap, cascading into ghost-kills, a 90 s BNC cooldown and four
/// "Pool exhausted" transfer errors. The listing failures never reached the log —
/// they were caught at Debug against an Information sink.
/// </summary>
public sealed class CpsvDataCommandRejectionTests
{
    private static AsyncFtpClient NewClient() => new("example.invalid", "u", "p");

    [Theory]
    [InlineData("425")]
    [InlineData("450")]
    [InlineData("550")]
    [InlineData("553")]
    public void Final_negative_reply_clears_the_mark_and_reports_in_sync(string code)
    {
        using var c = NewClient();
        CpsvDataHelper.BeginDataSequence(c);

        var ex = CpsvDataHelper.RejectDataCommand(c, "LIST", new FtpReply { Code = code, Message = "Nope" });

        Assert.IsAssignableFrom<IOException>(ex);
        Assert.Equal($"LIST failed: {code} Nope", ex.Message);
        Assert.True(ex.ControlChannelInSync);
        Assert.Equal(code, ex.Code);
        Assert.False(CpsvDataHelper.HasPendingDataSequence(c));
        Assert.True(FtpCommandRejection.IsClean(ex));
    }

    [Fact]
    public void Service_closing_reply_keeps_the_mark()
    {
        using var c = NewClient();
        CpsvDataHelper.BeginDataSequence(c);

        var ex = CpsvDataHelper.RejectDataCommand(c, "RETR", new FtpReply { Code = "421", Message = "Timeout" });

        Assert.False(ex.ControlChannelInSync);
        Assert.True(CpsvDataHelper.HasPendingDataSequence(c));
        Assert.False(FtpCommandRejection.IsClean(ex));
    }

    [Theory]
    [InlineData("200")]
    [InlineData("226")]
    [InlineData("227")]
    [InlineData("")]
    public void Unexpected_non_negative_reply_is_the_desync_signature_and_keeps_the_mark(string code)
    {
        using var c = NewClient();
        CpsvDataHelper.BeginDataSequence(c);

        var ex = CpsvDataHelper.RejectDataCommand(c, "STOR", new FtpReply { Code = code, Message = "stale" });

        Assert.False(ex.ControlChannelInSync);
        Assert.True(CpsvDataHelper.HasPendingDataSequence(c));
        Assert.False(FtpCommandRejection.IsClean(ex));
    }

    [Fact]
    public void Transport_and_cancellation_failures_are_never_clean()
    {
        Assert.False(FtpCommandRejection.IsClean(new IOException("RETR failed: 550 No such file")));
        Assert.False(FtpCommandRejection.IsClean(new OperationCanceledException()));
        Assert.False(FtpCommandRejection.IsClean(new TimeoutException()));
    }

    [Theory]
    [InlineData("LIST")]
    [InlineData("RETR")]
    [InlineData("STOR")]
    public void Every_cpsv_data_command_rejection_goes_through_the_shared_helper(string verb)
    {
        var source = ReadSource("Ftp", "CpsvDataHelper.cs");
        // No throw site may build its own "<VERB> failed:" IOException any more — that is
        // the shape that left the mark set. All of them must route through RejectDataCommand.
        Assert.DoesNotContain("throw new IOException($\"" + verb + " failed:", source);
        Assert.Contains("RejectDataCommand(client, \"" + verb + "\"", source);
    }

    [Fact]
    public void Callers_that_poison_on_failure_exempt_clean_rejections()
    {
        // FtpOperations and the SpreadJob scan listing each wrap a CPSV call in a catch
        // that poisons the borrowed connection. After a clean rejection the connection is
        // healthy; poisoning it is the needless login loss this release removes.
        var ops = ReadSource("Ftp", "FtpOperations.cs");
        var poisonCatches = Regex.Matches(ops,
            @"catch\s*(\([^)]*\))?\s*(when\s*\((?:[^()]|\([^()]*\))*\))?\s*\{\s*(//[^\n]*\n\s*)*conn\.Poison\(");
        Assert.True(poisonCatches.Count >= 4, $"expected the FtpOperations poison catches, found {poisonCatches.Count}");
        foreach (Match m in poisonCatches)
            Assert.Contains("when (!FtpCommandRejection.IsClean(ex))", m.Value);

        var job = ReadSource("Spread", "SpreadJob.cs");
        var scan = job.IndexOf("items = await CpsvDataHelper.ListDirectory(conn.Client, currentPath", StringComparison.Ordinal);
        Assert.True(scan > 0);
        // Anchor on the borrow scope, not a character distance: v3.10.116 added an
        // in-LIST cancellation branch between the LIST and this catch.
        var scopeEnd = job.IndexOf("List<string>? subdirs", scan, StringComparison.Ordinal);
        var ioCatch = job.IndexOf("catch (IOException", scan, StringComparison.Ordinal);
        Assert.True(ioCatch > scan && scopeEnd > ioCatch, "the scan's IOException catch left the LIST borrow scope");
        var line = job.Substring(ioCatch, job.IndexOf('\n', ioCatch) - ioCatch);
        Assert.Contains("when (!FtpCommandRejection.IsClean(ex))", line);
    }

    internal static string ReadSource(string folder, string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "GlDrive", folder, file)))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir.FullName, "src", "GlDrive", folder, file));
    }
}
