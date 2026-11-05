using System.IO;
using System.Diagnostics;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Downloads.ArchiveExtraction;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class DownloadExtractionSpaceTests
{
    // Generated RAR5 containing one 4 KiB zero-filled payload.txt; no external tool at test time.
    internal static readonly byte[] Archive = Convert.FromBase64String("UmFyIRoHAQDz4YLrCwEFBwAGAQGAgIAAbVrdoycCAwuRAASAICARABzHgAUAC3BheWxvYWQudHh0CgMCPQRQoH1s3QHBlQ4C/iGf38Ju03v1/P5/AB13VlEDBQQA");

    private static readonly ArchiveUnpacker Unpacker = new(new FakeTool((_, _) => false));


    [Fact]
    public async Task Insufficient_unpacked_space_parks_before_output_without_spending_retry_budget()
    {
        await using var h = new Harness();
        h.Item.RetryCount = 3;
        await h.Run();
        Assert.Equal(DownloadStatus.Queued, h.Item.Status);
        Assert.Equal(3, h.Item.RetryCount);
        Assert.Equal("extraction-space", DownloadWaiting.Describe(h.Item)!.ReasonCode);
        Assert.Null(h.Item.CompletedAt);
        Assert.True(File.Exists(Path.Combine(h.Item.LocalPath, "fixture.rar")));
        Assert.False(File.Exists(Path.Combine(h.Item.LocalPath, "payload.txt")));
        Assert.Empty(Directory.GetFiles(h.Item.LocalPath, "*.gldrive-tmp"));
        Assert.Equal(0, h.Disk.ReservedOn(h.Item.LocalPath));
        Assert.Equal(0, h.TerminalEvents);
    }

    [Fact]
    public async Task Restart_waits_without_remote_work_then_capacity_recovery_extracts_preserved_archive()
    {
        await using var h = new Harness();
        await h.Run();
        Assert.Equal(DownloadStatus.Queued, h.Item.Status);
        var plans = h.Source.Plans;
        await h.Reload();
        h.Source.RefuseRemoteWork = true;
        await h.Run();
        Assert.Equal(DownloadStatus.Queued, h.Item.Status);
        Assert.Equal(plans, h.Source.Plans);
        h.Free = 8192;
        await h.Run();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.Equal(new byte[4096], File.ReadAllBytes(Path.Combine(h.Item.LocalPath, "payload.txt")));
        Assert.Equal(plans, h.Source.Plans);
        Assert.Equal(1, h.Source.Transfers);
        Assert.Equal(0, h.Disk.ReservedOn(h.Item.LocalPath));
    }

    [Fact]
    public async Task Shared_reservations_cover_output_until_write_finishes_then_release()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Item.LocalPath);
        File.WriteAllBytes(Path.Combine(h.Item.LocalPath, "fixture.rar"), Archive);
        h.Free = 4096;
        Assert.True(h.Disk.TryReserve(h.Item.LocalPath, 1, out var root));
        var shortage = await Assert.ThrowsAsync<ExtractionSpaceException>(() =>
            Unpacker.ExtractDirectoryAsync(h.Item.LocalPath, default, h.Disk));
        Assert.Equal(4096, shortage.RequiredBytes);
        Assert.False(File.Exists(Path.Combine(h.Item.LocalPath, "payload.txt")));
        h.Disk.Release(root, 1);
        await Unpacker.ExtractDirectoryAsync(h.Item.LocalPath, default, h.Disk, (stream, target, ct, length) =>
        {
            Assert.Equal(4096, h.Disk.ReservedOn(target));
            Assert.False(h.Disk.TryReserve(target, 1, out _));
            ArchiveFileOperations.CopyToFileAtomically(stream, target, ct, length);
        });
        Assert.Equal(0, h.Disk.ReservedOn(h.Item.LocalPath));
        Assert.Equal(4096, new FileInfo(Path.Combine(h.Item.LocalPath, "payload.txt")).Length);
    }

    [Fact]
    public async Task Published_checkpoint_snapshot_stays_stable_while_worker_advances()
    {
        await using var h = new Harness();
        h.Free = 8192;
        DownloadExtractionCheckpoint? published = null;
        h.Manager.DownloadStatusChanged += item =>
        {
            if (item.Status == DownloadStatus.Extracting) published = item.ExtractionCheckpoint;
        };
        await h.Run();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.NotNull(published);
        // DownloadStore serializers may still be enumerating this published
        // snapshot after the worker has advanced to a new one.
        Assert.Single(published.Directories);
        Assert.Single(published.Inputs);
        Assert.Null(h.Item.ExtractionCheckpoint);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070070))]
    [InlineData(unchecked((int)0x80070027))]
    public async Task Mid_copy_disk_full_is_typed_preserves_archives_and_cleans_temporary_output(int code)
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Item.LocalPath);
        var archive = Path.Combine(h.Item.LocalPath, "fixture.rar");
        var target = Path.Combine(h.Item.LocalPath, "payload.txt");
        File.WriteAllBytes(archive, Archive);
        File.WriteAllText(target, "known-good");
        h.Free = 8192;
        var error = await Assert.ThrowsAsync<ExtractionSpaceException>(() =>
            Unpacker.ExtractDirectoryAsync(h.Item.LocalPath, default, h.Disk, (_, destination, ct, length) =>
            {
                using var source = new FullDiskStream(code);
                ArchiveFileOperations.CopyToFileAtomically(source, destination, ct, length);
            }));
        Assert.Equal(4096, error.RequiredBytes);
        Assert.True(ExtractSpaceParking.IsDiskFull(error.InnerException));
        Assert.Equal("known-good", File.ReadAllText(target));
        Assert.Equal(Archive, File.ReadAllBytes(archive));
        Assert.Empty(Directory.GetFiles(h.Item.LocalPath, "*.gldrive-tmp"));
        Assert.Equal(0, h.Disk.ReservedOn(h.Item.LocalPath));
    }

    [Fact]
    public async Task Changed_archive_invalidates_recovery_and_reenters_normal_verification()
    {
        await using var h = new Harness();
        await h.Run();
        var plans = h.Source.Plans;
        File.SetLastWriteTimeUtc(Path.Combine(h.Item.LocalPath, "fixture.rar"), DateTime.UtcNow.AddMinutes(1));
        h.Free = 8192;
        await h.Run();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.True(h.Source.Plans > plans);
        Assert.Equal(2, h.Source.Transfers);
    }

    [Fact]
    public async Task Changed_sfv_and_stricter_settings_invalidate_saved_verification()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Item.LocalPath);
        File.WriteAllBytes(Path.Combine(h.Item.LocalPath, "fixture.rar"), Archive);
        var sfv = Path.Combine(h.Item.LocalPath, "fixture.sfv");
        File.WriteAllText(sfv, "fixture.rar 00000000");
        var checkpoint = DownloadExtractionCheckpoint.Capture(h.Item, [h.Item.LocalPath]);
        Assert.True(checkpoint.IsCurrent(h.Item));
        Assert.False(checkpoint.IsCurrent(h.Item, requireSfv: true));
        Assert.False(checkpoint.IsCurrent(h.Item, requireNfo: true));
        checkpoint = DownloadExtractionCheckpoint.Capture(h.Item, [h.Item.LocalPath], verifiedSfv: true);
        Assert.True(checkpoint.IsCurrent(h.Item, requireSfv: true));
        File.SetLastWriteTimeUtc(sfv, DateTime.UtcNow.AddMinutes(1));
        Assert.False(checkpoint.IsCurrent(h.Item, requireSfv: true));
    }

    [Fact]
    public async Task Changed_verified_plain_file_invalidates_but_new_extracted_output_does_not()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Item.LocalPath);
        File.WriteAllBytes(Path.Combine(h.Item.LocalPath, "fixture.rar"), Archive);
        var sample = Path.Combine(h.Item.LocalPath, "sample.mkv");
        File.WriteAllText(sample, "verified-original");
        var checkpoint = DownloadExtractionCheckpoint.Capture(h.Item, [h.Item.LocalPath]);
        File.WriteAllText(Path.Combine(h.Item.LocalPath, "payload.txt"), "newly-extracted");
        Assert.True(checkpoint.IsCurrent(h.Item));
        File.WriteAllText(sample, "changed");
        Assert.False(checkpoint.IsCurrent(h.Item));
    }

    [Theory]
    [InlineData("duplicate-input")]
    [InlineData("directory-escape")]
    [InlineData("waiting-escape")]
    [InlineData("waiting-not-pending")]
    [InlineData("input-escape")]
    [InlineData("integrity-hold")]
    public async Task Invalid_checkpoint_cannot_authorize_local_extraction(string invalid)
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Item.LocalPath);
        File.WriteAllBytes(Path.Combine(h.Item.LocalPath, "fixture.rar"), Archive);
        File.WriteAllText(Path.Combine(h.Item.LocalPath, "fixture.sfv"), "fixture.rar 00000000");
        var checkpoint = DownloadExtractionCheckpoint.Capture(h.Item, [h.Item.LocalPath]);
        Assert.True(checkpoint.IsCurrent(h.Item));
        switch (invalid)
        {
            case "duplicate-input": checkpoint.Inputs[1] = checkpoint.Inputs[0]; break;
            case "directory-escape": checkpoint.Directories[0] = Path.GetTempPath(); break;
            case "waiting-escape": checkpoint.WaitingDirectory = Path.GetTempPath(); break;
            case "waiting-not-pending": checkpoint.WaitingDirectory = Path.Combine(h.Item.LocalPath, "other"); break;
            case "input-escape": checkpoint.Inputs[0] = checkpoint.Inputs[0] with { Path = Path.Combine(Path.GetTempPath(), "outside.rar") }; break;
            case "integrity-hold": h.Item.SfvIntegrityHold = true; break;
        }
        Assert.False(checkpoint.IsCurrent(h.Item));
    }

    [Fact]
    public async Task Download_reservation_uses_real_files_instead_of_stale_progress()
    {
        await using var h = new Harness();
        h.Item.TotalBytes = h.Item.DownloadedBytes = Archive.Length;
        h.Free = Archive.Length - 1;
        await h.Run();
        Assert.Equal(DownloadStatus.Queued, h.Item.Status);
        Assert.Equal("download-space", DownloadWaiting.Describe(h.Item)!.ReasonCode);
        Assert.Equal(0, h.Source.Transfers);
        Assert.Equal(0, h.Disk.ReservedOn(h.Item.LocalPath));
    }

    [Fact]
    public async Task Download_capacity_wait_survives_restart_without_source_work_or_retry_cost()
    {
        await using var h = new Harness();
        h.Free = Archive.Length - 1;
        h.Item.RetryCount = 3;
        await h.Run();
        Assert.Equal(DownloadStatus.Queued, h.Item.Status);
        var plans = h.Source.Plans;
        await h.Reload();
        h.Source.RefuseRemoteWork = true;
        await h.Run();
        Assert.Equal(DownloadStatus.Queued, h.Item.Status);
        Assert.Equal(plans, h.Source.Plans);
        Assert.Equal(3, h.Item.RetryCount);
        Assert.Equal(0, h.TerminalEvents);
        h.Source.RefuseRemoteWork = false;
        h.Free = 8192;
        await h.Run();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.True(h.Source.Plans > plans);
        Assert.Equal(1, h.Source.Transfers);
        Assert.Null(h.Item.SpaceCheckpoint);
    }

    [Theory]
    [InlineData("destination")]
    [InlineData("remote")]
    [InlineData("server")]
    [InlineData("source-kind")]
    [InlineData("selection")]
    [InlineData("resume")]
    [InlineData("retry")]
    public async Task Changed_identity_and_explicit_user_restarts_require_a_fresh_capacity_plan(string change)
    {
        await using var h = new Harness();
        h.Free = Archive.Length - 1;
        await h.Run();
        var plans = h.Source.Plans;
        Assert.NotNull(h.Item.SpaceCheckpoint);
        switch (change)
        {
            case "destination": h.Item.LocalPath += "-other"; break;
            case "remote": h.Item.RemotePath += "-other"; break;
            case "server": h.Item.ServerId = "other"; break;
            case "source-kind": h.Item.SourceKind = DownloadSourceKind.Torrent; break;
            case "selection": h.Item.SkipBlockedTorrentFiles = true; break;
            case "resume": h.Manager.Pause(h.Item.Id); h.Manager.Resume(h.Item.Id); break;
            case "retry": h.Manager.Cancel(h.Item.Id); h.Manager.Retry(h.Item.Id); break;
        }
        await h.Run();
        Assert.Equal(DownloadStatus.Queued, h.Item.Status);
        Assert.True(h.Source.Plans > plans);
        Assert.Equal(0, h.Source.Transfers);
    }

    [Fact]
    public async Task Newly_completed_local_cache_can_release_capacity_wait_without_waiting_for_the_old_byte_budget()
    {
        await using var h = new Harness();
        h.Source.SingleFile = true;
        h.Free = Archive.Length - 1;
        await h.Run();
        var plans = h.Source.Plans;
        File.WriteAllBytes(h.Item.LocalPath, Archive);
        await h.Run();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.True(h.Source.Plans > plans);
        Assert.Null(h.Item.SpaceCheckpoint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verified_local_extraction_recovery_does_not_wait_for_ftp_admission(bool racing)
    {
        await using var h = new Harness();
        await h.Run();
        h.Free = 8192;
        h.Source.RefuseRemoteWork = true;
        h.Source.IsInCooldown = !racing;
        h.Item.BackgroundWantedPull = racing;
        h.Manager.BackgroundPullMayRun = () => false;
        await h.Run();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.Equal(new byte[4096], File.ReadAllBytes(Path.Combine(h.Item.LocalPath, "payload.txt")));
    }

    [Fact]
    public async Task Single_file_plan_reserves_its_metadata_size_before_starting_a_transfer()
    {
        await using var h = new Harness();
        h.Source.SingleFile = true;
        h.Free = Archive.Length - 1;
        await h.Run();
        Assert.Equal(DownloadStatus.Queued, h.Item.Status);
        Assert.Equal("download-space", DownloadWaiting.Describe(h.Item)!.ReasonCode);
        Assert.Equal(0, h.Source.Transfers);
        h.Free = 8192;
        await h.Run();
        Assert.Equal(DownloadStatus.Completed, h.Item.Status);
        Assert.Equal(Archive, File.ReadAllBytes(h.Item.LocalPath));
    }

    [Fact]
    public async Task Checkpoint_rejects_a_real_junction_before_inspecting_its_files()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Item.LocalPath);
        var outside = Path.Combine(Path.GetDirectoryName(h.Item.LocalPath)!, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "fixture.rar"), Archive);
        var link = Path.Combine(h.Item.LocalPath, "linked");
        using var create = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{outside}\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        Assert.True(create.WaitForExit(10000));
        Assert.Equal(0, create.ExitCode);
        try
        {
            Assert.False(DownloadExtractionCheckpoint.SafePath(h.Item.LocalPath, link));
            Assert.Throws<IOException>(() => DownloadExtractionCheckpoint.Capture(h.Item, [link]));
            var checkpoint = new DownloadExtractionCheckpoint
            {
                LocalRoot = h.Item.LocalPath, RemotePath = h.Item.RemotePath, Directories = [link]
            };
            Assert.False(checkpoint.IsCurrent(h.Item));
            Assert.Equal(Archive, File.ReadAllBytes(Path.Combine(outside, "fixture.rar")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Checkpoint_normalizes_directory_aliases_and_rejects_stale_noncanonical_paths()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Item.LocalPath);
        File.WriteAllBytes(Path.Combine(h.Item.LocalPath, "fixture.rar"), Archive);
        var alias = h.Item.LocalPath + Path.DirectorySeparatorChar;
        var checkpoint = DownloadExtractionCheckpoint.Capture(h.Item, [h.Item.LocalPath, alias]);
        Assert.Equal(h.Item.LocalPath, Assert.Single(checkpoint.Directories));
        Assert.True(checkpoint.IsCurrent(h.Item));
        var advanced = checkpoint.Transition(completedDirectory: h.Item.LocalPath);
        Assert.Empty(advanced.Directories);
        Assert.Empty(advanced.Inputs);
        checkpoint.Directories[0] = alias;
        Assert.False(checkpoint.IsCurrent(h.Item));
    }

    private sealed class FullDiskStream(int code) : MemoryStream(new byte[1])
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position != 0) throw new IOException("Synthetic disk-full write boundary", code);
            return base.Read(buffer, offset, count);
        }
    }

    [Fact]
    public async Task Unreadable_rar_falls_back_to_external_tool_with_archive_size_reserved()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Item.LocalPath);
        var archive = Path.Combine(h.Item.LocalPath, "broken.rar");
        File.WriteAllBytes(archive, new byte[3000]);
        h.Free = 1 << 20;
        string? calledWith = null;
        var unpacker = new ArchiveUnpacker(new FakeTool((path, output) =>
        {
            calledWith = path;
            Assert.Equal(3000, h.Disk.ReservedOn(output));
            File.WriteAllText(Path.Combine(output, "payload.txt"), "from tool");
            return true;
        }));

        var result = await unpacker.ExtractDirectoryAsync(h.Item.LocalPath, default, h.Disk);

        Assert.True(result.Extracted);
        Assert.Equal(archive, calledWith);
        Assert.Equal(0, h.Disk.ReservedOn(h.Item.LocalPath));
        Assert.True(File.Exists(Path.Combine(h.Item.LocalPath, "payload.txt")));
    }

    [Fact]
    public async Task Unreadable_rar_without_external_tool_still_fails()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Item.LocalPath);
        File.WriteAllBytes(Path.Combine(h.Item.LocalPath, "broken.rar"), new byte[3000]);
        h.Free = 1 << 20;

        await Assert.ThrowsAnyAsync<Exception>(() => Unpacker.ExtractDirectoryAsync(h.Item.LocalPath, default, h.Disk));
        Assert.Equal(0, h.Disk.ReservedOn(h.Item.LocalPath));
    }

    [Fact]
    public async Task External_fallback_parks_when_archive_size_does_not_fit()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(h.Item.LocalPath);
        File.WriteAllBytes(Path.Combine(h.Item.LocalPath, "broken.rar"), new byte[3000]);
        h.Free = 1000;
        var called = false;
        var unpacker = new ArchiveUnpacker(new FakeTool((_, _) => called = true));

        var error = await Assert.ThrowsAsync<ExtractionSpaceException>(() =>
            unpacker.ExtractDirectoryAsync(h.Item.LocalPath, default, h.Disk));

        Assert.Equal(3000, error.RequiredBytes);
        Assert.False(called);
    }

    [Fact]
    public void External_tool_output_never_commits_executables()
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-commit-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(root, "staging");
        var output = Path.Combine(root, "out");
        try
        {
            Directory.CreateDirectory(Path.Combine(staging, "Sample"));
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(staging, "movie.mkv"), "video");
            File.WriteAllText(Path.Combine(staging, "Sample", "setup.exe"), "MZ");

            ExternalArchiveTool.CommitStagedOutput(staging, output, 0, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(output, "movie.mkv")));
            Assert.False(File.Exists(Path.Combine(output, "Sample", "setup.exe")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class FakeTool(Func<string, string, bool> behaviour) : IExternalArchiveTool
    {
        public bool TryExtract(string archivePath, string outputDir, string password, int overwriteMode,
            IProgress<ArchiveProgress> progress, CancellationToken ct)
        {
            Assert.Equal("", password);
            Assert.Equal(0, overwriteMode);
            return behaviour(archivePath, outputDir);
        }
    }

    internal sealed class Harness : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-extract-space-" + Guid.NewGuid().ToString("N"));
        private readonly DownloadConfig _config = new() { AutoExtract = true, VerifySfv = false, MaxRetries = 0, MaxConcurrentDownloads = 1 };
        public long Free = 512;
        public DiskReservation Disk { get; }
        public Source Source { get; } = new();
        public DownloadStore Store { get; private set; }
        public DownloadManager Manager { get; private set; }
        public DownloadItem Item { get; private set; }
        public int TerminalEvents;

        public Harness()
        {
            Directory.CreateDirectory(_root);
            Disk = new DiskReservation(0, _ => Free);
            Store = DownloadStore.ForFile(Path.Combine(_root, "queue.json"));
            Manager = NewManager();
            Item = new() { LocalPath = Path.Combine(_root, "release"), RemotePath = "/release", ReleaseName = "space-fixture" };
            Store.Add(Item);
        }

        private DownloadManager NewManager()
        {
            var manager = new DownloadManager(Store, Source, _config, Disk);
            manager.DownloadSettled += _ => TerminalEvents++;
            return manager;
        }

        public async Task Run()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(DownloadItem item)
            {
                if (item.Status is DownloadStatus.Completed or DownloadStatus.Failed ||
                    item.Status == DownloadStatus.Queued && item.ErrorMessage != null) done.TrySetResult();
            }
            Manager.DownloadStatusChanged += Changed;
            try { Manager.Start(); await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { await Manager.StopAsync(); Manager.DownloadStatusChanged -= Changed; }
        }

        public async Task Reload()
        {
            await Manager.StopAsync(); Manager.Dispose(); Store.Flush();
            Store = DownloadStore.ForFile(Path.Combine(_root, "queue.json"));
            Store.Load(); Item = Assert.Single(Store.Items); Manager = NewManager();
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.StopAsync(); Manager.Dispose(); Store.Flush();
            Directory.Delete(_root, true);
        }
    }

    internal sealed class Source : IDownloadSource
    {
        public int Plans, Transfers;
        public bool RefuseRemoteWork;
        public bool IsInCooldown { get; set; }
        public bool SingleFile;
        public Task RefreshManifestAsync(FtpDownloadFile file, CancellationToken ct) => throw new InvalidOperationException("No SFV in this fixture");
        public Task<DownloadTreePlan> PlanAsync(string remotePath, string localPath, CancellationToken ct)
        {
            Plans++;
            if (RefuseRemoteWork) throw new IOException("Remote work must not repeat during extraction recovery");
            if (SingleFile)
                return DownloadTree.BuildAsync(remotePath, localPath,
                    (_, _) => Task.FromResult(false), (_, _) => Task.FromResult(true),
                    (_, _) => Task.FromResult(new[] { new FluentFTP.FtpListItem
                    {
                        Name = Path.GetFileName(remotePath), Type = FluentFTP.FtpObjectType.File, Size = Archive.Length
                    } }), ct);
            return Task.FromResult(new DownloadTreePlan(true,
                [new FtpDownloadFile(remotePath + "/fixture.rar", Path.Combine(localPath, "fixture.rar"), Archive.Length)], [localPath]));
        }
        public Task DownloadAsync(IReadOnlyList<FtpDownloadFile> files, IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            Transfers++;
            foreach (var file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.LocalPath)!);
                File.WriteAllBytes(file.LocalPath, Archive);
            }
            return Task.CompletedTask;
        }
    }
}
