using System.Reflection;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class SpreadGrowingFileTests
{
    [Theory]
    [InlineData(40_894_464)]
    [InlineData(30_932_992)]
    [InlineData(31_195_136)]
    [InlineData(786_432)]
    public async Task Source_growth_reopens_the_destination_until_its_full_copy_is_observed(long initialSize)
    {
        // October 13 telemetry showed these successful byte counts followed by
        // 50 MB successes for the same files. It did not capture the expected size,
        // so growth is a possible explanation, not a proven production diagnosis.
        // Exercise the actual scan application, scheduler and completion wiring for
        // that scenario; these uninitialized pools never make network connections.
        const long finalSize = 50_000_000;
        const string fileName = "growing.r00";
        var source = new ServerConfig { Id = "source", Name = "Source" };
        var destination = new ServerConfig { Id = "destination", Name = "Destination" };
        await using var sourcePool = new FtpConnectionPool(factory: null!, maxSize: 1);
        await using var destinationPool = new FtpConnectionPool(factory: null!, maxSize: 1);
        var pools = new Dictionary<string, FtpConnectionPool>
        {
            [source.Id] = sourcePool,
            [destination.Id] = destinationPool
        };
        var paths = new Dictionary<string, string>
        {
            [source.Id] = "/source/Growing.Fixture-GROUP",
            [destination.Id] = "/destination/Growing.Fixture-GROUP"
        };
        var speeds = new SpeedTracker();
        using var job = new SpreadJob("TV", "Growing.Fixture-GROUP", SpreadMode.Race,
            new SpreadConfig(), pools, pools,
            new() { [source.Id] = source, [destination.Id] = destination },
            speeds, new SkiplistEvaluator());
        Field<HashSet<string>>(job, "_sourceServersField").Add(source.Id);
        var scorer = new SpreadScorer(speeds);

        void Observe(string serverId, long size)
        {
            Invoke(job, "ProcessFiles", serverId, new List<SpreadFileInfo>
            {
                new() { Name = fileName, FullPath = paths[serverId] + "/" + fileName, Size = size }
            }, 0L);
            // ScanSites reconciles every site's progress after applying its listings.
            Assert.Equal(1, job.ReconcileProgressFromOwnership());
        }

        (SpreadFileInfo file, string srcId, string dstId)? NextTransfer() =>
            ((SpreadFileInfo file, string srcId, string dstId)?)Invoke(job, "FindBestTransfer", paths, scorer);

        Observe(source.Id, initialSize);
        Observe(destination.Id, initialSize);
        Assert.Equal(1, job.Sites[destination.Id].FilesOwned);
        Assert.True(job.Sites[destination.Id].IsComplete);
        Assert.True((bool)Invoke(job, "IsJobComplete")!);
        Assert.Null(NextTransfer());

        Observe(source.Id, finalSize);
        Assert.Equal(1, job.Sites[source.Id].FilesOwned);
        Assert.Equal(0, job.Sites[destination.Id].FilesOwned);
        Assert.False(job.Sites[destination.Id].IsComplete);
        Assert.False((bool)Invoke(job, "IsJobComplete")!);
        var retry = NextTransfer();
        Assert.NotNull(retry);
        Assert.Equal(source.Id, retry.Value.srcId);
        Assert.Equal(destination.Id, retry.Value.dstId);
        Assert.Equal(fileName, retry.Value.file.Name);
        Assert.Equal(finalSize, retry.Value.file.Size);
        Assert.Equal(paths[source.Id] + "/" + fileName, retry.Value.file.FullPath);

        // A later listing of the still-short destination cannot shrink canonical
        // metadata or suppress the retry after the larger source was observed.
        Observe(destination.Id, initialSize);
        Assert.Equal(0, job.Sites[destination.Id].FilesOwned);
        Assert.False(job.Sites[destination.Id].IsComplete);
        Assert.False((bool)Invoke(job, "IsJobComplete")!);
        var repeatedRetry = NextTransfer();
        Assert.NotNull(repeatedRetry);
        Assert.Equal((source.Id, destination.Id, finalSize),
            (repeatedRetry.Value.srcId, repeatedRetry.Value.dstId, repeatedRetry.Value.file.Size));

        Observe(destination.Id, finalSize);
        Assert.Equal(1, job.Sites[destination.Id].FilesOwned);
        Assert.Equal(1, job.Sites[destination.Id].FilesTotal);
        Assert.True(job.Sites[destination.Id].IsComplete);
        Assert.True((bool)Invoke(job, "IsJobComplete")!);
        Assert.Null(NextTransfer());
    }

    private static object? Invoke(SpreadJob job, string method, params object[] args) =>
        typeof(SpreadJob).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(job, args);

    private static T Field<T>(SpreadJob job, string name) =>
        (T)typeof(SpreadJob).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(job)!;
}
