using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Progress re-arms the 3x ceiling, so a slow relay can now reach its separate 20x
/// duration limit. That limit must never be reported as 3x seconds of no progress.
/// </summary>
public sealed class RelayDeadlineAttributionTests
{
    [Theory]
    [InlineData(60, 1200)]
    [InlineData(30, 600)]
    [InlineData(0, 1200)]
    [InlineData(-1, 1200)]
    public void Relay_duration_policy_retains_the_existing_limits(int timeout, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), FxpFailurePolicy.RelayTransferDeadline(timeout));

    [Fact]
    public async Task Own_deadline_is_tagged_without_cancelling_the_outer_token()
    {
        using var outer = new CancellationTokenSource();
        var deadline = TimeSpan.FromMilliseconds(30);
        var error = await Assert.ThrowsAsync<RelayDurationExceededException>(() =>
            FxpTransfer.WithRelayDeadline(token => Task.Delay(Timeout.Infinite, token), deadline, outer.Token));

        Assert.False(outer.IsCancellationRequested);
        Assert.Equal(deadline, error.Deadline);
        var original = Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Equal(original.CancellationToken, error.CancellationToken);
        Assert.True(error.CancellationToken.IsCancellationRequested);
        Assert.Equal(FxpCancellation.TransferDeadline,
            FxpFailurePolicy.ClassifyCancellation(false, outer.IsCancellationRequested, true));
    }

    [Fact]
    public async Task Parent_cancellation_is_preserved_without_a_duration_tag()
    {
        using var outer = new CancellationTokenSource();
        OperationCanceledException? original = null;
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FxpTransfer.WithRelayDeadline(token =>
            {
                outer.Cancel();
                original = new OperationCanceledException(token);
                return Task.FromException(original);
            }, TimeSpan.FromMinutes(1), outer.Token));

        Assert.Same(original, error);
        Assert.IsNotType<RelayDurationExceededException>(error);
        Assert.True(outer.IsCancellationRequested);
    }

    [Fact]
    public async Task Parent_cancellation_wins_when_both_tokens_are_cancelled()
    {
        using var outer = new CancellationTokenSource();
        OperationCanceledException? original = null;
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FxpTransfer.WithRelayDeadline(async token =>
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException ex)
                {
                    original = ex;
                    outer.Cancel();
                    throw;
                }
            }, TimeSpan.FromMilliseconds(30), outer.Token));

        Assert.Same(original, error);
        Assert.IsNotType<RelayDurationExceededException>(error);
    }

    [Fact]
    public async Task Unrelated_transport_cancellation_is_not_relabelled_as_a_deadline()
    {
        var original = new OperationCanceledException("Transport cancelled its own operation");
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            FxpTransfer.WithRelayDeadline(_ => Task.FromException(original),
                TimeSpan.FromMinutes(1), CancellationToken.None));

        Assert.Same(original, error);
        Assert.Equal("unattributed cancellation",
            FxpFailurePolicy.DescribeTransferDeadline(error, false, FxpMode.Relay, 500_000_000, 60));
    }

    [Fact]
    public async Task Successful_work_retains_its_byte_count_and_completes_normally()
    {
        long moved = 0;
        await FxpTransfer.WithRelayDeadline(token =>
        {
            Assert.False(token.IsCancellationRequested);
            moved += 256 * 1024;
            return Task.CompletedTask;
        }, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(256 * 1024, moved);
    }

    [Theory]
    [InlineData(FxpMode.Relay, 500_000_000)]
    [InlineData(FxpMode.CpsvPasv, 500_000_000)]
    [InlineData(FxpMode.Relay, 0)]
    public void Tagged_duration_never_claims_an_inactivity_window(FxpMode mode, long moved)
    {
        var error = new RelayDurationExceededException(TimeSpan.FromSeconds(1200),
            new OperationCanceledException());
        Assert.Equal("relay duration ceiling 1200s",
            FxpFailurePolicy.DescribeTransferDeadline(error, false, mode, moved, 60));
        // Preserve the observed cause even if the outer timer fires before the catch logs it.
        Assert.Equal("relay duration ceiling 1200s",
            FxpFailurePolicy.DescribeTransferDeadline(error, true, mode, moved, 60));
    }

    [Theory]
    [InlineData(FxpMode.Relay, 0, "no progress for 180s")]
    [InlineData(FxpMode.Relay, 500_000_000, "no progress for 180s")]
    [InlineData(FxpMode.CpsvPasv, 500_000_000, "no progress for 180s")]
    [InlineData(FxpMode.CpsvPasv, 0, "ceiling 180s")]
    [InlineData(FxpMode.PasvPasv, 0, "ceiling 180s")]
    public void Outer_deadline_keeps_its_existing_mode_and_progress_policy(
        FxpMode mode, long moved, string expected)
        => Assert.Equal(expected, FxpFailurePolicy.DescribeTransferDeadline(
            new OperationCanceledException(), true, mode, moved, 60));

    [Fact]
    public void Relay_loop_uses_the_tagged_deadline_and_job_logs_its_provenance()
    {
        var transfer = CpsvDataCommandRejectionTests.ReadSource("Spread", "FxpTransfer.cs");
        var start = transfer.IndexOf("private async Task<bool> ExecuteRelay(", StringComparison.Ordinal);
        var end = transfer.IndexOf("internal static async Task WithRelayDeadline(", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var relay = transfer[start..end];
        Assert.Contains("await WithRelayDeadline(async relayToken =>", relay);
        Assert.Contains("FxpFailurePolicy.RelayTransferDeadline(timeoutSec), ct)", relay);
        Assert.Contains("srcSsl.ReadAsync(buf1, relayToken)", relay);
        Assert.Contains("srcSsl.ReadAsync(buf2, relayToken)", relay);
        Assert.Contains("dstSsl.WriteAsync(buf1.AsMemory(0, rd), relayToken)", relay);
        Assert.Contains("TotalBytes = totalRelayed;", relay);
        Assert.Contains("BytesTransferred?.Invoke(totalRelayed);", relay);

        var job = CpsvDataCommandRejectionTests.ReadSource("Spread", "SpreadJob.cs");
        Assert.Contains("FxpFailurePolicy.DescribeTransferDeadline(ex, ct.IsCancellationRequested,", job);
        Assert.Contains("ex is RelayDurationExceededException || ct.IsCancellationRequested", job);
    }
}
