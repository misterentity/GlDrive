using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Library;
using Xunit;

namespace GlDrive.Tests;

public sealed class LibraryImportOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-import-owner-" + Guid.NewGuid().ToString("N"));
    private string QueuePath => Path.Combine(_root, "downloads-fixture.json");
    private string Source => Path.Combine(_root, "staging", "release", "fixture.mkv");

    public LibraryImportOwnershipTests() => Directory.CreateDirectory(_root);

    private static DownloadItem Item(string path, DownloadStatus status) => new() { LocalPath = path, Status = status };
    private void Persist(params DownloadItem[] items) => File.WriteAllText(QueuePath, JsonSerializer.Serialize(items));
    private LibraryImportOwnership Guard() => new(_root, () => []);

    [Theory]
    [InlineData(DownloadStatus.Queued)]
    [InlineData(DownloadStatus.Downloading)]
    [InlineData(DownloadStatus.Extracting)]
    [InlineData(DownloadStatus.Paused)]
    public void Every_active_persisted_status_blocks_a_child_file(DownloadStatus status)
    {
        Persist(Item(Path.GetDirectoryName(Source)!, status));
        Assert.Throws<IOException>(() => Guard().EnsureUnowned(Source));
    }

    [Theory]
    [InlineData(DownloadStatus.Completed)]
    [InlineData(DownloadStatus.Failed)]
    [InlineData(DownloadStatus.Cancelled)]
    public void Terminal_queue_entries_do_not_block_import(DownloadStatus status)
    {
        Persist(Item(Source, status));
        Guard().EnsureUnowned(Source);
    }

    [Fact]
    public void Live_exact_file_owner_blocks_even_without_a_persisted_queue()
    {
        var guard = new LibraryImportOwnership(_root, () => [(QueuePath, new[] { Item(Source, DownloadStatus.Downloading) })]);
        Assert.Throws<IOException>(() => guard.EnsureUnowned(Source));
    }

    [Fact]
    public void Prefix_name_is_not_directory_ownership()
    {
        Persist(Item(Path.GetDirectoryName(Source)! + "-other", DownloadStatus.Downloading));
        Guard().EnsureUnowned(Source);
    }

    [Fact]
    public void Newest_live_snapshot_supersedes_stale_persisted_and_old_live_entries()
    {
        Persist(Item(Source, DownloadStatus.Queued));
        var guard = new LibraryImportOwnership(_root, () =>
        [
            (QueuePath, new[] { Item(Source, DownloadStatus.Completed) }),
            (QueuePath, new[] { Item(Source, DownloadStatus.Downloading) })
        ]);
        guard.EnsureUnowned(Source);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"status\":999}]")]
    [InlineData("[{\"status\":0,\"localPath\":\"\"}]")]
    public void Uncertain_persisted_ownership_refuses_only_import_without_rewriting_queue(string invalid)
    {
        File.WriteAllText(QueuePath, invalid);
        Assert.Throws<IOException>(() => Guard().EnsureUnowned(Source));
        Assert.Equal(invalid, File.ReadAllText(QueuePath));
        Assert.False(File.Exists(QueuePath + ".corrupt"));
    }

    [Fact]
    public void Corrupt_persisted_data_is_not_hidden_by_empty_live_snapshot()
    {
        File.WriteAllText(QueuePath, "{");
        var guard = new LibraryImportOwnership(_root, () => [(QueuePath, Array.Empty<DownloadItem>())]);
        Assert.Throws<IOException>(() => guard.EnsureUnowned(Source));
    }

    [Fact]
    public void Locked_persisted_queue_is_uncertain_and_refuses_import()
    {
        Persist();
        using var locked = new FileStream(QueuePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => Guard().EnsureUnowned(Source));
    }

    [Fact]
    public void Rechecks_persisted_changes_without_a_cache()
    {
        var guard = Guard();
        Persist(Item(Source, DownloadStatus.Completed));
        guard.EnsureUnowned(Source);
        Persist(Item(Source, DownloadStatus.Paused));
        Assert.Throws<IOException>(() => guard.EnsureUnowned(Source));
    }

    [Fact]
    public async Task Dry_run_never_calls_ownership_probe()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        File.WriteAllText(Source, "synthetic");
        var config = new LibraryConfig { Enabled = true, DryRun = true,
            StagingRoot = Path.Combine(_root, "staging"), MovieRoot = Path.Combine(_root, "movies") };
        var plan = LibraryPlanner.Plan(config, Source, "Synthetic.Film.2024.1080p.WEB-TEST");
        var result = await new SafeLibraryImporter(_ => throw new IOException("uncertain ownership")).ImportAsync(plan, config);
        Assert.Equal("dry-run", result.Status);
        Assert.True(File.Exists(Source));
    }

    [Fact]
    public async Task Ownership_discovered_after_copy_retains_source_and_verified_destination()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        File.WriteAllText(Source, "synthetic");
        var config = new LibraryConfig { Enabled = true, DryRun = false,
            StagingRoot = Path.Combine(_root, "staging"), MovieRoot = Path.Combine(_root, "movies") };
        var plan = LibraryPlanner.Plan(config, Source, "Synthetic.Film.2024.1080p.WEB-TEST");
        var guard = Guard();
        var importer = new SafeLibraryImporter(guard.EnsureUnowned) { ForceCopyForTests = true,
            Checkpoint = step => { if (step == "verified") Persist(Item(Source, DownloadStatus.Paused)); } };
        await Assert.ThrowsAsync<IOException>(() => importer.ImportAsync(plan, config));
        Assert.Equal("synthetic", File.ReadAllText(Source));
        Assert.Equal("synthetic", File.ReadAllText(plan.DestinationPath));
    }

    [Fact]
    public async Task Ownership_discovered_before_atomic_rename_keeps_staged_source()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        File.WriteAllText(Source, "synthetic");
        var config = new LibraryConfig { Enabled = true, DryRun = false,
            StagingRoot = Path.Combine(_root, "staging"), MovieRoot = Path.Combine(_root, "movies") };
        var plan = LibraryPlanner.Plan(config, Source, "Synthetic.Film.2024.1080p.WEB-TEST");
        var guard = Guard();
        var importer = new SafeLibraryImporter(guard.EnsureUnowned)
        { Checkpoint = step => { if (step == "before-rename") Persist(Item(Source, DownloadStatus.Queued)); } };
        await Assert.ThrowsAsync<IOException>(() => importer.ImportAsync(plan, config));
        Assert.True(File.Exists(Source));
        Assert.False(File.Exists(plan.DestinationPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
