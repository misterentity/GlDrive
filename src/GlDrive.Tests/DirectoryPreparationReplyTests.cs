using FluentFTP;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class DirectoryPreparationReplyTests
{
    public static IEnumerable<object[]> UnsafeReplies =>
        from step in Enumerable.Range(0, 5)
        from code in new[] { "421", "150", "350", "5xx", "" }
        select new object[] { step, code };

    [Theory]
    [MemberData(nameof(UnsafeReplies))]
    public async Task Unexpected_reply_stops_directory_walk_before_any_later_reply_can_mask_it(int failedStep, string code)
    {
        var sent = new List<string>();
        var normal = new[] { "550", "250", "550", "257", "250" };
        var result = await FtpDirectoryMaker.EnsurePath((command, _) =>
        {
            var index = sent.Count;
            sent.Add(command);
            return Task.FromResult(new FtpReply { Code = index == failedStep ? code : normal[Math.Min(index, 4)], Message = "fixture reply" });
        }, "/fixture", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(code, result.Code);
        Assert.Equal("fixture reply", result.Message);
        Assert.Equal(failedStep + 1, sent.Count);
        Assert.False(new FtpDirectoryRejectedException("/fixture", result.Code, result.Message).ControlChannelInSync);
    }

    [Theory]
    [InlineData("421")]
    [InlineData("150")]
    [InlineData("350")]
    [InlineData("5xx")]
    public void Already_exists_text_cannot_override_an_unsafe_reply(string code)
    {
        Assert.False(FtpDirectoryMaker.IsMkdSuccess(new FtpReply { Code = code, Message = "Directory already exists" }));
    }
}
