using System.IO;
using System.Reflection;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// v3.11.24 — a destination dupe verdict was credited, then revoked by the next listing.
///
/// 2026-10-20 18:01–18:06 Brothers.2026.S01E06.1080p.WEB.H264-CAKES: another racer had left a
/// 1,741,452-byte sample on zephyr (source: 83,380,161). glftpd's dupe checker refused our STOR
/// ("It was uploaded by altruist"), the dupe-skip credited zephyr as owner, and the next listing
/// saw the size mismatch and revoked it — so the file was re-sent into the same dupe 7 times and
/// the race held the only race slot for 5 min while 20 releases queued. It finished only when a
/// completion check happened to fall between a dupe-skip and the next listing.
/// </summary>
public sealed class SpreadDupeHeldOwnershipTests
{
    private const string Sample = "Sample/release-sample.mkv";

    [Fact]
    public async Task A_dupe_verdict_survives_a_later_listing_of_the_short_copy()
    {
        await using var fixture = new Fixture();
        fixture.Observe("source", 0, (Sample, 83_380_161));
        fixture.Observe("destination", 0, (Sample, 1_741_452));
        Assert.False(fixture.Owns("destination", Sample));

        fixture.CreditDupe(Sample, 83_380_161, "destination");
        Assert.True(fixture.Owns("destination", Sample));

        // A listing taken AFTER the verdict: the short copy is still there, still refused.
        fixture.Observe("destination", 100, (Sample, 1_741_452));
        Assert.True(fixture.Owns("destination", Sample), "re-sending a file the dest refuses as a dupe loops forever");
    }

    [Fact]
    public async Task The_verdict_ends_when_the_destination_stops_listing_the_file()
    {
        await using var fixture = new Fixture();
        fixture.Observe("source", 0, (Sample, 83_380_161), ("release.r00", 100));
        fixture.Observe("destination", 0, (Sample, 1_741_452), ("release.r00", 100));
        fixture.CreditDupe(Sample, 83_380_161, "destination");

        // The short copy was deleted (zipscript / nuke / site op): the dupe no longer stands.
        fixture.Observe("destination", 100, ("release.r00", 100));
        Assert.False(fixture.Owns("destination", Sample));

        // Someone else's short copy reappearing later is NOT covered by the old verdict.
        fixture.Observe("destination", 101, (Sample, 1_741_452), ("release.r00", 100));
        Assert.False(fixture.Owns("destination", Sample));

        // …and the listing is read normally again: a complete copy is credited from it.
        fixture.Observe("destination", 102, (Sample, 83_380_161), ("release.r00", 100));
        Assert.True(fixture.Owns("destination", Sample));
    }

    [Fact]
    public async Task A_listing_that_started_before_the_verdict_cannot_end_it()
    {
        await using var fixture = new Fixture();
        fixture.Observe("source", 0, (Sample, 83_380_161), ("release.r00", 100));
        fixture.Observe("destination", 0, (Sample, 1_741_452), ("release.r00", 100));
        fixture.CreditDupe(Sample, 83_380_161, "destination");

        // Snapshot from before the dupe-skip (epoch 0) that happens not to show the file.
        fixture.Observe("destination", 0, ("release.r00", 100));
        fixture.Observe("destination", 100, (Sample, 1_741_452), ("release.r00", 100));
        Assert.True(fixture.Owns("destination", Sample));
    }

    [Fact]
    public async Task A_real_delivery_replaces_an_earlier_dupe_verdict()
    {
        await using var fixture = new Fixture();
        fixture.Observe("source", 0, ("release.r00", 100));
        fixture.CreditDupe("release.r00", 100, "destination");
        fixture.CreditDelivery("release.r00", 100, "destination");

        fixture.Observe("destination", 100, ("release.r00", 40));
        Assert.False(fixture.Owns("destination", "release.r00"));
    }

    [Fact]
    public async Task A_real_delivery_is_not_held_against_a_later_short_listing()
    {
        await using var fixture = new Fixture();
        fixture.Observe("source", 0, ("release.r00", 100));
        fixture.CreditDelivery("release.r00", 100, "destination");

        // Only dupe verdicts are sticky: a delivered file that later lists short was truncated.
        fixture.Observe("destination", 100, ("release.r00", 40));
        Assert.False(fixture.Owns("destination", "release.r00"));
    }

    [Fact]
    public void The_transfer_success_path_credits_through_the_shared_helper()
    {
        var job = Job();
        var execute = Slice(job, "var ok = await transfer.ExecuteAsync(", "Log.Information(\"FXP complete: {File}");
        Assert.Contains("CreditDeliveryNoLock(file.Name, file.Size, dstId, transfer.WasDupe)", execute);
        Assert.DoesNotContain("owners.Add(dstId)", execute);
    }

    [Fact]
    public void The_sfv_read_prefers_the_main_pool_like_the_scan()
    {
        var sfv = Slice(Job(), "private async Task ParseSfvForCount", "internal bool ApplySfvInventory");
        Assert.Contains("ResolveMainPool(serverId)", sfv);
        Assert.Contains("PoolFallback.BorrowAsync(", sfv);
        Assert.Contains("SfvMainPoolBudget, borrowCts.Token)", sfv);
        // The spread pool is the fallback, never the first choice: its logins carry FXP/relay
        // transfers that outlast the 15s read budget (204 of ~640 reads failed on 2026-10-20).
        Assert.Contains("var pool = mainPool ?? spreadPool!;", sfv);
        Assert.Contains("PoolFallback.BorrowAsync(pool.Borrow, fallback,", sfv);
        // The lender's CPSV settings drive the read, so the fallback must re-point `pool`.
        Assert.Contains("t => { pool = spreadPool; return spreadPool.Borrow(t); }", sfv);
        Assert.DoesNotContain("var pool = ResolveSpreadPool(serverId);", sfv);
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

        public Fixture()
        {
            Job = new("TV", "Release", SpreadMode.Race, new SpreadConfig(), Pools, Pools,
                new() { ["source"] = new() { Id = "source" }, ["destination"] = new() { Id = "destination" } },
                new SpeedTracker(), new SkiplistEvaluator());
            Field<HashSet<string>>(Job, "_sourceServersField").Add("source");
            typeof(SpreadJob).GetField("_sitePathsRef", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Job, Paths);
        }

        public void Observe(string id, long epoch, params (string name, long size)[] files)
        {
            lock (Field<System.Threading.Lock>(Job, "_ownershipLock"))
                Invoke(Job, "ProcessFiles", id,
                    files.Select(f => new SpreadFileInfo { Name = f.name, FullPath = Paths[id] + "/" + f.name, Size = f.size }).ToList(), epoch);
        }

        public void CreditDupe(string name, long size, string dst) => Credit(name, size, dst, true);
        public void CreditDelivery(string name, long size, string dst) => Credit(name, size, dst, false);

        private void Credit(string name, long size, string dst, bool wasDupe)
        {
            lock (Field<System.Threading.Lock>(Job, "_ownershipLock"))
                Invoke(Job, "CreditDeliveryNoLock", name, size, dst, wasDupe);
        }

        public bool Owns(string id, string name) =>
            Field<Dictionary<string, HashSet<string>>>(Job, "_fileOwnership").TryGetValue(name, out var owners) && owners.Contains(id);

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
