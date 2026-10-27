using System;
using System.Threading;
using System.Threading.Tasks;
using GlDrive.Ftp;
using Xunit;
using Verdict = GlDrive.Ftp.FtpOperations.DiskFreeVerdict;

namespace GlDrive.Tests;

/// <summary>
/// v3.11.32 — the dashboard status bar polls SITE DISKFREE on every connected server every
/// 30s. Neither glftpd here implements it: across 2026-10-24..26 the logs hold 15,110 lines
/// of it, every reply "500 'SITE DISKFREE': Command not understood." (1,278 sends in the
/// 7.2h after the 3.11.31 deploy — 54% of ALL 5xx replies), each one a borrow of the main
/// pool, which on superbnc is a single login shared with spread scans and SITE INVITE.
///
/// The two callers got the verdict wrong in opposite directions (pattern #2 / #3):
///   * DashboardViewModel remembered nothing, so a permanent 5xx was re-asked forever.
///   * GlDriveFileSystem treated ANY null — including a 5s borrow timeout on that busy
///     single-login pool — as "unsupported" for the whole mount, so one transient left
///     the drive reporting default free space until the next remount.
///
/// The verdict now lives on the resource both callers share (FtpOperations, one per
/// mount, so a remount re-probes once). RFC 959: a 5yz reply is a permanent negative
/// completion — "the user is discouraged from repeating the exact request". Nothing
/// else is a finding about the server.
/// </summary>
public class DiskFreeVerdictTests
{
    [Fact]
    public void GlftpdTotalsParse()
    {
        Assert.Equal(Verdict.Parsed,
            FtpOperations.ClassifyDiskFreeReply("200", "Total: 2048 MB Free: 512 MB", out var total, out var free));
        Assert.Equal(2048L * 1024 * 1024, total);
        Assert.Equal(512L * 1024 * 1024, free);
    }

    // The exact reply both production servers gave 15,110 times, plus the rest of the 5yz class.
    [Theory]
    [InlineData("500", "'SITE DISKFREE': Command not understood.")]
    [InlineData("502", "Command not implemented.")]
    [InlineData("550", "Permission denied.")]
    public void PermanentNegativeReplyIsUnsupported(string code, string message)
        => Assert.Equal(Verdict.Unsupported, FtpOperations.ClassifyDiskFreeReply(code, message, out _, out _));

    // A 2xx whose body we cannot read will read the same way next time: the format is the
    // server's, not the moment's.
    [Fact]
    public void SuccessWithUnreadableBodyIsUnsupported()
        => Assert.Equal(Verdict.Unsupported,
            FtpOperations.ClassifyDiskFreeReply("200", "Command Successful.", out _, out _));

    // A 4yz is transient by definition, and an empty code means no reply was read at all.
    [Theory]
    [InlineData("421", "Service not available, closing control connection.")]
    [InlineData("450", "Requested action not taken.")]
    [InlineData("", "")]
    public void TransientOrMissingReplyIsNotAVerdict(string code, string message)
        => Assert.Equal(Verdict.Transient, FtpOperations.ClassifyDiskFreeReply(code, message, out _, out _));

    [Fact]
    public async Task PermanentRejectionStopsFurtherSends()
    {
        var ops = new FtpOperations(null!);
        var sends = 0;
        Task<(string, string)> Reject(CancellationToken _)
        {
            sends++;
            return Task.FromResult(("500", "'SITE DISKFREE': Command not understood."));
        }

        Assert.Null(await ops.GetDiskFree(Reject, CancellationToken.None));
        Assert.True(ops.DiskFreeUnsupported);
        Assert.Null(await ops.GetDiskFree(Reject, CancellationToken.None));
        Assert.Null(await ops.GetDiskFree(Reject, CancellationToken.None));
        Assert.Equal(1, sends);
    }

    // The filesystem's old behaviour: a borrow timeout on a busy pool disabled the query for
    // the life of the mount. A failure that never reached the server says nothing about it.
    [Fact]
    public async Task TransientFailureDoesNotDisableTheQuery()
    {
        var ops = new FtpOperations(null!);
        var sends = 0;
        Task<(string, string)> TimeOutThenAnswer(CancellationToken _)
        {
            if (++sends == 1) throw new TimeoutException("no free connection in the pool");
            return Task.FromResult(("200", "Total: 10 GB Free: 4 GB"));
        }

        Assert.Null(await ops.GetDiskFree(TimeOutThenAnswer, CancellationToken.None));
        Assert.False(ops.DiskFreeUnsupported);
        var disk = await ops.GetDiskFree(TimeOutThenAnswer, CancellationToken.None);
        Assert.Equal((10L << 30, 4L << 30), disk);
        Assert.Equal(2, sends);
    }

    // A caller abandoning its own wait observed nothing about the server either.
    [Fact]
    public async Task CancellationIsNotAVerdict()
    {
        var ops = new FtpOperations(null!);
        Task<(string, string)> Cancelled(CancellationToken _) => throw new OperationCanceledException();

        Assert.Null(await ops.GetDiskFree(Cancelled, CancellationToken.None));
        Assert.False(ops.DiskFreeUnsupported);
    }
}
