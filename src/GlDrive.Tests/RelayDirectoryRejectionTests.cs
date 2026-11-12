using System.IO;
using FluentFTP;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class RelayDirectoryRejectionTests
{
    [Theory]
    [InlineData("450")]
    [InlineData("451")]
    [InlineData("550")]
    [InlineData("553")]
    public async Task Completed_directory_rejection_before_retr_preserves_both_sessions(string code)
    {
        using var src = new AsyncFtpClient("example.invalid", "u", "p");
        using var dst = new AsyncFtpClient("example.invalid", "u", "p");
        CpsvDataHelper.BeginDataSequence(src);
        CpsvDataHelper.BeginDataSequence(dst);
        var rejection = new FtpDirectoryRejectedException("/fixture", code, "Not allowed to make directories here");
        var transfer = new FxpTransfer { BeforeStore = _ => Task.FromException(rejection) };

        Assert.Same(rejection, await Record.ExceptionAsync(() => transfer.PrepareRelayStoreAsync(src, dst, CancellationToken.None)));

        Assert.False(CpsvDataHelper.HasPendingDataSequence(src));
        Assert.False(CpsvDataHelper.HasPendingDataSequence(dst));
        Assert.Equal(FxpFaultSide.Neither, transfer.FaultSide);
        Assert.Equal(code, rejection.Code);
        Assert.Equal("Not allowed to make directories here", rejection.ReplyMessage);
        Assert.Equal($"MKD failed for /fixture: {code} Not allowed to make directories here", rejection.Message);
        Assert.False(transfer.WasDupe);
    }

    [Theory]
    [InlineData("421")]
    [InlineData("150")]
    [InlineData("200")]
    [InlineData("350")]
    [InlineData("ERR")]
    [InlineData("5xx")]
    [InlineData("5000")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Closing_or_unexpected_directory_reply_preserves_both_pending_markers(string? code)
    {
        await AssertPending(new FtpDirectoryRejectedException("/fixture", code, "Rejected"));
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("timeout")]
    [InlineData("cancelled")]
    [InlineData("untyped-clean-looking-message")]
    public async Task Ambiguous_callback_failures_never_clear_markers(string kind)
    {
        Exception failure = kind switch
        {
            "timeout" => new TimeoutException("Directory reply timed out"),
            "cancelled" => new OperationCanceledException(),
            "untyped-clean-looking-message" => new IOException("MKD failed for /fixture: 550 Not allowed to make directories here"),
            _ => new IOException("Control channel disconnected")
        };
        await AssertPending(failure);
    }

    [Fact]
    public async Task Cancellation_concurrent_with_a_clean_rejection_keeps_conservative_markers()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await AssertPending(new FtpDirectoryRejectedException("/fixture", "550", "Rejected"), cancellation.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Successful_or_absent_callback_leaves_sequences_pending_for_the_transfer(bool hasCallback)
    {
        using var src = new AsyncFtpClient("example.invalid", "u", "p");
        using var dst = new AsyncFtpClient("example.invalid", "u", "p");
        CpsvDataHelper.BeginDataSequence(src);
        CpsvDataHelper.BeginDataSequence(dst);
        var transfer = new FxpTransfer { BeforeStore = hasCallback ? _ => Task.CompletedTask : null };
        await transfer.PrepareRelayStoreAsync(src, dst, CancellationToken.None);
        Assert.True(CpsvDataHelper.HasPendingDataSequence(src));
        Assert.True(CpsvDataHelper.HasPendingDataSequence(dst));
        Assert.Equal(FxpFaultSide.None, transfer.FaultSide);
    }

    private static async Task AssertPending(Exception failure, CancellationToken ct = default)
    {
        using var src = new AsyncFtpClient("example.invalid", "u", "p");
        using var dst = new AsyncFtpClient("example.invalid", "u", "p");
        CpsvDataHelper.BeginDataSequence(src);
        CpsvDataHelper.BeginDataSequence(dst);
        var transfer = new FxpTransfer { BeforeStore = _ => Task.FromException(failure) };
        Assert.Same(failure, await Record.ExceptionAsync(() => transfer.PrepareRelayStoreAsync(src, dst, ct)));
        Assert.True(CpsvDataHelper.HasPendingDataSequence(src));
        Assert.True(CpsvDataHelper.HasPendingDataSequence(dst));
        Assert.Equal(FxpFaultSide.None, transfer.FaultSide);
    }
}
