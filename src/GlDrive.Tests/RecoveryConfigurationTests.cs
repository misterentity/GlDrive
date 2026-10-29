using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Services.Automation;
using GlDrive.Services.Recovery;
using GlDrive.Util;
using Xunit;

namespace GlDrive.Tests;

public sealed class RecoveryConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-recovery-config-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "appsettings.json");
    private static readonly byte[] Damaged = Encoding.UTF8.GetBytes("{\"controlApi\":{\"token\":\"synthetic-original\"}, broken");
    public RecoveryConfigurationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private void Save(AppConfig candidate) => SecureFile.WriteAllTextRestricted(ConfigPath,
        JsonSerializer.Serialize(candidate, ConfigurationAutomation.Json));
    private static JsonArray Edit() => JsonNode.Parse("[{\"op\":\"replace\",\"path\":\"/logging/level\",\"value\":\"Debug\"}]")!.AsArray();

    [Fact]
    public void Explicit_offline_repair_preserves_exact_source_before_attributed_save()
    {
        File.WriteAllBytes(ConfigPath, Damaged);
        var saves = 0;
        var repair = RecoveryConfigurationFactory.Create(new AppConfig(), true, true, ConfigPath, candidate =>
        {
            var preserved = Assert.Single(Directory.GetFiles(_root, "*.pre-recovery"));
            Assert.Equal(Damaged, File.ReadAllBytes(preserved));
            Assert.Equal(Damaged, File.ReadAllBytes(ConfigPath));
            Assert.Equal("local:portable-backup-import", ConfigWriteScope.Current.Actor);
            Save(candidate); saves++;
        });
        Assert.True(repair.NeedsExplicitRepair);
        Assert.Empty(Directory.GetFiles(_root, "*.pre-recovery"));
        using (ConfigWriteScope.Begin("local:portable-backup-import")) repair.Configuration.Apply(Edit());
        Assert.Equal(1, saves);
        Assert.Equal("Debug", repair.Configuration.CurrentSnapshot()["logging"]!["level"]!.GetValue<string>());
    }

    [Fact]
    public void Unselected_replacement_refuses_save_without_copying_or_losing_source()
    {
        File.WriteAllBytes(ConfigPath, Damaged);
        var repair = RecoveryConfigurationFactory.Create(new AppConfig(), true, false, ConfigPath, _ => Assert.Fail("Must not save"));
        Assert.True(repair.NeedsExplicitRepair);
        Assert.Throws<InvalidOperationException>(() => repair.Configuration.Apply(Edit()));
        Assert.Equal(Damaged, File.ReadAllBytes(ConfigPath));
        Assert.Empty(Directory.GetFiles(_root, "*.pre-recovery"));
    }

    [Fact]
    public void Normal_runtime_never_uses_fallback_for_corrupt_settings()
    {
        File.WriteAllBytes(ConfigPath, Damaged);
        var normal = RecoveryConfigurationFactory.Create(new AppConfig(), false, true, ConfigPath, _ => Assert.Fail("Must not save"));
        Assert.False(normal.NeedsExplicitRepair);
        Assert.Throws<JsonException>(() => normal.Configuration.CurrentSnapshot());
        Assert.Equal(Damaged, File.ReadAllBytes(ConfigPath));
    }

    [Fact]
    public void Intervening_edit_invalidates_repair_even_if_new_source_is_also_corrupt()
    {
        File.WriteAllBytes(ConfigPath, Damaged);
        var repair = RecoveryConfigurationFactory.Create(new AppConfig(), true, true, ConfigPath, _ => Assert.Fail("Must not save"));
        File.WriteAllText(ConfigPath, "new damaged source");
        Assert.Throws<InvalidOperationException>(() => repair.Configuration.Apply(Edit()));
        Assert.Equal("new damaged source", File.ReadAllText(ConfigPath));
        Assert.Empty(Directory.GetFiles(_root, "*.pre-recovery"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Preservation_failure_never_reaches_the_settings_save(bool throwOnWrite)
    {
        File.WriteAllBytes(ConfigPath, Damaged);
        var repair = RecoveryConfigurationFactory.Create(new AppConfig(), true, true, ConfigPath, _ => Assert.Fail("Must not save"),
            (path, _) => { if (throwOnWrite) throw new IOException("Synthetic write failure"); File.WriteAllText(path, "wrong bytes"); });
        Assert.Throws<IOException>(() => repair.Configuration.Apply(Edit()));
        Assert.Equal(Damaged, File.ReadAllBytes(ConfigPath));
    }

    [Fact]
    public void Failed_settings_save_keeps_source_and_verified_recovery_copy_for_retry()
    {
        File.WriteAllBytes(ConfigPath, Damaged);
        var fail = true;
        var repair = RecoveryConfigurationFactory.Create(new AppConfig(), true, true, ConfigPath,
            candidate => { if (fail) throw new IOException("Synthetic save failure"); Save(candidate); });
        Assert.Throws<IOException>(() => repair.Configuration.Apply(Edit()));
        Assert.Equal(Damaged, File.ReadAllBytes(ConfigPath));
        var first = Assert.Single(Directory.GetFiles(_root, "*.pre-recovery"));
        fail = false;
        repair.Configuration.Apply(Edit());
        Assert.Equal(2, Directory.GetFiles(_root, "*.pre-recovery").Length);
        Assert.Equal(Damaged, File.ReadAllBytes(first));
    }

    [Fact]
    public void Valid_unicode_file_and_malformed_optional_block_do_not_require_repair()
    {
        File.WriteAllText(ConfigPath, "{\"dataStewardship\":{\"copyConfigSecrets\":\"draft\"}}", Encoding.Unicode);
        var normal = RecoveryConfigurationFactory.Create(new AppConfig(), true, false, ConfigPath, Save);
        Assert.False(normal.NeedsExplicitRepair);
        normal.Configuration.Apply(Edit());
        Assert.Empty(Directory.GetFiles(_root, "*.pre-recovery"));
    }
}
