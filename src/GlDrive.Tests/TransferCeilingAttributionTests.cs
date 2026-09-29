using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.10.138: on 2026-09-28 a superbnc -> zephyr relay hit the 180s per-transfer ceiling
/// while the race kept running. Because the transfer token is linked to the job token,
/// the catch read it as "FXP job cancelled mid-transfer": no warning, no failure counted,
/// no telemetry row — and the thrown path never removed the transfer from
/// _activeTransfers, so the race's end drain waited out its full 60s on a phantom.
/// </summary>
public sealed class TransferCeilingAttributionTests
{
    [Theory]
    // job, transferToken, protocolStarted -> expected
    [InlineData(true, true, true, nameof(FxpCancellation.JobCancelled))]
    [InlineData(true, true, false, nameof(FxpCancellation.JobCancelled))]
    [InlineData(false, true, true, nameof(FxpCancellation.TransferDeadline))]   // the 17:33:57 event
    [InlineData(false, false, true, nameof(FxpCancellation.TransferDeadline))]  // FxpTransfer's own relay timeout
    [InlineData(false, true, false, nameof(FxpCancellation.SetupDeadline))]
    [InlineData(false, false, false, nameof(FxpCancellation.BorrowTimeout))]
    public void Cancellation_is_attributed_to_the_token_that_fired(
        bool job, bool transferToken, bool started, string expected)
        => Assert.Equal(expected, FxpFailurePolicy.ClassifyCancellation(job, transferToken, started).ToString());

    [Fact]
    public void Transfer_ceiling_is_three_times_the_transfer_timeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(180), FxpFailurePolicy.TransferCeiling(60));
        Assert.Equal(TimeSpan.FromSeconds(180), FxpFailurePolicy.TransferCeiling(0));
        Assert.Equal(TimeSpan.FromSeconds(90), FxpFailurePolicy.TransferCeiling(30));
    }

    private static SpreadJob NewJob(FtpConnectionPool pool) => new("TV", "Fixture.S01E01-GROUP",
        SpreadMode.Race, new SpreadConfig(), new() { ["a"] = pool }, new(),
        new() { ["a"] = new ServerConfig { Name = "fixture" } },
        new SpeedTracker(), new SkiplistEvaluator());

    [Fact]
    public async Task Tracked_transfer_is_released_even_when_the_transfer_throws()
    {
        await using var pool = new FtpConnectionPool(null!, 1);
        using var job = NewJob(pool);

        async Task Transfer()
        {
            using var tracked = job.TrackActiveTransfer("f.r00|a->b", new ActiveTransferInfo { FileName = "f.r00" });
            Assert.Single(job.ActiveTransferList);
            await Task.Yield();
            throw new OperationCanceledException();
        }

        await Assert.ThrowsAsync<OperationCanceledException>(Transfer);
        Assert.Empty(job.ActiveTransferList);
    }

    [Fact]
    public async Task Release_is_idempotent_and_never_removes_a_newer_registration()
    {
        await using var pool = new FtpConnectionPool(null!, 1);
        using var job = NewJob(pool);

        var first = job.TrackActiveTransfer("f.r00|a->b", new ActiveTransferInfo { FileName = "first" });
        var retry = job.TrackActiveTransfer("f.r00|a->b", new ActiveTransferInfo { FileName = "retry" });
        first.Dispose(); // a late release of the superseded entry
        first.Dispose(); // the success path + finally both release

        Assert.Equal("retry", Assert.Single(job.ActiveTransferList).FileName);
        retry.Dispose();
        Assert.Empty(job.ActiveTransferList);
    }

    [Fact]
    public void Kbps_matrix_ignores_aborted_rows()
    {
        var digest = new TransfersDigester().Build(
        [
            new FileTransferEvent { SrcServer = "s", DstServer = "d", Bytes = 1_000_000, ElapsedMs = 1000 },
            // A refused STOR "moves" a whole 100 MB file in 200 ms.
            new FileTransferEvent { SrcServer = "s", DstServer = "d", Bytes = 100_000_000, ElapsedMs = 200,
                AbortReason = "STOR failed: 425 Can't build data connection" },
            new FileTransferEvent { SrcServer = "s", DstServer = "d", Bytes = 50_000_000, ElapsedMs = 180_000,
                AbortReason = "cancelled" },
        ]);

        Assert.Equal(8000, digest.KbpsMatrix["s->d"]);
    }
}
