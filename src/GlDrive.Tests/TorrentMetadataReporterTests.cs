using System.IO;
using GlDrive.Player;
using Xunit;

namespace GlDrive.Tests;

public sealed class TorrentMetadataReporterTests
{
    private sealed class Reporter
    {
        public bool Stopped { get; private set; }
        public async Task Run(CancellationToken ct)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally { Stopped = true; }
        }
    }

    [Fact]
    public async Task Successful_metadata_stops_reporter_and_returns_before_deadline()
    {
        using var caller = new CancellationTokenSource();
        var reporter = new Reporter();
        var fetch = TorrentStreamService.FetchMetadataWithReporterAsync(
            _ => Task.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1, 2, 3 }), reporter.Run,
            TimeSpan.FromSeconds(5), caller.Token);
        try
        {
            var result = await fetch.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(new byte[] { 1, 2, 3 }, result.ToArray());
            Assert.True(reporter.Stopped);
            Assert.False(caller.IsCancellationRequested);
        }
        finally { caller.Cancel(); await fetch; }
    }

    [Fact]
    public async Task Failed_metadata_stops_reporter_and_preserves_exception()
    {
        using var caller = new CancellationTokenSource();
        var reporter = new Reporter();
        var failure = new IOException("fixture metadata failure");
        var fetch = TorrentStreamService.FetchMetadataWithReporterAsync(
            _ => Task.FromException<ReadOnlyMemory<byte>>(failure), reporter.Run,
            TimeSpan.FromSeconds(5), caller.Token);
        try
        {
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => fetch.WaitAsync(TimeSpan.FromSeconds(1))));
            Assert.True(reporter.Stopped);
        }
        finally { caller.Cancel(); try { await fetch; } catch (IOException) { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_and_caller_cancellation_stop_both_tasks(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        var reporter = new Reporter();
        var fetch = TorrentStreamService.FetchMetadataWithReporterAsync(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ReadOnlyMemory<byte>.Empty;
        }, reporter.Run, cancelCaller ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(30), caller.Token);
        if (cancelCaller) caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(reporter.Stopped);
        Assert.Equal(cancelCaller, caller.IsCancellationRequested);
    }
}
