using System.Collections.Concurrent;
using System.Reflection;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class SpreadLateReleaseTests
{
    [Fact]
    public async Task Existing_partial_sites_are_receivers_and_late_volume_reopens_completion()
    {
        await using var fixture = new Fixture();
        var job = fixture.Job;
        fixture.Observe("source", "release.sfv", "first.bin");
        fixture.Observe("destination", "release.sfv", "first.bin");
        Assert.True(job.ApplySfvInventory("release.sfv", "first.bin 00000000\nsecond.bin 00000000\n"));
        fixture.Reconcile();

        Assert.True(job.HasPendingReleaseInventory());
        Assert.False(fixture.Complete());
        Assert.False(fixture.Terminal());
        Assert.Null(fixture.NextTransfer());

        fixture.Observe("source", "release.sfv", "first.bin", "second.bin");
        fixture.Reconcile();
        var transfer = Assert.NotNull(fixture.NextTransfer());
        Assert.Equal("second.bin", transfer.file.Name);
        Assert.Equal("source", transfer.srcId);
        Assert.Equal("destination", transfer.dstId);
        Assert.False(fixture.Terminal());

        fixture.Observe("destination", "release.sfv", "first.bin", "second.bin");
        fixture.Reconcile();
        Assert.True(fixture.Complete());
        Assert.True(fixture.Terminal());

        // A later manifest revision must reopen Complete, including for a site
        // discovered as a source because its directory existed before the race.
        Assert.True(job.ApplySfvInventory("release.sfv", "first.bin 00000000\nsecond.bin 00000000\nthird.bin 00000000\n"));
        fixture.Reconcile();
        Assert.False(fixture.Complete());
        Assert.False(fixture.Terminal());
    }

    [Fact]
    public async Task Ancillary_file_cannot_stand_in_for_missing_manifest_volume()
    {
        await using var fixture = new Fixture();
        fixture.Observe("source", "release.sfv", "first.bin", "release.nfo");
        fixture.Observe("destination", "release.sfv", "first.bin", "release.nfo");
        fixture.Job.ApplySfvInventory("release.sfv", "first.bin 00000000\nsecond.bin 00000000\n");
        fixture.Reconcile();
        Assert.Equal(4, fixture.Job.Sites["destination"].FilesTotal);
        Assert.True(fixture.Job.HasPendingReleaseInventory());
        Assert.False(fixture.Complete());
        Assert.False(fixture.Terminal());
    }

    [Fact]
    public async Task Empty_or_partial_manifest_waits_and_valid_rewrite_replaces_prior_inventory()
    {
        await using var fixture = new Fixture();
        fixture.Observe("source", "release.sfv", "first.bin");
        fixture.Observe("destination", "release.sfv", "first.bin");
        Assert.False(fixture.Job.ApplySfvInventory("release.sfv", "; still uploading\n"));
        Assert.True(fixture.Job.HasPendingReleaseInventory());
        Assert.False(fixture.Complete());
        Assert.False(fixture.Job.ApplySfvInventory("release.sfv", "first.bin 00000000\nsecond.bin 0000"));
        Assert.True(fixture.Job.HasPendingReleaseInventory());
        Assert.True(fixture.Job.ApplySfvInventory("release.sfv", "first.bin 00000000\nsecond.bin 00000000\n"));
        Assert.True(fixture.Job.HasPendingReleaseInventory());
        Assert.True(fixture.Job.ApplySfvInventory("release.sfv", "first.bin 00000000\n"));
        fixture.Reconcile();
        Assert.False(fixture.Job.HasPendingReleaseInventory());
        Assert.True(fixture.Complete());
    }

    [Fact]
    public async Task Missing_marker_blocks_both_completion_modes_even_with_full_counts_and_complete_marker()
    {
        await using var fixture = new Fixture();
        fixture.Observe("source", "first.bin");
        fixture.Observe("destination", "first.bin");
        Field<ConcurrentDictionary<string, bool>>(fixture.Job, "_destSawMarker")["destination"] = true;
        var missing = Field<ConcurrentDictionary<string, bool>>(fixture.Job, "_destHasMissingStub");
        missing["destination"] = true;
        fixture.Reconcile();
        Assert.False(fixture.Complete());
        Assert.False(fixture.Terminal());
        Assert.False(fixture.Job.Sites["destination"].IsComplete);
        missing["destination"] = false;
        fixture.Reconcile();
        Assert.True(fixture.Terminal());
    }

    [Fact]
    public async Task Live_pool_replacement_reaches_scheduler_and_registry_absence_never_uses_snapshot()
    {
        await using var fixture = new Fixture();
        await using var replacement = new FtpConnectionPool(null!, 1);
        var live = new Dictionary<string, FtpConnectionPool>(fixture.Pools);
        fixture.Job.LivePoolResolver = id => live.GetValueOrDefault(id);
        fixture.Job.LiveMainPoolResolver = id => live.GetValueOrDefault(id);
        fixture.Observe("source", "first.bin");
        fixture.Observe("destination");
        Assert.NotNull(fixture.NextTransfer());

        await fixture.Pools["source"].DisposeAsync();
        live.Remove("source");
        Assert.Null(fixture.Job.ResolveSpreadPool("source"));
        Assert.Null(fixture.Job.ResolveMainPool("source"));
        Assert.Null(fixture.NextTransfer());

        live["source"] = replacement;
        Assert.Same(replacement, fixture.Job.ResolveSpreadPool("source"));
        Assert.Same(replacement, fixture.Job.ResolveMainPool("source"));
        Assert.NotNull(fixture.NextTransfer());
        // Also reject a disposed instance if registry removal is momentarily late.
        await replacement.DisposeAsync();
        Assert.Null(fixture.NextTransfer());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Dictionary<string, FtpConnectionPool> Pools { get; } = new()
        {
            ["source"] = new(null!, 1), ["destination"] = new(null!, 1)
        };
        public Dictionary<string, string> Paths { get; } = new()
        {
            ["source"] = "/source/Release", ["destination"] = "/dest/Release"
        };
        public SpreadJob Job { get; }
        private readonly SpeedTracker _speeds = new();
        private readonly HashSet<string> _sources = ["source", "destination"];

        public Fixture()
        {
            Job = new("TV", "Release", SpreadMode.Race, new SpreadConfig(), Pools, Pools,
                new() { ["source"] = new() { Id = "source" }, ["destination"] = new() { Id = "destination" } },
                _speeds, new SkiplistEvaluator());
            Field<HashSet<string>>(Job, "_sourceServersField").UnionWith(_sources);
            typeof(SpreadJob).GetField("_sitePathsRef", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Job, Paths);
        }

        public void Observe(string id, params string[] names) => Invoke(Job, "ProcessFiles", id,
            names.Select(n => new SpreadFileInfo { Name = n, FullPath = Paths[id] + "/" + n, Size = 100 }).ToList(), 0L);
        public void Reconcile() => Invoke(Job, "EvaluateDestCompletion", Job.ReconcileProgressFromOwnership());
        public bool Complete() => (bool)Invoke(Job, "IsJobComplete")!;
        public bool Terminal() => (bool)Invoke(Job, "AllDestinationsTerminal", Paths, _sources)!;
        public (SpreadFileInfo file, string srcId, string dstId)? NextTransfer() =>
            ((SpreadFileInfo file, string srcId, string dstId)?)Invoke(Job, "FindBestTransfer", Paths, new SpreadScorer(_speeds));
        public async ValueTask DisposeAsync()
        {
            Job.Dispose();
            foreach (var pool in Pools.Values) await pool.DisposeAsync();
        }
    }

    private static object? Invoke(SpreadJob job, string name, params object[] args) =>
        typeof(SpreadJob).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(job, args);
    private static T Field<T>(SpreadJob job, string name) =>
        (T)typeof(SpreadJob).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(job)!;
}
