using System.IO;
using FluentFTP;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class DirectFxpCompletionTests
{
    private static Task<FtpReply?> Reply(string code) =>
        Task.FromResult<FtpReply?>(new FtpReply { Code = code, Message = "Fixture completion" });

    [Theory]
    [InlineData("226", "226")]
    [InlineData("226", "250")]
    [InlineData("250", "226")]
    [InlineData("250", "250")]
    public async Task Both_positive_completions_finish_the_transfer(string source, string destination)
    {
        var transfer = new FxpTransfer();

        Assert.True(await transfer.CompleteDirectAsync(30,
            (_, _) => Reply(source), (_, _) => Reply(destination), CancellationToken.None));

        Assert.Equal(TransferState.Complete, transfer.State);
    }

    [Theory]
    [InlineData("426", "226", "source")]
    [InlineData("451", "226", "source")]
    [InlineData("550", "226", "source")]
    [InlineData("421", "226", "source")]
    [InlineData("150", "226", "source")]
    [InlineData("", "226", "source")]
    [InlineData("226", "426", "destination")]
    [InlineData("226", "451", "destination")]
    [InlineData("226", "550", "destination")]
    [InlineData("226", "421", "destination")]
    [InlineData("226", "125", "destination")]
    [InlineData("226", "", "destination")]
    [InlineData("426", "451", "source")]
    public async Task Rejected_completions_drain_both_replies_and_never_credit_delivery(
        string source, string destination, string failedSide)
    {
        var transfer = new FxpTransfer();
        var reads = new List<string>();

        var error = await Assert.ThrowsAsync<DirectFxpCompletionException>(() => transfer.CompleteDirectAsync(30,
            (_, _) => { reads.Add("source"); return Reply(source); },
            (_, _) => { reads.Add("destination"); return Reply(destination); }, CancellationToken.None));

        Assert.Equal(new[] { "source", "destination" }, reads);
        Assert.Contains(failedSide, error.Message);
        Assert.NotEqual(TransferState.Complete, transfer.State);
        // Data already flowed: retain the existing conservative quarantine of both sessions.
        Assert.Equal(FxpFaultSide.None, transfer.FaultSide);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_completion_preserves_the_transfer_timeout(bool sourceTimesOut)
    {
        var transfer = new FxpTransfer();
        var destinationReads = 0;

        Assert.False(await transfer.CompleteDirectAsync(30,
            (_, _) => sourceTimesOut ? Task.FromResult<FtpReply?>(null) : Reply("226"),
            (_, _) => { destinationReads++; return Task.FromResult<FtpReply?>(null); }, CancellationToken.None));

        Assert.Equal(sourceTimesOut ? 0 : 1, destinationReads);
        Assert.Equal(TransferState.Error, transfer.State);
        Assert.Equal("Transfer timed out", transfer.ErrorMessage);
        Assert.Equal(FxpFaultSide.None, transfer.FaultSide);
    }

    [Fact]
    public async Task Destination_uses_the_remaining_shared_deadline()
    {
        var transfer = new FxpTransfer();
        TimeSpan sourceBudget = default, destinationBudget = default;
        using var cancellation = new CancellationTokenSource();

        Assert.True(await transfer.CompleteDirectAsync(30,
            async (timeout, token) =>
            {
                sourceBudget = timeout;
                Assert.Equal(cancellation.Token, token);
                await Task.Delay(20);
                return new FtpReply { Code = "226" };
            },
            (timeout, token) =>
            {
                destinationBudget = timeout;
                Assert.Equal(cancellation.Token, token);
                return Reply("226");
            }, cancellation.Token));

        Assert.Equal(TimeSpan.FromSeconds(30), sourceBudget);
        Assert.InRange(destinationBudget, TimeSpan.Zero, sourceBudget - TimeSpan.FromMilliseconds(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Unconfigured_timeout_keeps_the_sixty_second_default(int timeout)
    {
        var transfer = new FxpTransfer();
        TimeSpan sourceBudget = default;

        Assert.True(await transfer.CompleteDirectAsync(timeout,
            (budget, _) => { sourceBudget = budget; return Reply("226"); },
            (_, _) => Reply("226"), CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(60), sourceBudget);
    }

    [Fact]
    public async Task Cancellation_wins_when_the_last_reply_arrives_successfully()
    {
        var transfer = new FxpTransfer();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer.CompleteDirectAsync(30,
            (_, _) => Reply("226"),
            (_, _) => { cancellation.Cancel(); return Reply("226"); }, cancellation.Token));

        Assert.NotEqual(TransferState.Complete, transfer.State);
        Assert.Equal(FxpFaultSide.None, transfer.FaultSide);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_before_or_during_the_source_reply_does_not_start_destination_read(bool cancelBefore)
    {
        var transfer = new FxpTransfer();
        using var cancellation = new CancellationTokenSource();
        var sourceReads = 0;
        var destinationReads = 0;
        if (cancelBefore) cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer.CompleteDirectAsync(30,
            (_, _) => { sourceReads++; cancellation.Cancel(); return Reply("226"); },
            (_, _) => { destinationReads++; return Reply("226"); }, cancellation.Token));

        Assert.Equal(cancelBefore ? 0 : 1, sourceReads);
        Assert.Equal(0, destinationReads);
        Assert.NotEqual(TransferState.Complete, transfer.State);
        Assert.Equal(FxpFaultSide.None, transfer.FaultSide);
    }

    [Fact]
    public async Task A_faulting_reply_read_is_not_converted_to_success()
    {
        var transfer = new FxpTransfer();
        var failure = new IOException("Fixture control channel reset");
        var destinationReads = 0;

        var actual = await Assert.ThrowsAsync<IOException>(() => transfer.CompleteDirectAsync(30,
            (_, _) => Task.FromException<FtpReply?>(failure),
            (_, _) => { destinationReads++; return Reply("226"); }, CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.Equal(0, destinationReads);
        Assert.NotEqual(TransferState.Complete, transfer.State);
        Assert.Equal(FxpFaultSide.None, transfer.FaultSide);
    }
}
