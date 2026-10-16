using System.IO;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Downloads.Segmented;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public sealed class AtomicStorePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-atomic-stores-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("blacklist")]
    [InlineData("abandon")]
    [InlineData("usage")]
    public void Save_writes_a_complete_reloadable_snapshot(string kind)
    {
        var path = Path.Combine(_root, kind + ".json");
        var scenario = CreateScenario(kind, path);
        scenario.First();
        var before = File.ReadAllBytes(path);
        scenario.Second();
        Assert.True(scenario.ReloadHasSecond());
        Assert.NotEqual(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData("blacklist")]
    [InlineData("abandon")]
    [InlineData("usage")]
    public void Failed_replacement_preserves_disk_and_in_memory_update_can_be_retried(string kind)
    {
        var path = Path.Combine(_root, kind + ".json");
        var scenario = CreateScenario(kind, path);
        scenario.First();
        var before = File.ReadAllBytes(path);
        // Permit in-place writes but deny replacement. The old direct-write implementation would
        // change both the disk bytes and this reader's snapshot instead of preserving them.
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            scenario.Second();
            Assert.True(scenario.MemoryHasSecond());
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.False(scenario.ReloadHasSecond());
            using var oldBytes = new MemoryStream();
            reader.CopyTo(oldBytes);
            Assert.Equal(before, oldBytes.ToArray());
        }
        scenario.Retry();
        Assert.True(scenario.ReloadHasSecond());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    private static Scenario CreateScenario(string kind, string path)
    {
        if (kind == "blacklist")
        {
            var store = new SectionBlacklistStore(path);
            void Second() => store.RecordPermanentFailure("site", "Site", "second", "/second", "denied");
            return new(() => store.RecordPermanentFailure("site", "Site", "first", "/first", "denied"),
                Second, Second, () => store.IsBlacklisted("site", "second"), () =>
                {
                    var loaded = new SectionBlacklistStore(path);
                    loaded.Load();
                    return loaded.IsBlacklisted("site", "second");
                });
        }
        if (kind == "abandon")
        {
            var store = new ExtractAbandonStore(path);
            void Second() => store.Record("second.rar", 2, 200, "incomplete");
            return new(() => store.Record("first.rar", 1, 100, "incomplete"), Second, Second,
                () => store.ShouldSkip("second.rar", 2, 200), () =>
                {
                    var loaded = new ExtractAbandonStore(path);
                    loaded.Load();
                    return loaded.ShouldSkip("second.rar", 2, 200);
                });
        }
        var today = new DateTime(2026, 10, 16, 12, 0, 0);
        var usage = new NetworkUsageStore(path, () => today);
        return new(() => { usage.Add("first", 100); usage.Flush(); },
            () => { usage.Add("second", 200); usage.Flush(); }, usage.Flush,
            () => usage.UsedThisPeriod("second", AllowancePeriod.Daily) == 200,
            () => new NetworkUsageStore(path, () => today).UsedThisPeriod("second", AllowancePeriod.Daily) == 200);
    }

    private sealed record Scenario(Action First, Action Second, Action Retry, Func<bool> MemoryHasSecond, Func<bool> ReloadHasSecond);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
