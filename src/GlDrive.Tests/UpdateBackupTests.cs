using System.IO;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public sealed class UpdateBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gldrive-backup-{Guid.NewGuid():N}");

    public UpdateBackupTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Locked_legacy_backup_does_not_prevent_the_next_update()
    {
        var current = Path.Combine(_root, "runtime.dll");
        var legacy = current + ".old";
        File.WriteAllText(legacy, "previous release");
        File.WriteAllText(current, "installed release");
        using var locked = new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Reproduce the old updater's failure without loading a real application DLL.
        var failure = Record.Exception(() => File.Move(current, legacy, overwrite: true));
        Assert.True(failure is IOException or UnauthorizedAccessException,
            $"Replacing a locked legacy backup should fail; got {failure?.GetType().Name ?? "no exception"}.");
        var backups = new List<(string Original, string Backup)>();
        Assert.Equal(1, UpdateChecker.BackUpInstalledFiles(_root, backups));

        var backup = Assert.Single(backups);
        Assert.Equal(current, backup.Original);
        Assert.NotEqual(legacy, backup.Backup);
        Assert.EndsWith(".old", backup.Backup);
        Assert.Equal("installed release", File.ReadAllText(backup.Backup));
        Assert.Equal("previous release", File.ReadAllText(legacy));
        Assert.False(File.Exists(current));
    }

    [Fact]
    public void Successive_updates_preserve_locked_generations_and_rollback_only_the_current_attempt()
    {
        var mcp = Directory.CreateDirectory(Path.Combine(_root, "mcp")).FullName;
        var runtime = Path.Combine(mcp, "runtime.dll");
        var main = Path.Combine(_root, "GlDrive.exe");
        File.WriteAllText(runtime, "runtime v1");
        File.WriteAllText(main, "app v1");
        // A running process may permit rename of its current image, retaining the old content.
        using var running = new FileStream(runtime, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        var first = new List<(string Original, string Backup)>();
        Assert.Equal(2, UpdateChecker.BackUpInstalledFiles(_root, first));
        File.WriteAllText(runtime, "runtime v2");
        File.WriteAllText(main, "app v2");
        var firstRuntime = first.Single(b => b.Original == runtime).Backup;
        using var retained = new FileStream(firstRuntime, FileMode.Open, FileAccess.Read, FileShare.Read);

        var second = new List<(string Original, string Backup)>();
        Assert.Equal(2, UpdateChecker.BackUpInstalledFiles(_root, second));
        Assert.Equal(2, second.Count);
        Assert.DoesNotContain(second, b => first.Any(old => old.Backup == b.Backup));
        Assert.Equal("runtime v2", File.ReadAllText(second.Single(b => b.Original == runtime).Backup));
        Assert.Equal("runtime v1", File.ReadAllText(firstRuntime));

        // One payload was copied; a later payload copy fails. Roll back through the same
        // production helper, ensuring neither the oldest generation nor a partial v3 wins.
        var source = Path.Combine(_root, "payload.tmp");
        File.WriteAllText(source, "runtime v3");
        File.Copy(source, runtime);
        var copied = new List<string> { runtime, main };
        Assert.ThrowsAny<IOException>(() => File.Copy(Path.Combine(_root, "missing.tmp"), main));
        var errors = new List<string>();
        UpdateChecker.RollBackUpdateFiles(copied, second, errors.Add);

        Assert.Empty(errors);
        Assert.Equal("runtime v2", File.ReadAllText(runtime));
        Assert.Equal("app v2", File.ReadAllText(main));
        Assert.Equal("runtime v1", File.ReadAllText(firstRuntime));
        Assert.All(second, b => Assert.False(File.Exists(b.Backup)));
        Assert.All(first, b => Assert.True(File.Exists(b.Backup)));
    }

    [Fact]
    public void Cleanup_recognizes_unique_backups_and_retries_a_retained_generation()
    {
        var current = Path.Combine(_root, "runtime.dll");
        File.WriteAllText(current, "v1");
        var first = new List<(string Original, string Backup)>();
        UpdateChecker.BackUpInstalledFiles(_root, first);
        File.WriteAllText(current, "v2");
        var second = new List<(string Original, string Backup)>();
        UpdateChecker.BackUpInstalledFiles(_root, second);
        File.WriteAllText(current, "v3");

        using (var retained = new FileStream(first[0].Backup, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal((1, 1), UpdateChecker.DeleteOldUpdateFiles(_root));
            Assert.True(File.Exists(first[0].Backup));
            Assert.False(File.Exists(second[0].Backup));
            Assert.Equal("v3", File.ReadAllText(current));
        }

        Assert.Equal((1, 0), UpdateChecker.DeleteOldUpdateFiles(_root));
        Assert.Equal("v3", File.ReadAllText(current));
    }

    [Fact]
    public void Locked_current_file_still_aborts_without_overwriting_or_recording_a_backup()
    {
        var current = Path.Combine(_root, "runtime.dll");
        File.WriteAllText(current, "installed");
        using var locked = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read);
        var backups = new List<(string Original, string Backup)>();

        Assert.Throws<IOException>(() => UpdateChecker.BackUpInstalledFiles(_root, backups));

        Assert.Empty(backups);
        Assert.Equal("installed", File.ReadAllText(current));
        Assert.Empty(Directory.GetFiles(_root, "*.old"));
    }

    [Fact]
    public void Install_log_is_mirrored_to_both_paths()
    {
        var temp = Path.Combine(_root, "temp", "gldrive-update.log");
        var shared = Path.Combine(_root, "shared", "update-install.log");

        UpdateChecker.WriteUpdateLog("Update FAILED: diagnostic detail", temp, shared);

        Assert.Equal(File.ReadAllText(temp), File.ReadAllText(shared));
        Assert.Contains("Update FAILED: diagnostic detail", File.ReadAllText(shared));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unwritable_log_sink_does_not_suppress_the_other(bool tempUnavailable)
    {
        var blocked = Directory.CreateDirectory(Path.Combine(_root, "directory-not-file")).FullName;
        var available = Path.Combine(_root, "available.log");

        UpdateChecker.WriteUpdateLog("rollback detail",
            tempUnavailable ? blocked : available, tempUnavailable ? available : blocked);

        Assert.Contains("rollback detail", File.ReadAllText(available));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
