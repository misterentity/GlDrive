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
            new SpreadConfig { WaitForDestinationComplete = false }, new(), new(), new(), new SpeedTracker(), new SkiplistEvaluator());
        var configs = Field<IDictionary<string, ServerConfig>>(job, "_serverConfigs");
        configs["source"] = new ServerConfig { Id = "source", SpreadSite = new() { DownloadOnly = true } };
        configs["destination"] = new ServerConfig { Id = "destination" };
        typeof(SpreadJob).GetField("_sitePathsRef", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(job, new Dictionary<string, string> { ["source"] = "/source", ["destination"] = "/destination" });
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
        var recorded = Record(job);
        Assert.Equal(owned, recorded.FilesDelivered);
        Assert.Equal(Math.Max(24, expected), recorded.FilesTotal);
        Assert.Equal(state, recorded.Result);
        Assert.Equal(clean, recorded.CleanComplete);
    }

    [Theory]
    [InlineData(SpreadJobState.Completed, 24, true)]
    [InlineData(SpreadJobState.Completed, 20, false)]
    [InlineData(SpreadJobState.Failed, 24, false)]
    [InlineData(SpreadJobState.Stopped, 24, false)]
    public void An_initial_partial_source_can_receive_and_its_history_keeps_delivery(
        SpreadJobState state, int owned, bool clean)
    {
        using var job = Seed(owned);
        // Production topology: the first announce came from a partial receiver,
        // while the other source is download-only. The primary retains its src
        // display role even as this race fills its missing payload.
        job.Sites["destination"].IsSource = true;
        typeof(SpreadJob).GetProperty(nameof(SpreadJob.State))!.SetValue(job, state);
        job.ReconcileProgressFromOwnership();
        var recorded = Record(job);
        Assert.Equal(owned, recorded.FilesDelivered);
        Assert.Equal(clean, recorded.CleanComplete);
        Assert.Equal(state, recorded.Result);
        if (clean) Assert.Equal(65535, recorded.Score);
    }

    [Fact]
    public void History_excludes_read_only_affiliated_unopened_and_nonparticipating_sites()
    {
        using var job = Seed(24);
        var configs = Field<IDictionary<string, ServerConfig>>(job, "_serverConfigs");
        var sites = Field<Dictionary<string, SiteProgress>>(job, "_siteProgress");
        var paths = Field<Dictionary<string, string>>(job, "_sitePathsRef");
        foreach (var id in new[] { "read-only", "affiliated", "unopened", "not-participating" })
        {
            configs[id] = new ServerConfig { Id = id };
            sites[id] = new SiteProgress { ServerId = id };
            if (id != "not-participating") paths[id] = "/" + id;
        }
        configs["read-only"].SpreadSite.DownloadOnly = true;
        configs["affiliated"].SpreadSite.Affils = ["GROUP"];
        Field<HashSet<string>>(job, "_fillOnlyDests").Add("unopened");
        typeof(SpreadJob).GetProperty(nameof(SpreadJob.State))!.SetValue(job, SpreadJobState.Completed);
        job.ReconcileProgressFromOwnership();
        var recorded = Record(job);
        Assert.Equal(24, recorded.FilesDelivered);
        Assert.True(recorded.CleanComplete);
        Assert.Equal(65535, recorded.Score);
        Assert.Equal("destination", Assert.Single(recorded.DestinationOutcomes).ServerId);
    }

    [Theory]
    [InlineData("missing-stub")]
    [InlineData("missing-expected-file")]
    [InlineData("zipscript-timeout")]
    [InlineData("zipscript-unknown")]
    public void Full_counts_do_not_turn_unverified_inventory_into_clean_completion(string fault)
    {
        using var job = Seed(24);
        typeof(SpreadJob).GetProperty(nameof(SpreadJob.State))!.SetValue(job, SpreadJobState.Completed);
        if (fault == "missing-stub")
            Field<System.Collections.Concurrent.ConcurrentDictionary<string, bool>>(job, "_destHasMissingStub")["destination"] = true;
        else if (fault == "missing-expected-file")
            Field<HashSet<string>>(job, "_expectedFiles").Add("file0");
        else
        {
            Field<SpreadConfig>(job, "_spreadConfig").WaitForDestinationComplete = true;
            if (fault == "zipscript-timeout") Field<Dictionary<string, DestState>>(job, "_destStates")["destination"] = DestState.TimedOut;
        }
        job.ReconcileProgressFromOwnership();
        var recorded = Record(job);
        Assert.Equal(24, recorded.FilesDelivered);
        Assert.False(recorded.CleanComplete);
    }

    [Fact]
    public void Verified_completion_of_a_receiving_source_is_recorded_as_clean()
    {
        using var job = Seed(24);
        job.Sites["destination"].IsSource = true;
        Field<SpreadConfig>(job, "_spreadConfig").WaitForDestinationComplete = true;
        Field<Dictionary<string, DestState>>(job, "_destStates")["destination"] = DestState.Complete;
        Field<Dictionary<string, DateTime>>(job, "_sitebotListingStartedAtUtc")["destination"] = DateTime.UtcNow;
        typeof(SpreadJob).GetProperty(nameof(SpreadJob.State))!.SetValue(job, SpreadJobState.Completed);
        job.ReconcileProgressFromOwnership();
        var recorded = Record(job);
        Assert.Equal(24, recorded.FilesDelivered);
        Assert.True(recorded.CleanComplete);
        Assert.True(Assert.Single(recorded.DestinationOutcomes).CompletionVerified);
    }

    [Fact]
    public void A_full_writable_source_does_not_hide_a_partial_peer_or_inflate_live_score()
    {
        using var job = Seed(20);
        Field<IDictionary<string, ServerConfig>>(job, "_serverConfigs")["source"].SpreadSite.DownloadOnly = false;
        job.Sites["destination"].BytesTransferred = 1024;
        typeof(SpreadJob).GetProperty(nameof(SpreadJob.State))!.SetValue(job, SpreadJobState.Completed);
        job.ReconcileProgressFromOwnership();
        var partialScore = (int)(20L * 65535 / 24);
        Assert.Equal(partialScore, job.Score);
        var recorded = Record(job);
        // Delivery is inventory at the best eligible receiver, not bytes this
        // attempt uploaded. The partial peer still prevents clean completion.
        Assert.Equal(24, recorded.FilesDelivered);
        Assert.Equal(1024, recorded.BytesTransferred);
        Assert.False(recorded.CleanComplete);
        Assert.Equal(partialScore, recorded.Score);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void No_receiving_participants_or_zero_inventory_never_claims_clean_completion(bool noReceivers)
    {
        using var job = Seed(24);
        if (noReceivers) Field<Dictionary<string, string>>(job, "_sitePathsRef").Remove("destination");
        else
        {
            Field<Dictionary<string, SpreadFileInfo>>(job, "_fileInfos").Clear();
            Field<Dictionary<string, int>>(job, "_serverFileCount").Clear();
            typeof(SpreadJob).GetField("_expectedFileCount", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(job, 0);
        }
        typeof(SpreadJob).GetProperty(nameof(SpreadJob.State))!.SetValue(job, SpreadJobState.Completed);
        job.ReconcileProgressFromOwnership();
        var recorded = Record(job);
        Assert.Equal(0, recorded.FilesDelivered);
        Assert.False(recorded.CleanComplete);
    }

    private static RaceHistoryItem Record(SpreadJob job)
    {
        var directory = Path.Combine(Path.GetTempPath(), "gldrive-final-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var manager = new SpreadManager(new AppConfig(), directory);
            typeof(SpreadManager).GetMethod("RecordHistory", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(manager, [job]);
            return Assert.Single(manager.History.Items);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
