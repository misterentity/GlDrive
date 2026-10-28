using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using GlDrive.Config;
using Xunit;

namespace GlDrive.Tests;

public sealed class ConfigHistoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "config-history-test-" + Guid.NewGuid().ToString("N"));
    private static readonly ConfigWriteContext Actor = new("settings-ui", "audit-fixture");
    private string FilePath => Path.Combine(_root, "config-history.json");
    public ConfigHistoryStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private ConfigHistoryStore Store(Action<string, string>? write = null, Func<string, string>? protect = null) => new(_root,
        protect ?? (text => Convert.ToBase64String(Encoding.UTF8.GetBytes(text))),
        text => Encoding.UTF8.GetString(Convert.FromBase64String(text)), write ?? File.WriteAllText);

    [Fact]
    public void Corruption_is_preserved_and_new_tracking_starts_empty()
    {
        File.WriteAllText(FilePath, "torn JSON evidence");
        var store = Store();
        Assert.Empty(store.Read());
        Assert.Equal("torn JSON evidence", File.ReadAllText(FilePath + ".corrupt"));
        store.Record(null, "{\"theme\":\"Dark\"}", Actor, 2);
        Assert.Equal(1, Assert.Single(store.Read()).Number);
        Assert.Single(Store().Read());
    }

    [Fact]
    public void Failed_writes_keep_versions_in_memory_and_next_success_preserves_them()
    {
        var fail = true;
        var store = Store((path, text) => { if (fail) throw new IOException("synthetic unavailable disk"); File.WriteAllText(path, text); });
        store.Record(null, "{\"value\":1}", Actor, 10);
        Assert.Equal(1, Assert.Single(store.Read()).Number);
        Assert.False(File.Exists(FilePath));
        fail = false;
        store.Record("{\"value\":1}", "{\"value\":2}", Actor, 10);
        Assert.Equal(new long[] { 1, 2 }, Store().Read().Select(v => v.Number));
    }

    [Fact]
    public void Oldest_eviction_does_not_reset_numbers_across_restart()
    {
        var store = Store();
        for (var value = 1; value <= 3; value++)
            store.Record(null, $"{{\"value\":{value}}}", Actor, 2);
        Assert.Equal(new long[] { 2, 3 }, store.Read().Select(v => v.Number));
        var restored = Store();
        restored.Record("{\"value\":3}", "{\"value\":4}", Actor, 2);
        Assert.Equal(new long[] { 3, 4 }, restored.Read().Select(v => v.Number));
        Assert.Throws<ArgumentException>(() => restored.RestoreSnapshot(2));
        Assert.Equal(4, restored.RestoreSnapshot(4)["value"]!.GetValue<int>());
    }

    [Fact]
    public void Empty_diff_skips_version_protection_and_persistence()
    {
        var writes = 0;
        var protects = 0;
        var store = Store((_, _) => writes++, text => { protects++; return text; });
        const string config = "{\"value\":1}";
        store.Record(null, config, Actor, 10);
        store.Record(config, config, new("mcp:config_patch"), 10);
        store.Record(config, "{ \"value\": 1 }", Actor, 10);
        Assert.Equal(1, Assert.Single(store.Read()).Number);
        Assert.Equal(1, writes);
        Assert.Equal(1, protects);
        store.Record(config, "{\"value\":2}", Actor, 10);
        Assert.Equal(new long[] { 1, 2 }, store.Read().Select(r => r.Number));
    }

    [Fact]
    public void Secret_leaves_nested_subtrees_and_echoes_are_masked_in_public_history()
    {
        var store = Store();
        const string secret = "synthetic-secret-7654321";
        var config = new JsonObject
        {
            ["controlApi"] = new JsonObject { ["token"] = secret },
            ["channels"] = new JsonArray(new JsonObject { ["name"] = "#fixture", ["key"] = secret }),
            ["note"] = "echo " + secret
        }.ToJsonString();
        store.Record(null, config, Actor, 10);
        var row = Assert.Single(store.Read());
        Assert.Equal(Actor.Actor, row.Actor);
        Assert.Equal(Actor.AuditRef, row.AuditRef);
        Assert.Null(row.ProtectedSnapshot);
        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(row));
        Assert.DoesNotContain(secret, File.ReadAllText(FilePath));
        Assert.Equal(secret, store.RestoreSnapshot(row.Number)["controlApi"]!["token"]!.GetValue<string>());
    }

    [Fact]
    public void Snapshot_protection_failure_does_not_lose_attributed_diff()
    {
        var store = Store(protect: _ => throw new InvalidOperationException("synthetic protector failure"));
        store.Record("{\"value\":1}", "{\"value\":2}", Actor, 10);
        var row = Assert.Single(store.Read());
        Assert.Equal("2", Assert.Single(row.Diff).After);
        Assert.Equal(Actor.Actor, row.Actor);
        Assert.Throws<InvalidOperationException>(() => store.RestoreSnapshot(row.Number));
    }

    [Fact]
    public void Returned_diff_collections_and_restored_snapshots_are_detached()
    {
        var store = Store();
        store.Record(null, "{\"value\":1}", Actor, 10);
        Assert.Single(store.Read()).Diff.Clear();
        Assert.NotEmpty(Assert.Single(store.Read()).Diff);
        store.RestoreSnapshot(1)["value"] = 99;
        Assert.Equal(1, store.RestoreSnapshot(1)["value"]!.GetValue<int>());
    }
}
