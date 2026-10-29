using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public sealed class FishKeyRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-fish-recovery-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "appsettings.json");
    public FishKeyRecoveryTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(ConfigPath, "{}");
    }
    private string StorePath(string id) => Path.Combine(_root, "fish-keys-" + id + ".json");
    private static FishKeyStore Open(string path) => new(path, bytes => bytes.ToArray(), _ => throw new CryptographicException());

    [Fact]
    public void Sweep_repairs_orphan_and_disabled_stores_and_promotes_pending_only_before_inventories_retry()
    {
        var disabled = StorePath("disabled");
        var orphan = StorePath("orphan");
        var pendingOnly = StorePath("pending-only");
        File.WriteAllBytes(disabled, [1, 2, 3]);
        File.WriteAllBytes(orphan, [4, 5, 6]);
        File.WriteAllText(disabled + ".pending", """{"#one":{"key":"synthetic-disabled-key"}}""");
        File.WriteAllText(pendingOnly + ".pending", """{"#two":{"key":"synthetic-pending-key"}}""");
        Assert.ThrowsAny<Exception>(() => OutboundSecretInventory.Collect(ConfigPath, _root, () => []));
        Assert.ThrowsAny<Exception>(() => OutboundSecretInventory.CollectForDiagnostics(ConfigPath, _root, () => []));

        var snapshot = FishKeyRecovery.Sweep(_root, Open);

        Assert.Equal(3, snapshot.StoresExamined);
        Assert.Equal(2, snapshot.StoresNeedingReview);
        Assert.False(snapshot.Incomplete);
        Assert.False(File.Exists(disabled + ".pending"));
        Assert.False(File.Exists(pendingOnly + ".pending"));
        Assert.Equal("synthetic-disabled-key", Open(disabled).GetKey("#one")!.Key);
        Assert.Equal("synthetic-pending-key", Open(pendingOnly).GetKey("#two")!.Key);
        Assert.Empty(Open(orphan).GetAllKeys());
        foreach (var values in new[] { OutboundSecretInventory.Collect(ConfigPath, _root, () => []),
                     OutboundSecretInventory.CollectForDiagnostics(ConfigPath, _root, () => []) })
            Assert.Equal(new[] { "synthetic-disabled-key", "synthetic-pending-key" }, values.Order());
        var quarantined = Directory.GetFiles(_root, "*.quarantined");
        Assert.Equal(2, quarantined.Length);
        Assert.All(quarantined, path => Assert.True(new FileInfo(path).GetAccessControl().AreAccessRulesProtected));
    }

    [Fact]
    public void One_store_failure_does_not_stop_recovery_of_other_stores_or_rewrite_healthy_keys()
    {
        var failed = StorePath("unavailable");
        var healthy = StorePath("healthy");
        var damaged = StorePath("damaged");
        const string valid = """{"#healthy":{"key":"synthetic-healthy-key","setAt":"2026-01-01T12:00:00Z"}}""";
        File.WriteAllText(failed, valid);
        File.WriteAllText(healthy, valid);
        File.WriteAllBytes(damaged, [7, 8, 9]);
        var snapshot = FishKeyRecovery.Sweep(_root, path => path == failed ? throw new IOException("Synthetic unavailable store") : Open(path));
        Assert.True(snapshot.Incomplete);
        Assert.True(snapshot.NeedsReview);
        Assert.Equal(2, snapshot.StoresExamined);
        Assert.Equal(1, snapshot.StoresNeedingReview);
        Assert.Equal(valid, File.ReadAllText(healthy));
        Assert.True(Open(damaged).PersistenceHealthy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Passive_recovery_snapshot_produces_problem_for_orphan_or_disabled_server(bool configured)
    {
        File.WriteAllBytes(StorePath("retired"), [1, 2, 3]);
        var snapshot = FishKeyRecovery.Sweep(_root, Open);
        var config = new AppConfig();
        if (configured) config.Servers.Add(new() { Id = "retired", Enabled = false });
        var problems = HealthDoctor.Evaluate(new(config, [], [], [], [], LocalFishRecoveryRequired: snapshot.NeedsReview), DateTimeOffset.UtcNow);
        var problem = Assert.Single(problems.Problems);
        Assert.StartsWith("fish-storage-recovery:", problem.Id);
        Assert.Contains("Recovery Center", problem.Remedy);
        Assert.False(problem.FixAvailable);
    }

    [Fact]
    public async Task Runtime_writes_wait_for_sweep_promotion_then_save_authoritative_keys_to_repaired_primary()
    {
        var path = StorePath("concurrent");
        File.WriteAllBytes(path, [1, 2, 3]);
        FishKeyStore runtime;
        // Reading/copying is allowed, but the external handle temporarily prevents quarantine rename.
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            runtime = Open(path);
            Assert.False(runtime.PersistenceHealthy);
            runtime.SetKey("#keep", "synthetic-kept-key");
            runtime.SetKey("#remove", "synthetic-removed-key");
        }
        using var promote = new ManualResetEventSlim();
        var staging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweep = Task.Run(() => FishKeyRecovery.Sweep(_root, candidate => new FishKeyStore(candidate, bytes =>
        {
            staging.TrySetResult();
            if (!promote.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic promotion barrier timed out");
            return bytes.ToArray();
        }, _ => throw new CryptographicException())));
        Task update = Task.CompletedTask;
        try
        {
            await staging.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var updating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            update = Task.Run(() =>
            {
                updating.SetResult();
                runtime.SetKey("#late", "synthetic-late-key");
                runtime.RemoveKey("#remove");
            });
            await updating.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(100);
            Assert.False(update.IsCompleted); // The primary path lock covers pending writes too.
        }
        finally { promote.Set(); }
        await Task.WhenAll(sweep, update).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(runtime.PersistenceHealthy);
        Assert.False(File.Exists(path + ".pending"));
        var restarted = Open(path);
        Assert.Equal("synthetic-kept-key", restarted.GetKey("#keep")!.Key);
        Assert.Equal("synthetic-late-key", restarted.GetKey("#late")!.Key);
        Assert.Null(restarted.GetKey("#remove"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verified_import_keeps_chosen_primary_even_if_stale_pending_cleanup_fails(bool lockPending)
    {
        var path = StorePath("import");
        var store = Open(path);
        store.SetKey("#key", "synthetic-current-key");
        File.WriteAllText(path + ".pending", """{"#key":{"key":"synthetic-stale-key"}}""");
        File.SetLastWriteTimeUtc(path + ".pending", DateTime.UtcNow.AddHours(1));
        using var locked = lockPending ? new FileStream(path + ".pending", FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        Assert.True(store.TryImportVerified("#key", new() { Key = "synthetic-chosen-key" }, replaceExisting: true));
        Assert.Equal(lockPending, File.Exists(path + ".pending"));
        Assert.Equal("synthetic-chosen-key", Open(path).GetKey("#key")!.Key);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
