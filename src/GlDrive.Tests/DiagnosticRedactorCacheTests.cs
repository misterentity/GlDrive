using System.IO;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Logging;
using Xunit;

namespace GlDrive.Tests;

[Collection("Flight recorder logger")]
public sealed class DiagnosticRedactorCacheTests
{
    [Fact]
    public void ReusesInventoryAcrossBusySecondsAndRefreshesOnChange()
    {
        var now = DateTimeOffset.UtcNow;
        var revision = 0L;
        var builds = 0;
        var secret = "original-secret";
        var cache = new DiagnosticRedactorCache(() => { builds++; return DiagnosticTextRedactor.Create([secret]); }, () => revision, () => now);
        var held = cache.Get();
        for (var second = 0; second < 59; second++)
        {
            Assert.DoesNotContain(secret, cache.Get()(secret));
            now = now.AddSeconds(1);
        }
        Assert.Equal(1, builds);
        secret = "replacement-secret"; revision++;
        Assert.Equal("[redaction unavailable]", held(secret));
        Assert.DoesNotContain(secret, cache.Get()(secret));
        Assert.Equal(2, builds);
        now = now.AddMinutes(1);
        cache.Get(); Assert.Equal(3, builds);
    }

    [Fact]
    public void TransientRefreshRetainsGoodInventoryButChangedInputsFailClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var revision = 0L;
        var fail = false;
        var cache = new DiagnosticRedactorCache(() => fail ? throw new IOException() : DiagnosticTextRedactor.Create(["known-secret"]), () => revision, () => now);
        var good = cache.Get();
        fail = true; now = now.AddMinutes(1);
        Assert.Same(good, cache.Get());
        Assert.DoesNotContain("known-secret", cache.Get()("known-secret"));
        revision++;
        Assert.Same(DiagnosticRedactorCache.Unavailable, cache.Get());
        Assert.Equal("[redaction unavailable]", good("new-secret"));
        fail = false; now = now.AddMinutes(1);
        Assert.NotSame(DiagnosticRedactorCache.Unavailable, cache.Get());
    }

    [Fact]
    public void HeldDelegateFailsClosedWhileInputIsChanging()
    {
        var cache = new DiagnosticRedactorCache(() => value => value);
        var held = cache.Get();
        using (DiagnosticRedactorCache.BeginInputChange())
        {
            Assert.Same(DiagnosticRedactorCache.Unavailable, cache.Get());
            Assert.Equal("[redaction unavailable]", held("new-value"));
        }
        Assert.Equal("[redaction unavailable]", held("new-value"));
        Assert.Equal("ordinary evidence", cache.Get()("ordinary evidence"));
    }

    [Fact]
    public void InventoryExpansionFailureCannotReplaceTheLastGoodRedactor()
    {
        var now = DateTimeOffset.UtcNow;
        IEnumerable<string> values = ["known-secret"];
        var cache = new DiagnosticRedactorCache(() => DiagnosticTextRedactor.Create(values, throwOnInventoryFailure: true), now: () => now);
        var good = cache.Get();
        values = Enumerable.Range(0, 4097).Select(i => "synthetic-variant-" + i);
        now = now.AddMinutes(1);
        Assert.Same(good, cache.Get());
        Assert.DoesNotContain("known-secret", cache.Get()("known-secret"));
        DiagnosticRedactorCache.Invalidate();
        Assert.Same(DiagnosticRedactorCache.Unavailable, cache.Get());
    }

    [Fact]
    public void ConfigCredentialAndFishMutationPathsInvalidateTheInventory()
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-inventory-" + Guid.NewGuid().ToString("N"));
        var previous = ConfigManager.AppDataPathOverride;
        Directory.CreateDirectory(root);
        try
        {
            ConfigManager.AppDataPathOverride = root;
            void Changed(Action mutation)
            {
                var before = DiagnosticRedactorCache.InventoryRevision;
                mutation();
                Assert.True(DiagnosticRedactorCache.InventoryRevision > before);
            }
            Changed(() => ConfigManager.Save(new AppConfig()));
            // Test-process isolation disables all real credential access.
            Assert.True(CredentialStore.AccessDisabled);
            Changed(() => CredentialStore.SavePassword("synthetic.invalid", 21, "test", "value"));
            Changed(() => CredentialStore.SaveProxyPassword("synthetic.invalid", 21, "test", "value"));
            Changed(() => CredentialStore.SaveIrcPassword("synthetic.invalid", 6697, "test", "value"));
            Changed(() => CredentialStore.SaveSshPassword("synthetic.invalid", 22, "test", "value"));
            Changed(() => CredentialStore.SaveApiKey("synthetic", "value"));
            Changed(() => CredentialStore.DeletePassword("synthetic.invalid", 21, "test"));
            Changed(() => CredentialStore.DeleteProxyPassword("synthetic.invalid", 21, "test"));
            Changed(() => CredentialStore.DeleteIrcPassword("synthetic.invalid", 6697, "test"));
            Changed(() => CredentialStore.DeleteApiKey("synthetic"));
            var fish = new FishKeyStore("synthetic");
            Changed(() => fish.SetKey("target", "synthetic-key"));
            Changed(() => fish.SetKeyWithAlt("target", "synthetic-key", "synthetic-alt", FishMode.CBC));
            Changed(() => fish.SetDh1080Keys("another", "synthetic-key", "synthetic-alt", "aabb", FishMode.CBC));
            Changed(() => fish.RemoveKey("target"));
        }
        finally
        {
            ConfigManager.AppDataPathOverride = previous;
            Directory.Delete(root, true);
        }
    }
}
