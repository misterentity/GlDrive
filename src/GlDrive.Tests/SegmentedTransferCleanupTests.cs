using System.IO;
using FluentFTP;
using GlDrive.Downloads.Segmented;
using Xunit;

namespace GlDrive.Tests;

public sealed class SegmentedTransferCleanupTests
{
    [Fact]
    public async Task Failed_post_drain_sidecar_reset_still_closes_every_staged_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-drain-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var growing = new TransferFile("/growing.bin", Path.Combine(root, "growing.bin"), 8, "8:old");
        var untouched = new TransferFile("/untouched.bin", Path.Combine(root, "untouched.bin"), 8, "8:stable");
        var sidecar = growing.LocalPath + StagedFile.SidecarSuffix;
        var lane = new OversizedLane(growing, () =>
        {
            // The first invalidation already persisted successfully. Simulate the
            // sidecar becoming unwritable while readers finish draining, without
            // replacing the staging handles or changing production filesystem state.
            File.Delete(sidecar);
            Directory.CreateDirectory(sidecar);
        });
        try
        {
            var error = await Record.ExceptionAsync(() => new SegmentedTransfer([growing, untouched],
                new SegmentedTransferOptions { MaxLaneFailures = 1 })
                .RunAsync([lane], null, null, CancellationToken.None));

            Assert.NotNull(error);
            Assert.True(error is IOException or UnauthorizedAccessException, error.ToString());
            Assert.IsNotType<RemoteFileSizeExceededException>(error);
            Assert.Equal(1, lane.DisposeCount);
            Assert.Equal(1, lane.FaultCount);
            Assert.True(Directory.Exists(sidecar));
            foreach (var file in new[] { growing, untouched })
            {
                Assert.False(File.Exists(file.LocalPath));
                // An exclusive reopen proves cleanup closed the retained stage even
                // when persisting the first file's invalidated ranges threw.
                using var exclusive = new FileStream(file.LocalPath + StagedFile.StagingSuffix,
                    FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.Equal(file.Size, exclusive.Length);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class OversizedLane(TransferFile expected, Action afterDrain) : ISegmentLane
    {
        public string Label => "cleanup fixture";
        public string? NetworkId => null;
        public bool CanStopEarly => false;
        public bool ShouldYield => false;
        public int FaultCount { get; private set; }
        public int DisposeCount { get; private set; }

        public Task<ISegmentReader> OpenAsync(TransferFile file, long offset, long end, CancellationToken ct)
        {
            Assert.Equal(expected, file);
            return Task.FromResult<ISegmentReader>(new FtpLane.StandardReader(new MemoryStream(new byte[9]),
                _ => Task.FromResult(new FtpReply { Code = "226" }), file));
        }

        public void Fault(Exception ex)
        {
            Assert.IsType<RemoteFileSizeExceededException>(ex);
            FaultCount++;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            afterDrain();
            return ValueTask.CompletedTask;
        }
    }
}
