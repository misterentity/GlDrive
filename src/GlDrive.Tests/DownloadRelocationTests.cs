using System.IO;
using System.Text.Json;
using FluentFTP.Exceptions;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using Xunit;

namespace GlDrive.Tests;

public sealed class DownloadRelocationTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DownloadItem Item() => new()
    {
        RemotePath = "/incoming/video/Example.2026.1080p-GRP", Category = "video",
        ReleaseName = "Example.2026.1080p-GRP", SourceCompletionPending = true
    };

    [Fact]
    public void Candidates_include_watch_sections_and_all_matching_maps_with_deduplication()
    {
        var config = new ServerConfig();
        config.Notifications.WatchPath = "/recent/";
        config.SpreadSite.Sections = new() { ["video"] = "/incoming/video", ["archive"] = "/archive/video" };
        config.SpreadSite.SectionMappings =
        [
            new() { IrcSection = "VIDEO", RemoteSection = "archive", Path = "/completed/video", TriggerRegex = "1080p" },
            new() { IrcSection = "video", RemoteSection = "archive", Path = "/later/video" },
            new() { IrcSection = "video", RemoteSection = "archive", Path = "/wrong", TriggerRegex = "2160p" },
            new() { IrcSection = "other", RemoteSection = "archive", Path = "/other" },
            new() { IrcSection = "video", RemoteSection = "archive", Path = "/disabled", Enabled = false }
        ];
        Assert.Equal(new[] { "/recent/video/", "/completed/video/", "/archive/video/", "/recent/archive/", "/later/video/" }
            .Select(p => p + Item().ReleaseName), DownloadRelocation.Candidates(config, Item()));
    }

    [Theory]
    [InlineData("../escape", "release")]
    [InlineData("video", "../escape")]
    [InlineData("video", ".metadata")]
    [InlineData("video", "bad\\name")]
    public void Unsafe_names_have_no_candidates(string category, string release)
    {
        var item = Item(); item.Category = category; item.ReleaseName = release;
        Assert.Empty(DownloadRelocation.Candidates(new ServerConfig(), item));
    }

    [Fact]
    public void Unsafe_configured_roots_are_ignored()
    {
        var config = new ServerConfig();
        config.Notifications.WatchPath = "relative";
        config.SpreadSite.Sections = new() { ["a"] = "/bad/../path", ["b"] = "/bad\\path", ["c"] = "/ok" };
        Assert.Equal("/ok/" + Item().ReleaseName, Assert.Single(DownloadRelocation.Candidates(config, Item())));
    }

    [Fact]
    public async Task Successful_lookup_changes_only_remote_path_and_persists_budget_before_probe()
    {
        var item = Item();
        var local = item.LocalPath = "unchanged-local";
        var probes = new List<string>();
        string? persisted = null;
        var relocation = new DownloadRelocation(() => new ServerConfig(), (path, _) =>
        {
            Assert.NotNull(persisted);
            probes.Add(path);
            return Task.FromResult(path.StartsWith("/recent/"));
        });
        var result = await relocation.ResolveAsync(item, Now, () => persisted = JsonSerializer.Serialize(item), default);
        Assert.Equal(RelocationOutcome.Relocated, result.Outcome);
        Assert.Contains("relocated from /incoming/video/", result.Reason);
        Assert.Equal("/recent/video/" + item.ReleaseName, item.RemotePath);
        Assert.Equal(local, item.LocalPath);
        Assert.Equal(2, probes.Count);
        var restored = JsonSerializer.Deserialize<DownloadItem>(persisted!)!;
        Assert.Equal(1, restored.RelocationAttempts);
        Assert.Equal(Now, restored.LastRelocationLookupAt);
        Assert.Equal(RelocationOutcome.Waiting, DownloadRelocation.Admission(restored, Now.AddMinutes(4))!.Outcome);
        Assert.Null(DownloadRelocation.Admission(restored, Now.AddMinutes(5)));
    }

    [Fact]
    public async Task Failed_lookup_retains_path_and_caps_attempts_across_reload()
    {
        var item = Item();
        var probes = 0;
        var relocation = new DownloadRelocation(() => new ServerConfig(), (_, _) => { probes++; return Task.FromResult(false); });
        for (var attempt = 0; attempt < DownloadRelocation.MaxAttempts; attempt++)
        {
            var result = await relocation.ResolveAsync(item, Now.AddMinutes(attempt * 5), () => { }, default);
            Assert.Equal(RelocationOutcome.Missing, result.Outcome);
            Assert.Equal(Item().RemotePath, item.RemotePath);
            item = JsonSerializer.Deserialize<DownloadItem>(JsonSerializer.Serialize(item))!;
        }
        var count = probes;
        Assert.Equal(RelocationOutcome.Exhausted,
            (await relocation.ResolveAsync(item, Now.AddDays(1), () => { }, default)).Outcome);
        Assert.Equal(count, probes);
    }

    [Fact]
    public async Task Throttled_lookup_makes_no_probe_and_does_not_spend_an_attempt()
    {
        var item = Item(); item.RelocationAttempts = 1; item.LastRelocationLookupAt = Now;
        var relocation = new DownloadRelocation(() => throw new Exception("must not read config"), (_, _) => throw new Exception("must not probe"));
        var result = await relocation.ResolveAsync(item, Now.AddSeconds(30), () => throw new Exception("must not persist"), default);
        Assert.Equal(RelocationOutcome.Waiting, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(270), result.Delay);
        Assert.Equal(1, item.RelocationAttempts);
    }

    [Fact]
    public async Task Existing_root_does_not_redirect_a_missing_child()
    {
        var item = Item(); var probes = 0;
        var relocation = new DownloadRelocation(() => new ServerConfig(), (_, _) => { probes++; return Task.FromResult(true); });
        Assert.Equal(RelocationOutcome.NotApplicable, (await relocation.ResolveAsync(item, Now, () => { }, default)).Outcome);
        Assert.Equal(1, probes);
        Assert.Equal(Item().RemotePath, item.RemotePath);
    }

    [Fact]
    public async Task Probe_fault_is_not_absence_and_cancellation_does_not_change_path()
    {
        var item = Item();
        var relocation = new DownloadRelocation(() => new ServerConfig(), (_, _) => throw new IOException("disconnected"));
        await Assert.ThrowsAsync<IOException>(() => relocation.ResolveAsync(item, Now, () => { }, default));
        Assert.Equal(Item().RemotePath, item.RemotePath);
        Assert.Equal(1, item.RelocationAttempts);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relocation.ResolveAsync(item, Now.AddMinutes(5), () => { }, cts.Token));
        Assert.Equal(1, item.RelocationAttempts);
    }

    [Theory]
    [InlineData("No such file or directory", true)]
    [InlineData("File not found", true)]
    [InlineData("Permission denied", false)]
    public void Only_explicit_remote_missing_failures_trigger_relocation(string message, bool expected)
    {
        Assert.Equal(expected, DownloadRelocation.IsMissingRemotePath(new FtpCommandException("550", message)));
        Assert.False(DownloadRelocation.IsMissingRemotePath(new FileNotFoundException("local file missing")));
        Assert.True(DownloadRelocation.IsMissingRemotePath(new FileNotFoundException("Remote path does not exist or is inaccessible: /release")));
    }

    [Fact]
    public async Task Manager_retries_relocated_release_even_without_ordinary_retries()
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-relocation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = DownloadStore.ForFile(Path.Combine(root, "downloads.json"));
        var item = Item(); item.LocalPath = Path.Combine(root, "release"); store.Add(item);
        var source = new MovedSource();
        using var manager = new DownloadManager(store, source, new DownloadConfig
        {
            AutoExtract = false, MaxRetries = 0, MaxConcurrentDownloads = 1
        });
        manager.Relocation = new DownloadRelocation(() => new ServerConfig(), (path, _) => Task.FromResult(path.StartsWith("/recent/")));
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.DownloadStatusChanged += changed => { if (changed.Status is DownloadStatus.Completed or DownloadStatus.Failed) complete.TrySetResult(); };
        try
        {
            manager.Start();
            await complete.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DownloadStatus.Completed, item.Status);
            Assert.StartsWith("/recent/", item.RemotePath);
            Assert.Equal(0, item.RetryCount);
            Assert.Equal(1, item.RelocationAttempts);
            Assert.Equal(3, source.Plans);
        }
        finally
        {
            await manager.StopAsync(); manager.Dispose(); store.Flush();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MovedSource : IDownloadSource
    {
        public bool IsInCooldown => false;
        public int Plans { get; private set; }
        public Task<DownloadTreePlan> PlanAsync(string remotePath, string localPath, CancellationToken ct)
        {
            Plans++;
            if (remotePath.StartsWith("/incoming/")) throw new FileNotFoundException("Remote path does not exist or is inaccessible: " + remotePath);
            return Task.FromResult(new DownloadTreePlan(true, [], [localPath]));
        }
        public Task DownloadAsync(IReadOnlyList<FtpDownloadFile> files, IProgress<DownloadProgress>? progress, CancellationToken ct) => Task.CompletedTask;
        public Task RefreshManifestAsync(FtpDownloadFile file, CancellationToken ct) => Task.CompletedTask;
    }
}
