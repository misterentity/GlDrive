using System.IO;
using System.Reflection;
using GlDrive.Config;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class SpreadFinalProgressTests
{
    private static T Field<T>(SpreadJob job, string name) =>
        (T)typeof(SpreadJob).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(job)!;

    private static SpreadJob Seed(int actualOwned, int staleOwned = 23, int expected = 22)
    {
        var job = new SpreadJob("TV", "Final.Progress.Fixture-GROUP", SpreadMode.Race,
            new SpreadConfig(), new(), new(), new(), new SpeedTracker(), new SkiplistEvaluator());
        var sites = Field<Dictionary<string, SiteProgress>>(job, "_siteProgress");
        sites["source"] = new SiteProgress { ServerId = "source", IsSource = true, FilesOwned = 24, FilesTotal = 24 };
        sites["destination"] = new SiteProgress { ServerId = "destination", FilesOwned = staleOwned, FilesTotal = 24, IsComplete = staleOwned >= 24 };
        var counts = Field<Dictionary<string, int>>(job, "_serverFileCount");
        counts["source"] = 24;
        counts["destination"] = actualOwned;
        var files = Field<Dictionary<string, SpreadFileInfo>>(job, "_fileInfos");
        for (var i = 0; i < 24; i++) files[$"file{i}"] = new SpreadFileInfo { Name = $"file{i}" };
        typeof(SpreadJob).GetField("_expectedFileCount", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(job, expected);
        return job;
    }

    [Theory]
    [InlineData(24, true)]
    [InlineData(20, false)]
    public async Task Terminal_lifecycle_refreshes_counts_even_on_failure(int owned, bool siteComplete)
    {
        using var job = Seed(owned);
        // No pools/configured sites: discovery exits before any network I/O. Its
        // finally must still reconcile the authoritative pre-existing ownership.
        await job.RunAsync();
        var destination = job.Sites["destination"];
        Assert.Equal(owned, destination.FilesOwned);
        Assert.Equal(24, destination.FilesTotal);
        Assert.Equal(siteComplete, destination.IsComplete);
        Assert.Equal(SpreadJobState.Failed, job.State);
    }

    [Fact]
    public async Task Final_snapshot_includes_a_transfer_that_finishes_during_drain()
    {
        using var job = Seed(23);
        using var transfer = job.TrackActiveTransfer("last-file|source->destination", new ActiveTransferInfo());
        var finish = job.RunAsync();
        Assert.False(finish.IsCompleted);
        Field<Dictionary<string, int>>(job, "_serverFileCount")["destination"] = 24;
        transfer.Dispose();
        await finish.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(24, job.Sites["destination"].FilesOwned);
        Assert.True(job.Sites["destination"].IsComplete);
        Assert.Empty(job.ActiveTransferList);
    }

    [Theory]
    [InlineData(SpreadJobState.Completed, 24, 22, true)]
    [InlineData(SpreadJobState.Completed, 20, 22, false)]
    [InlineData(SpreadJobState.Completed, 24, 25, false)]
    [InlineData(SpreadJobState.Failed, 24, 22, false)]
    [InlineData(SpreadJobState.Stopped, 24, 22, false)]
    public void History_uses_final_ownership_without_hiding_partial_or_failed_results(
        SpreadJobState state, int owned, int expected, bool clean)
    {
        using var job = Seed(owned, expected: expected);
        typeof(SpreadJob).GetProperty(nameof(SpreadJob.State))!.SetValue(job, state);
        job.ReconcileProgressFromOwnership();
        var directory = Path.Combine(Path.GetTempPath(), "gldrive-final-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var manager = new SpreadManager(new AppConfig(), directory);
            typeof(SpreadManager).GetMethod("RecordHistory", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(manager, [job]);
            var recorded = Assert.Single(manager.History.Items);
            Assert.Equal(owned, recorded.FilesDelivered);
            Assert.Equal(Math.Max(24, expected), recorded.FilesTotal);
            Assert.Equal(state, recorded.Result);
            Assert.Equal(clean, recorded.CleanComplete);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
