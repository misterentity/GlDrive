using GlDrive.Downloads;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class DownloadStatusProjectionTests
{
    [Theory]
    [InlineData("Waiting for release completion: 2 files are still missing")]
    [InlineData("Waiting for drive Z:\\ — destination volume is not mounted")]
    [InlineData("Retry 1/3: connection closed")]
    public void Queued_rows_show_the_persisted_wait_or_retry_reason(string reason)
    {
        var item = new DownloadItem { Status = DownloadStatus.Queued, ErrorMessage = reason };
        Assert.Equal(reason, DownloadItemVm.DescribeProgress(item));
        Assert.Equal(DownloadStatus.Queued, item.Status);
    }

    [Theory]
    [InlineData(DownloadStatus.Queued, null, "")]
    [InlineData(DownloadStatus.Queued, "  ", "")]
    [InlineData(DownloadStatus.Failed, "Access denied", "Access denied")]
    [InlineData(DownloadStatus.Downloading, "Waiting for release completion", "25%")]
    [InlineData(DownloadStatus.Extracting, "Waiting for release completion", "Extracting...")]
    [InlineData(DownloadStatus.Completed, "Waiting for release completion", "Done")]
    [InlineData(DownloadStatus.Cancelled, "Waiting for release completion", "")]
    public void Current_state_takes_precedence_over_stale_wait_reasons(DownloadStatus status, string? reason, string expected)
    {
        var item = new DownloadItem { Status = status, ErrorMessage = reason, DownloadedBytes = 25, TotalBytes = 100 };
        Assert.Equal(expected, DownloadItemVm.DescribeProgress(item));
    }

    [Theory]
    [InlineData(DownloadStatus.Queued)]
    [InlineData(DownloadStatus.Cancelled)]
    [InlineData(DownloadStatus.Completed)]
    public void Late_progress_does_not_replace_a_waiting_or_terminal_row(DownloadStatus status)
    {
        var item = new DownloadItem { Status = status, ErrorMessage = "Waiting for release completion" };
        var vm = new DownloadItemVm { Status = status.ToString(), ProgressText = DownloadItemVm.DescribeProgress(item) };
        var original = vm.ProgressText;
        vm.ApplyProgress(item, new DownloadProgress(90, 100, 50, "late.r00"));
        Assert.Equal(original, vm.ProgressText);
        Assert.Empty(vm.StreamsText);
    }

    [Fact]
    public void Resumed_download_replaces_the_wait_reason_with_current_progress()
    {
        var item = new DownloadItem { Status = DownloadStatus.Downloading, ErrorMessage = null };
        var vm = new DownloadItemVm { ProgressText = "Waiting for release completion" };
        vm.ApplyProgress(item, new DownloadProgress(50, 100, 25, "release.r00"));
        Assert.Equal("50% — release.r00", vm.ProgressText);
    }
}
