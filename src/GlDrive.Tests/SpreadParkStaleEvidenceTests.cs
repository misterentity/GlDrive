using System.IO;
using System.Reflection;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.11.22 — a finished race parked as "Waiting for remaining release files".
///
/// 2026-10-19 09:15:41 Tear.Along.the.Dotted.Line.S01E02.HDR: FindBestTransfer reported
/// owned=14 (7 files x 2 sites) and zephyr's bot announced COMPLETE, yet the race returned
/// its slot and sat queued for 40 min. The 30s SFV refresh had REVOKED the parsed manifest
/// before re-reading it, and the re-read failed ("Account login cap reached"), so the race
/// saw "manifest unknown". 118 of 130 SFV reads that day failed the same way (15s borrow
/// wait behind a busy transfer) — harmless alone, fatal when one coincided with the last file.
///
/// 09:22:08 S01E03.DV parked 2.3s after its last file landed, on listings taken before that
/// delivery (a dest stub / progress marker the delivery had just cleared).
/// </summary>
public sealed class SpreadParkStaleEvidenceTests
{
    [Fact]
    public async Task Age_refresh_rereads_the_manifest_without_revoking_it()
    {
        await using var fixture = new Fixture();
        fixture.Observe("source", "release.sfv", "first.bin");
        fixture.Observe("destination", "release.sfv", "first.bin");
        Assert.True(fixture.Job.ApplySfvInventory("release.sfv", "first.bin 00000000\n"));
        var pending = Field<Dictionary<string, (string, string)>>(fixture.Job, "_pendingSfvs");
        pending.Clear(); // RunAsync dispatched the first read
        fixture.Reconcile();
        Assert.False(fixture.Job.HasPendingReleaseInventory());
        fixture.Observe("destination", "release.sfv", "first.bin");
        Assert.Empty(pending);

        // The manifest is now older than the refresh interval.
        Field<Dictionary<string, DateTime>>(fixture.Job, "_sfvParsedAt")["release.sfv"] = DateTime.UtcNow.AddSeconds(-31);
        fixture.Observe("destination", "release.sfv", "first.bin");

        Assert.True(pending.ContainsKey("release.sfv"), "a stale manifest must still be re-read");
        Assert.False(fixture.Job.HasPendingReleaseInventory());
        Assert.True(fixture.Complete());
    }

    [Fact]
    public async Task A_resized_manifest_is_unknown_until_reread()
    {
        await using var fixture = new Fixture();
        fixture.Observe("source", "release.sfv", "first.bin");
        fixture.Observe("destination", "release.sfv", "first.bin");
        Assert.True(fixture.Job.ApplySfvInventory("release.sfv", "first.bin 00000000\n"));
        fixture.Reconcile();
        Assert.False(fixture.Job.HasPendingReleaseInventory());

        fixture.ObserveFiles("source", 0, ("release.sfv", 140), ("first.bin", 100));
        Assert.True(fixture.Job.HasPendingReleaseInventory());
        Assert.True(Field<Dictionary<string, (string, string)>>(fixture.Job, "_pendingSfvs").ContainsKey("release.sfv"));
    }

    [Fact]
    public async Task A_delivery_newer_than_every_listing_defers_the_park_until_relisted()
    {
        await using var fixture = new Fixture();
        fixture.Observe("source", "first.bin", "second.bin");
        fixture.Observe("destination", "first.bin");
        Invoke(fixture.Job, "RecordConfirmedDeliveryNoLock", "second.bin", "destination");
        var now = DateTime.UtcNow;

        Assert.True(fixture.Job.DeliveryAwaitingListing(now));

        // A listing that started before the delivery still cannot have seen it.
        fixture.ObserveSized("destination", 0, 100, "first.bin", "second.bin");
        Assert.True(fixture.Job.DeliveryAwaitingListing(now));

        fixture.ObserveSized("destination", 1, 100, "first.bin", "second.bin");
        Assert.False(fixture.Job.DeliveryAwaitingListing(now));
    }

    [Fact]
    public async Task The_park_deferral_is_bounded_by_time()
    {
        await using var fixture = new Fixture();
        fixture.Observe("source", "first.bin");
        fixture.Observe("destination");
        Invoke(fixture.Job, "RecordConfirmedDeliveryNoLock", "first.bin", "destination");

        Assert.True(fixture.Job.DeliveryAwaitingListing(DateTime.UtcNow));
        Assert.False(fixture.Job.DeliveryAwaitingListing(DateTime.UtcNow + SpreadJob.ParkEvidenceWait + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void The_park_branch_waits_for_post_delivery_listings()
    {
        var run = Slice(Job(), "public async Task RunAsync()", "private async Task ScanSites(");
        var park = run.IndexOf("WaitReason = poolsUnavailable", StringComparison.Ordinal);
        Assert.True(park > 0);
        var guard = run.LastIndexOf("if (_backgroundScan is { IsCompleted: false }", park, StringComparison.Ordinal);
        Assert.True(guard > 0);
        var condition = run[guard..run.IndexOf('\n', guard)];
        Assert.Contains("waitingForRelease && DeliveryAwaitingListing(DateTime.UtcNow)", condition);
    }

    [Fact]
    public void Every_confirmed_delivery_goes_through_the_recorder()
    {
        var job = Job();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(job, @"\+\+_ownershipEpoch"));
        var recorder = Slice(job, "private void RecordConfirmedDeliveryNoLock", "internal bool DeliveryAwaitingListing");
        Assert.Contains("++_ownershipEpoch", recorder);
        Assert.Contains("_deliveryEpoch[dstId]", recorder);
        Assert.Contains("_lastDeliveryAt = DateTime.UtcNow", recorder);
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
        private readonly HashSet<string> _sources = ["source"];

        public Fixture()
        {
            Job = new("TV", "Release", SpreadMode.Race, new SpreadConfig(), Pools, Pools,
                new() { ["source"] = new() { Id = "source" }, ["destination"] = new() { Id = "destination" } },
                new SpeedTracker(), new SkiplistEvaluator());
            Field<HashSet<string>>(Job, "_sourceServersField").UnionWith(_sources);
            typeof(SpreadJob).GetField("_sitePathsRef", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Job, Paths);
        }

        public void Observe(string id, params string[] names) => ObserveSized(id, 0, 100, names);
        public void ObserveSized(string id, long epoch, long size, params string[] names) =>
            ObserveFiles(id, epoch, names.Select(n => (n, size)).ToArray());
        public void ObserveFiles(string id, long epoch, params (string name, long size)[] files)
        {
            // Mirror ScanSites: ProcessFiles runs under _ownershipLock.
            var gate = Field<System.Threading.Lock>(Job, "_ownershipLock");
            lock (gate)
                Invoke(Job, "ProcessFiles", id,
                    files.Select(f => new SpreadFileInfo { Name = f.name, FullPath = Paths[id] + "/" + f.name, Size = f.size }).ToList(), epoch);
        }
        public void Reconcile() => Invoke(Job, "EvaluateDestCompletion", Job.ReconcileProgressFromOwnership());
        public bool Complete() => (bool)Invoke(Job, "IsJobComplete")!;
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

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, end);
        return source[from..to];
    }

    private static string Job()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var path = Path.Combine(dir, "src", "GlDrive", "Spread", "SpreadJob.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("SpreadJob.cs not found from " + AppContext.BaseDirectory);
    }
}
