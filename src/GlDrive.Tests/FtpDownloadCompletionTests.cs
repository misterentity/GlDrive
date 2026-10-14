using System.IO;
using FluentFTP;
using GlDrive.Downloads.Segmented;
using Xunit;

namespace GlDrive.Tests;

public class FtpDownloadCompletionTests
{
    [Theory]
    [InlineData("426")]
    [InlineData("451")]
    [InlineData("150")]
    [InlineData("200")]
    [InlineData("350")]
    public async Task Standard_lane_rejects_any_reply_that_does_not_confirm_the_transfer(string code)
    {
        await using var reader = new FtpLane.StandardReader(new MemoryStream(),
            _ => Task.FromResult(new FtpReply { Code = code, Message = "fixture reply" }));
        var error = await Assert.ThrowsAsync<IOException>(() => reader.FinishAsync(true, default));
        Assert.Contains(code, error.Message);
    }

    [Theory]
    [InlineData("226")]
    [InlineData("250")]
    public async Task Standard_lane_accepts_confirmed_eof(string code)
    {
        await using var reader = new FtpLane.StandardReader(new MemoryStream(),
            _ => Task.FromResult(new FtpReply { Code = code }));
        await reader.FinishAsync(true, default);
    }

    [Fact]
    public async Task Unexpected_trailing_bytes_prevent_completion_even_with_a_success_reply()
    {
        var replies = 0;
        await using var reader = new FtpLane.StandardReader(new MemoryStream([1]),
            _ => { replies++; return Task.FromResult(new FtpReply { Code = "226" }); });
        await Assert.ThrowsAsync<IOException>(() => reader.FinishAsync(true, default));
        Assert.Equal(0, replies);
    }
}
