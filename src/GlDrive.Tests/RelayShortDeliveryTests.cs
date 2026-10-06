using System.IO;
using FluentFTP;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// 2026-10-05 19:29:48: a Relay of senna.s01e04.hdr...r15 read EOF from the source data
/// channel after 81,821,696 of 400,000,000 bytes, the completion reply then timed out, and
/// the "bytes were relayed, the reply is only confirmation" forgiveness logged
/// <c>FXP complete</c>. Ownership, the delivered count, the speed tracker (400 MB credited)
/// and telemetry all recorded a truncated file as delivered; only a later rescan re-raced it.
/// A source EOF is proof of delivery only when the byte count reaches the listed size.
/// </summary>
public sealed class RelayShortDeliveryTests
{
    [Theory]
    [InlineData(400_000_000, 400_000_000)]
    [InlineData(400_000_100, 400_000_000)] // file grew on a still-racing source
    [InlineData(1, 0)]                     // size unknown — keep the existing forgiveness
    public void Full_or_unknown_size_delivery_counts_as_delivered(long relayed, long expected)
    {
        Assert.True(FxpTransfer.RelayDeliveredInFull(relayed, expected));
    }

    [Theory]
    [InlineData(81_821_696, 400_000_000)]  // the production event
    [InlineData(399_999_999, 400_000_000)]
    [InlineData(0, 400_000_000)]
    [InlineData(0, 0)]
    public void Short_or_empty_delivery_is_not_delivered(long relayed, long expected)
    {
        Assert.False(FxpTransfer.RelayDeliveredInFull(relayed, expected));
    }

    [Theory]
    [InlineData(81_821_696, "226")]
    [InlineData(399_999_999, "250")]
    [InlineData(0, "226")]
    public async Task Successful_replies_cannot_mark_a_short_file_complete(long relayed, string code)
    {
        var transfer = new FxpTransfer();
        var repliesRead = false;

        var error = await Assert.ThrowsAsync<IOException>(() => transfer.CompleteRelayAsync(
            relayed, 400_000_000, () =>
            {
                CpsvDataHelper.ValidateCompletion(new FtpReply { Code = code });
                repliesRead = true;
                return Task.CompletedTask;
            }, CancellationToken.None));

        Assert.True(repliesRead); // drain the replies before rejecting the byte count
        Assert.Contains($"{relayed} of 400000000 bytes", error.Message);
        Assert.Equal(relayed, transfer.TotalBytes);
        Assert.NotEqual(TransferState.Complete, transfer.State);
        Assert.Equal(FxpFaultSide.None, transfer.FaultSide); // retain conservative pool quarantine
    }

    [Theory]
    [InlineData(400_000_000, 400_000_000)]
    [InlineData(400_000_100, 400_000_000)]
    [InlineData(1, 0)]
    [InlineData(0, 0)] // valid zero-byte file with successful completion replies
    public async Task Successful_replies_accept_full_grown_and_unknown_sizes(long relayed, long expected)
    {
        var transfer = new FxpTransfer();

        Assert.True(await transfer.CompleteRelayAsync(relayed, expected,
            () => Task.CompletedTask, CancellationToken.None));

        Assert.Equal(relayed, transfer.TotalBytes);
        Assert.Equal(TransferState.Complete, transfer.State);
    }

    [Theory]
    [InlineData(400_000_000, 400_000_000)]
    [InlineData(400_000_100, 400_000_000)]
    [InlineData(1, 0)]
    public async Task Lost_completion_reply_keeps_existing_full_delivery_forgiveness(long relayed, long expected)
    {
        var transfer = new FxpTransfer();

        Assert.True(await transfer.CompleteRelayAsync(relayed, expected,
            () => Task.FromException(new IOException("Control channel closed")), CancellationToken.None));

        Assert.Equal(relayed, transfer.TotalBytes);
        Assert.Equal(TransferState.Complete, transfer.State);
    }

    [Fact]
    public async Task Short_delivery_with_a_lost_reply_retains_failure_cause_and_byte_counts()
    {
        var transfer = new FxpTransfer();
        var replyFailure = new TimeoutException("The operation has timed out.");

        var error = await Assert.ThrowsAsync<IOException>(() => transfer.CompleteRelayAsync(
            81_821_696, 400_000_000, () => Task.FromException(replyFailure), CancellationToken.None));

        Assert.Same(replyFailure, error.InnerException);
        Assert.Contains("81821696 of 400000000 bytes", error.Message);
        Assert.Equal(81_821_696, transfer.TotalBytes);
        Assert.NotEqual(TransferState.Complete, transfer.State);
    }

    [Fact]
    public async Task Caller_cancellation_during_completion_is_never_counted_as_delivery()
    {
        var transfer = new FxpTransfer();
        using var cancellation = new CancellationTokenSource();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer.CompleteRelayAsync(
            400_000_000, 400_000_000, () =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            }, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.NotEqual(TransferState.Complete, transfer.State);
    }

    [Fact]
    public async Task Caller_cancellation_wins_even_when_reply_reader_returns_success()
    {
        var transfer = new FxpTransfer();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer.CompleteRelayAsync(
            400_000_000, 400_000_000, () =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }, cancellation.Token));

        Assert.NotEqual(TransferState.Complete, transfer.State);
    }

    [Fact]
    public async Task Caller_cancellation_surfaced_as_io_failure_is_not_forgiven()
    {
        var transfer = new FxpTransfer();
        using var cancellation = new CancellationTokenSource();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer.CompleteRelayAsync(
            400_000_000, 400_000_000, () =>
            {
                cancellation.Cancel();
                return Task.FromException(new IOException("Cancelled control read"));
            }, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.NotEqual(TransferState.Complete, transfer.State);
    }

    [Fact]
    public void Relay_routes_its_listed_size_and_completion_through_the_checked_policy()
    {
        var src = CpsvDataCommandRejectionTests.ReadSource("Spread", "FxpTransfer.cs");
        var start = src.IndexOf("private async Task<bool> ExecuteRelay(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = src.IndexOf("internal void AcceptRelayRetrReply(", start, StringComparison.Ordinal);
        var body = src[start..end];

        Assert.Contains("return await CompleteRelayAsync(totalRelayed, expectedBytes,", body);
        Assert.DoesNotContain("when (totalRelayed > 0)", body);
        Assert.DoesNotContain("ExecuteRelay(source.Client, dest.Client, srcPath, dstPath, transferTimeoutSeconds, ct, pasvSw)", src);
    }
}
