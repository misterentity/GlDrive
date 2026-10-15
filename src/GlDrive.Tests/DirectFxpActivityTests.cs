using System.IO;
using System.Reflection;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

[Collection("Config read-only mode")]
public sealed class DirectFxpActivityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gldrive-direct-fxp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private SpreadManager CreateManager(params string[] sites)
    {
        Directory.CreateDirectory(_directory);
        var manager = new SpreadManager(new AppConfig(), _directory);
        var pools = Pools(manager);
        foreach (var site in sites)
        {
            // One checked-out slot, no idle connection: Borrow waits until cancellation.
            // No client/factory is used, no login is opened and no live state is touched.
            var pool = new FtpConnectionPool(null!, 1);
            typeof(FtpConnectionPool).GetField("_created", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(pool, 1);
            typeof(FtpConnectionPool).GetProperty(nameof(FtpConnectionPool.UseCpsv))!.SetValue(pool, true);
            pools.Add(site, pool);
        }
        return manager;
    }

    private static Dictionary<string, FtpConnectionPool> Pools(SpreadManager manager) =>
        (Dictionary<string, FtpConnectionPool>)typeof(SpreadManager)
            .GetField("_spreadPools", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;

    private static Task Start(SpreadManager manager, bool directory, string source, string destination, CancellationToken ct) =>
        directory
            ? manager.StartFxpDirectory(source, "/release", destination, "/target", ct)
            : manager.StartFxp(source, "/release/file.bin", destination, "/target/file.bin", ct);

    private static async Task CancelAndDrain(Task task, CancellationTokenSource cts)
    {
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingDirectTransfer_RequestsBothSitesBeforeFirstBorrow_AndClearsOnCancel(bool directory)
    {
        using var manager = CreateManager("src", "dst", "other");
        using var cts = new CancellationTokenSource();
        var transfer = Start(manager, directory, "src", "dst", cts.Token);
        try
        {
            Assert.False(transfer.IsCompleted); // The actual entrypoint is waiting on the held source pool.
            Assert.False(manager.IsRaceIdle("src"));
            Assert.False(manager.IsRaceIdle("dst"));
            Assert.True(manager.IsRaceIdle("other"));
            var lending = manager.TryBorrowAsync("src", TimeSpan.FromSeconds(5), cts.Token);
            Assert.True(lending.IsCompleted); // Activity rejects new lending without waiting on the pool.
            Assert.Null(await lending);
        }
        finally { await CancelAndDrain(transfer, cts); }

        Assert.True(manager.IsRaceIdle("src"));
        Assert.True(manager.IsRaceIdle("dst"));
    }

    [Fact]
    public async Task OverlappingFileAndDirectoryTransfers_KeepSharedSiteBusyUntilLastCancellation()
    {
        using var manager = CreateManager("shared", "a", "b");
        using var firstCts = new CancellationTokenSource();
        using var secondCts = new CancellationTokenSource();
        var first = Start(manager, false, "shared", "a", firstCts.Token);
        var second = Start(manager, true, "shared", "b", secondCts.Token);
        try
        {
            Assert.False(manager.IsRaceIdle("shared"));
            await CancelAndDrain(first, firstCts);
            Assert.True(manager.IsRaceIdle("a"));
            Assert.False(manager.IsRaceIdle("shared"));
            Assert.False(manager.IsRaceIdle("b"));
        }
        finally
        {
            await CancelAndDrain(first, firstCts);
            await CancelAndDrain(second, secondCts);
        }
        Assert.True(manager.IsRaceIdle("shared"));
        Assert.True(manager.IsRaceIdle("b"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdenticalEndpoints_AreRejectedBeforeBorrowWithoutResidualActivity(bool directory)
    {
        using var manager = CreateManager("same");
        await Assert.ThrowsAsync<ArgumentException>(() => Start(manager, directory, "same", "same", default));
        Assert.True(manager.IsRaceIdle("same"));
    }

    [Theory]
    [InlineData(false, "missing", "dst")]
    [InlineData(false, "src", "missing")]
    [InlineData(true, "missing", "dst")]
    [InlineData(true, "src", "missing")]
    public async Task MissingPool_DoesNotLeaveActivity(bool directory, string source, string destination)
    {
        using var manager = CreateManager("src", "dst");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Start(manager, directory, source, destination, default));
        Assert.True(manager.IsRaceIdle(source));
        Assert.True(manager.IsRaceIdle(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BorrowFailure_DoesNotLeaveActivity(bool directory)
    {
        using var manager = CreateManager("src", "dst");
        // Directory listing starts on its source; a file pair starts in site order.
        await Pools(manager)[directory ? "src" : "dst"].DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Start(manager, directory, "src", "dst", default));
        Assert.True(manager.IsRaceIdle("src"));
        Assert.True(manager.IsRaceIdle("dst"));
    }

    [Fact]
    public async Task FileTransfer_BorrowsCanonicalDestinationBeforeHeldSource()
    {
        using var manager = CreateManager("z-source", "a-destination");
        await Pools(manager)["a-destination"].DisposeAsync();
        // The source pool is held indefinitely. Reaching the destination failure
        // proves the actual entrypoint uses canonical order rather than source-first.
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await Start(manager, false, "z-source", "a-destination", default)
                .WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(manager.IsRaceIdle("z-source"));
        Assert.True(manager.IsRaceIdle("a-destination"));
    }
}
