using System.IO;
using System.IO.Hashing;
using System.Text;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class SfvRepairIntegrationTests
{
    [Fact]
    public async Task Repair_refetches_only_bad_payload_and_clears_extraction_hold_after_verification()
    {
        await using var h = new Harness();
        h.AddPayload("good.rar", "sound", "sound");
        h.AddPayload("bad.r00", "bad01", "right");
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Queued, h.Item.Status);
        Assert.True(h.Item.SfvIntegrityHold);
        Assert.True(DownloadExtractionGuard.Blocks(h.Item, h.PathFor("good.rar")));
        Assert.Equal(1, h.Record("bad.r00").Attempts);
        Assert.Equal(0, h.Record("good.rar").Attempts);
        Assert.Equal(Bytes("sound"), File.ReadAllBytes(h.PathFor("good.rar")));

        h.Source.Files["bad.r00"] = Bytes("right");
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.Equal(1, h.Source.Fetches["good.rar"]);
        Assert.Equal(2, h.Source.Fetches["bad.r00"]);
        Assert.False(h.Item.SfvIntegrityHold);
        Assert.False(DownloadExtractionGuard.Blocks(h.Item, h.PathFor("good.rar")));
        Assert.Equal(Bytes("right"), File.ReadAllBytes(h.PathFor("bad.r00")));
    }

    [Fact]
    public async Task Pending_repair_and_budget_survive_store_and_manager_restart()
    {
        await using var h = new Harness();
        h.AddPayload("disc.rar", "bad01", "right");
        await h.RunOnePass();
        Assert.Equal(1, h.Record("disc.rar").Attempts);
        var owner = h.Item.SfvRepairOwner;
        Assert.False(string.IsNullOrWhiteSpace(owner));
        var record = h.Record("disc.rar");
        record.LocalPath = Path.Combine(Path.GetDirectoryName(record.LocalPath)!, ".", "disc.rar").ToUpperInvariant();
        h.Store.Save();

        await h.Reload();
        Assert.Equal(DownloadStatus.Queued, h.Item.Status);
        Assert.Equal(owner, h.Item.SfvRepairOwner);
        Assert.Equal(1, h.Record("disc.rar").Attempts);
        Assert.True(h.Item.SfvIntegrityHold);
        Assert.True(DownloadExtractionGuard.Blocks(h.Item, h.PathFor("disc.rar")));
        h.Source.Files["disc.rar"] = Bytes("right");
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.Equal(2, h.Source.Fetches["disc.rar"]);
        Assert.Equal(1, h.Record("disc.rar").Attempts);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("late-sfv")]
    [InlineData("late-proof")]
    public async Task Pending_repair_fetches_other_absent_files_after_restart(string missingKind)
    {
        await using var h = new Harness();
        h.AddPayload("healthy.rar", "sound", "sound");
        h.AddPayload("repair.r00", "bad01", "right");
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Queued, h.Item.Status);
        string missing;
        if (missingKind == "deleted")
        {
            missing = "healthy.rar";
            File.Delete(h.PathFor(missing));
        }
        else if (missingKind == "late-sfv")
        {
            missing = "late.r01";
            h.AddPayload(missing, "added", "added");
        }
        else
        {
            missing = "Proof/proof.jpg";
            h.Source.Files[missing] = Bytes("proof");
        }
        h.Source.Files["repair.r00"] = Bytes("right");
        await h.Reload();

        await h.RunOnePass();

        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.Equal(h.Source.Files[missing], File.ReadAllBytes(h.PathFor(missing)));
        Assert.Equal(missingKind == "deleted" ? 2 : 1, h.Source.Fetches[missing]);
        Assert.Equal(2, h.Source.Fetches["repair.r00"]);
        Assert.False(h.Item.SfvIntegrityHold);
    }

    [Fact]
    public async Task Nfo_precheck_does_not_create_a_staging_folder_or_marker()
    {
        await using var h = new Harness(new DownloadConfig { SfvRepairEnabled = true, SkipIncompleteReleases = true });
        h.AddPayload("disc.rar", "sound", "sound");

        await h.RunOnePass();

        Assert.Equal(DownloadStatus.Queued, h.Item.Status);
        Assert.Contains("no .nfo", h.Item.ErrorMessage);
        Assert.False(Directory.Exists(h.Item.LocalPath));
        Assert.Null(h.Item.SfvRepairOwner);
        Assert.Empty(h.Source.Fetches);
    }

    [Fact]
    public async Task Failed_disk_reservation_does_not_create_a_staging_folder_or_marker()
    {
        await using var h = new Harness(disk: new DiskReservation(0, _ => 0));
        h.AddPayload("disc.rar", "sound", "sound");

        await h.RunOnePass();

        Assert.Equal(DownloadStatus.Failed, h.Item.Status);
        Assert.Contains("Insufficient disk space", h.Item.ErrorMessage);
        Assert.False(Directory.Exists(h.Item.LocalPath));
        Assert.Null(h.Item.SfvRepairOwner);
        Assert.Empty(h.Source.Fetches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_verification_removes_owner_marker_and_empty_quarantine(bool repair)
    {
        await using var h = new Harness();
        h.AddPayload("disc.rar", repair ? "bad01" : "right", "right");
        await h.RunOnePass();
        if (repair)
        {
            Assert.True(File.Exists(h.PathFor(".gldrive-sfv-owner")));
            Assert.True(Directory.Exists(h.PathFor(".gldrive-sfv-quarantine")));
            h.Source.Files["disc.rar"] = Bytes("right");
            await h.RunOnePass();
        }

        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.False(File.Exists(h.PathFor(".gldrive-sfv-owner")));
        Assert.False(Directory.Exists(h.PathFor(".gldrive-sfv-quarantine")));
        Assert.Equal(Bytes("right"), File.ReadAllBytes(h.PathFor("disc.rar")));
    }

    [Fact]
    public async Task Repeated_same_bad_crc_stops_after_one_refetch_and_keeps_terminal_integrity_hold()
    {
        await using var h = new Harness();
        h.AddPayload("disc.rar", "bad01", "right");
        await h.RunOnePass();
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Failed, h.Item.Status);
        Assert.Equal(1, h.Record("disc.rar").Attempts);
        Assert.Equal(2, h.Source.Fetches["disc.rar"]);
        Assert.True(h.Item.SfvIntegrityHold);
        Assert.True(DownloadExtractionGuard.Blocks(h.Item, h.PathFor("disc.rar")));

        await h.Reload();
        Assert.True(h.Item.SfvIntegrityHold);
        Assert.True(DownloadExtractionGuard.Blocks(h.Item, h.PathFor("disc.rar")));
        h.Manager.Retry(h.Item.Id);
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Failed, h.Item.Status);
        Assert.Equal(1, h.Record("disc.rar").Attempts);
        Assert.Equal(2, h.Source.Fetches["disc.rar"]);
    }

    [Fact]
    public async Task Different_bad_crcs_exhaust_per_file_budget_even_across_restart_and_manual_retry()
    {
        await using var h = new Harness();
        h.AddPayload("disc.rar", "bad01", "right");
        await h.RunOnePass();
        h.Source.Files["disc.rar"] = Bytes("bad02");
        await h.Reload();
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Queued, h.Item.Status);
        Assert.Equal(2, h.Record("disc.rar").Attempts);

        h.Source.Files["disc.rar"] = Bytes("bad03");
        await h.Reload();
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Failed, h.Item.Status);
        Assert.Equal(2, h.Record("disc.rar").Attempts);
        Assert.Equal(3, h.Source.Fetches["disc.rar"]);
        h.Manager.Retry(h.Item.Id);
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Failed, h.Item.Status);
        Assert.Equal(2, h.Record("disc.rar").Attempts);
        Assert.Equal(3, h.Source.Fetches["disc.rar"]);
    }

    [Fact]
    public async Task Two_bad_files_have_independent_budgets_and_successful_file_is_not_refetched_again()
    {
        await using var h = new Harness();
        h.AddPayload("first.rar", "bad01", "right");
        h.AddPayload("second.rar", "bad01", "right");
        await h.RunOnePass();
        h.Source.Files["first.rar"] = Bytes("right");
        h.Source.Files["second.rar"] = Bytes("bad02");
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Queued, h.Item.Status);
        Assert.Equal(1, h.Record("first.rar").Attempts);
        Assert.Equal(2, h.Record("second.rar").Attempts);
        h.Source.Files["second.rar"] = Bytes("right");
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.Equal(2, h.Source.Fetches["first.rar"]);
        Assert.Equal(3, h.Source.Fetches["second.rar"]);
    }

    [Fact]
    public async Task Nested_sfv_paths_repair_the_exact_disc_instead_of_same_basename_in_other_disc()
    {
        await using var h = new Harness();
        h.Source.Files["CD1/disc.rar"] = Bytes("sound");
        h.Source.Files["CD2/disc.rar"] = Bytes("bad01");
        h.Source.Files["CD1/disc.sfv"] = Sfv(("disc.rar", "sound"));
        h.Source.Files["CD2/disc.sfv"] = Sfv(("disc.rar", "right"));
        await h.RunOnePass();
        Assert.Equal(1, h.Record("CD2/disc.rar").Attempts);
        Assert.Equal(0, h.Record("CD1/disc.rar").Attempts);
        h.Source.Files["CD2/disc.rar"] = Bytes("right");
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.Equal(1, h.Source.Fetches["CD1/disc.rar"]);
        Assert.Equal(2, h.Source.Fetches["CD2/disc.rar"]);
        Assert.Equal(Bytes("sound"), File.ReadAllBytes(h.PathFor("CD1/disc.rar")));
    }

    [Fact]
    public async Task Default_configuration_does_not_mutate_or_refetch_crc_bad_payload()
    {
        var config = new DownloadConfig();
        Assert.False(config.SfvRepairEnabled);
        Assert.Equal(2, config.SfvRepairMaxAttempts);
        await using var h = new Harness(config);
        h.AddPayload("disc.rar", "bad01", "right");
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Failed, h.Item.Status);
        Assert.Equal(1, h.Source.Fetches["disc.rar"]);
        Assert.Equal(Bytes("bad01"), File.ReadAllBytes(h.PathFor("disc.rar")));
        Assert.Empty(h.Item.SfvRepairFiles);
        Assert.False(h.Item.SfvIntegrityHold);
    }

    [Fact]
    public async Task Preexisting_user_root_and_file_are_never_claimed_or_quarantined()
    {
        await using var h = new Harness();
        h.AddPayload("disc.rar", "bad01", "right");
        Directory.CreateDirectory(h.Item.LocalPath);
        File.WriteAllBytes(h.PathFor("disc.rar"), Bytes("user!"));
        File.WriteAllText(h.PathFor("notes.txt"), "keep me");
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Failed, h.Item.Status);
        Assert.Equal(Bytes("user!"), File.ReadAllBytes(h.PathFor("disc.rar")));
        Assert.Equal("keep me", File.ReadAllText(h.PathFor("notes.txt")));
        Assert.False(h.Source.Fetches.ContainsKey("disc.rar"));
        Assert.Empty(h.Item.SfvRepairFiles);
        Assert.False(h.Item.SfvIntegrityHold);
        Assert.Null(h.Item.SfvRepairOwner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_manifest_is_not_repaired_even_when_another_entry_has_bad_crc(bool withBadCrc)
    {
        await using var h = new Harness();
        h.Source.Files["disc.rar"] = Bytes("bad01");
        h.Source.Files["disc.sfv"] = Bytes((withBadCrc ? Encoding.UTF8.GetString(Sfv(("disc.rar", "right"))) : "") + "invalid entry\n");
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Failed, h.Item.Status);
        Assert.Equal(1, h.Source.Fetches["disc.rar"]);
        Assert.All(h.Item.SfvRepairFiles, record => Assert.Equal(0, record.Attempts));
        Assert.Equal(Bytes("bad01"), File.ReadAllBytes(h.PathFor("disc.rar")));
    }

    [Fact]
    public async Task Failed_durable_budget_write_preserves_bad_original_and_does_not_refetch()
    {
        await using var h = new Harness();
        h.AddPayload("disc.rar", "bad01", "right");
        h.Source.AfterDownload = () =>
        {
            File.Delete(h.QueuePath);
            Directory.CreateDirectory(h.QueuePath);
        };
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Failed, h.Item.Status);
        Assert.Equal(1, h.Source.Fetches["disc.rar"]);
        Assert.Equal(Bytes("bad01"), File.ReadAllBytes(h.PathFor("disc.rar")));
        Assert.True(h.Item.SfvIntegrityHold);
        Assert.True(DownloadExtractionGuard.Blocks(h.Item, h.PathFor("disc.rar")));
        var beforeRetry = Directory.GetFiles(h.Item.LocalPath, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        h.Source.AfterDownload = null;
        h.Manager.Retry(h.Item.Id);
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Failed, h.Item.Status);
        Assert.Equal(1, h.Source.Fetches["disc.rar"]);
        Assert.Equal(Bytes("bad01"), File.ReadAllBytes(h.PathFor("disc.rar")));
        Assert.Equal(beforeRetry, Directory.GetFiles(h.Item.LocalPath, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.All(h.Item.SfvRepairFiles, record =>
        {
            if (record.QuarantinePath != null) Assert.False(File.Exists(record.QuarantinePath));
        });
    }

    [Fact]
    public async Task Disabling_repair_while_pending_stops_refetch_and_retains_integrity_hold()
    {
        await using var h = new Harness();
        h.AddPayload("disc.rar", "bad01", "right");
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Queued, h.Item.Status);
        Assert.True(h.Item.SfvIntegrityHold);
        var quarantine = h.Record("disc.rar").QuarantinePath;
        Assert.NotNull(quarantine);
        Assert.True(File.Exists(quarantine));
        h.Config.SfvRepairEnabled = false;
        h.Source.Files["disc.rar"] = Bytes("right");
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Failed, h.Item.Status);
        Assert.Equal(1, h.Source.Fetches["disc.rar"]);
        Assert.Equal(1, h.Record("disc.rar").Attempts);
        Assert.True(h.Item.SfvIntegrityHold);
        Assert.True(DownloadExtractionGuard.Blocks(h.Item, h.PathFor("disc.rar")));
        Assert.Equal(Bytes("bad01"), File.ReadAllBytes(quarantine));
    }

    [Fact]
    public async Task Turning_checksum_toggle_off_does_not_erase_previously_observed_integrity_failure()
    {
        await using var h = new Harness();
        h.AddPayload("disc.rar", "bad01", "right");
        await h.RunOnePass();
        h.Config.VerifySfv = false;
        await h.RunOnePass();
        Assert.Equal(DownloadStatus.Failed, h.Item.Status);
        Assert.True(h.Item.SfvIntegrityHold);
        Assert.True(DownloadExtractionGuard.Blocks(h.Item, h.PathFor("disc.rar")));
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static byte[] Sfv(params (string Name, string Contents)[] entries) => Bytes(string.Join("\n",
        entries.Select(e => $"{e.Name} {Crc32.HashToUInt32(Bytes(e.Contents)):X8}")) + "\n");

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-sfv-repair-" + Guid.NewGuid().ToString("N"));
        private readonly List<(string Name, string Contents)> _entries = [];
        private readonly DiskReservation? _disk;
        public DownloadConfig Config { get; }
        public DownloadStore Store { get; private set; }
        public DownloadManager Manager { get; private set; }
        public DownloadItem Item { get; private set; }
        public FakeSource Source { get; } = new();
        public string QueuePath => Path.Combine(_root, "downloads-test.json");
        public string PathFor(string relative) => Path.Combine(Item.LocalPath, relative.Replace('/', Path.DirectorySeparatorChar));
        public SfvRepairFile Record(string relative) => Assert.Single(Item.SfvRepairFiles,
            r => Path.GetFullPath(r.LocalPath).Equals(PathFor(relative), StringComparison.OrdinalIgnoreCase));

        public Harness(DownloadConfig? config = null, DiskReservation? disk = null)
        {
            Directory.CreateDirectory(_root);
            _disk = disk;
            Config = config ?? new DownloadConfig { SfvRepairEnabled = true };
            Config.MaxConcurrentDownloads = 1;
            Config.AutoExtract = false;
            Config.MaxRetries = 0;
            Store = DownloadStore.ForFile(QueuePath);
            Manager = CreateManager();
            Item = new DownloadItem { RemotePath = "/release", LocalPath = Path.Combine(_root, "release"), ReleaseName = "Example.2026.1080p.WEB-GROUP" };
            Store.Add(Item);
        }

        private DownloadManager CreateManager() => new(Store, Source, Config, _disk) { SfvRepairRecheckSeconds = 30 };

        public void AddPayload(string name, string actual, string expected)
        {
            Source.Files[name] = Bytes(actual);
            _entries.Add((name, expected));
            Source.Files["release.sfv"] = Sfv(_entries.ToArray());
        }

        public async Task RunOnePass()
        {
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(DownloadItem item)
            {
                if (item.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled ||
                    item.Status == DownloadStatus.Queued &&
                    (item.StatusDetail?.StartsWith("SFV repair", StringComparison.Ordinal) == true || item.ErrorMessage != null))
                    settled.TrySetResult();
            }
            Manager.DownloadStatusChanged += Changed;
            try
            {
                Manager.Start();
                await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                await Manager.StopAsync();
                Manager.DownloadStatusChanged -= Changed;
            }
        }

        public async Task Reload()
        {
            await Manager.StopAsync();
            Manager.Dispose();
            Store.Flush();
            Store = DownloadStore.ForFile(QueuePath);
            Store.Load();
            Item = Assert.Single(Store.Items);
            Manager = CreateManager();
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.StopAsync();
            Manager.Dispose();
            Store.Flush();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeSource : IDownloadSource
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Fetches { get; } = new(StringComparer.Ordinal);
        public Action? AfterDownload { get; set; }
        public bool IsInCooldown => false;

        public Task<DownloadTreePlan> PlanAsync(string remotePath, string localPath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var files = Files.Select(f => new FtpDownloadFile(remotePath + "/" + f.Key,
                Path.Combine(localPath, f.Key.Replace('/', Path.DirectorySeparatorChar)), f.Value.LongLength)).ToArray();
            return Task.FromResult(new DownloadTreePlan(true, files,
                files.Select(f => Path.GetDirectoryName(f.LocalPath)!).Append(localPath).Distinct().ToArray()));
        }

        public async Task DownloadAsync(IReadOnlyList<FtpDownloadFile> files, IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(file.LocalPath) && new FileInfo(file.LocalPath).Length == file.Size) continue;
                var key = file.RemotePath["/release/".Length..];
                Directory.CreateDirectory(Path.GetDirectoryName(file.LocalPath)!);
                await File.WriteAllBytesAsync(file.LocalPath, Files[key], ct);
                Fetches[key] = Fetches.GetValueOrDefault(key) + 1;
            }
            AfterDownload?.Invoke();
        }

        public async Task RefreshManifestAsync(FtpDownloadFile file, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.LocalPath)!);
            await File.WriteAllBytesAsync(file.LocalPath, Files[file.RemotePath["/release/".Length..]], ct);
        }
    }
}
