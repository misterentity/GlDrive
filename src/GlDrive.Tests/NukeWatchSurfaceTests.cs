using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using GlDrive.Library;
using GlDrive.Services.Automation;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public sealed class NukeWatchSurfaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-nuke-surface-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Owned = "Surface.Film.2026.1080p.WEB.H264-ALPHA";
    private const string Fix = "Surface.Film.2026.1080p.PROPER.WEB.H264-BETA";

    public NukeWatchSurfaceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"downloads\":{\"qualityDefault\":\"2160p\"}}")]
    [InlineData("{\"nukeWatch\":{}}")]
    public void Compatible_defaults_are_passive_and_never_enable_correction_acquisition(string json)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(json, Json)!;
        Assert.True(config.NukeWatch.Enabled);
        Assert.False(config.NukeWatch.AutoQueueFixes);
        Assert.Equal(7, config.NukeWatch.WatchDays);
        Assert.Equal(2048, config.NukeWatch.HistoryCapacity);
        Assert.Equal(256, config.NukeWatch.QueueCapacity);
        ConfigurationAutomation.Validate(config);
    }

    [Theory]
    [InlineData("watchDays", 1, 90)]
    [InlineData("historyCapacity", 1, 10000)]
    [InlineData("queueCapacity", 1, 4096)]
    public void Published_schema_exposes_the_same_validated_ranges(string property, int minimum, int maximum)
    {
        var schema = JsonSerializer.SerializeToNode(new ConfigurationAutomation(new(), _ => { }).Schema())!;
        var field = schema["schema"]!["properties"]!["nukeWatch"]!["properties"]![property]!;
        Assert.Equal(minimum, field["minimum"]!.GetValue<int>());
        Assert.Equal(maximum, field["maximum"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("watchDays", 0)]
    [InlineData("watchDays", 91)]
    [InlineData("historyCapacity", 0)]
    [InlineData("historyCapacity", 10001)]
    [InlineData("queueCapacity", 0)]
    [InlineData("queueCapacity", 4097)]
    public void Out_of_range_patch_is_rejected_before_any_persistence_or_partial_mutation(string property, int value)
    {
        var config = new AppConfig();
        var before = JsonSerializer.Serialize(config);
        var saves = 0;
        var module = new ConfigurationAutomation(config, _ => saves++);
        var patch = new JsonArray(
            new JsonObject { ["op"] = "replace", ["path"] = "/logging/level", ["value"] = "Debug" },
            new JsonObject { ["op"] = "replace", ["path"] = "/nukeWatch/" + property, ["value"] = value });
        Assert.Throws<ArgumentException>(() => module.Apply(patch));
        Assert.Equal(0, saves);
        Assert.Equal(before, JsonSerializer.Serialize(config));
    }

    [Theory]
    [InlineData(true, false, 7)]
    [InlineData(false, true, 90)]
    public void Settings_roundtrip_keeps_explicit_choices_without_reading_or_saving_credentials(bool enabled, bool autoQueue, int days)
    {
        var config = new AppConfig { NukeWatch = new() { Enabled = enabled, AutoQueueFixes = autoQueue, WatchDays = days } };
        var editor = new SettingsViewModel(config, readLocalState: false);
        Assert.Equal(enabled, editor.NukeWatchEnabled);
        Assert.Equal(autoQueue, editor.NukeWatchAutoQueueFixes);
        Assert.Equal(days.ToString(), editor.NukeWatchDays);
        var destination = new AppConfig();
        var downloads = JsonSerializer.Serialize(destination.Downloads);
        editor.ApplyNukeWatchSettings(destination);
        Assert.Equal(enabled, destination.NukeWatch.Enabled);
        Assert.Equal(autoQueue, destination.NukeWatch.AutoQueueFixes);
        Assert.Equal(days, destination.NukeWatch.WatchDays);
        Assert.Equal(downloads, JsonSerializer.Serialize(destination.Downloads));
        ConfigurationAutomation.Validate(destination);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("91", 90)]
    [InlineData("unknown", 7)]
    public void Settings_applies_retention_with_bounded_default_without_changing_passive_capacities(string text, int expected)
    {
        var config = new AppConfig { NukeWatch = new() { HistoryCapacity = 81, QueueCapacity = 19 } };
        var editor = new SettingsViewModel(config, readLocalState: false)
        { NukeWatchEnabled = false, NukeWatchAutoQueueFixes = true, NukeWatchDays = text };
        editor.ApplyNukeWatchSettings(config);
        Assert.False(config.NukeWatch.Enabled);
        Assert.True(config.NukeWatch.AutoQueueFixes);
        Assert.Equal(expected, config.NukeWatch.WatchDays);
        Assert.Equal(81, config.NukeWatch.HistoryCapacity);
        Assert.Equal(19, config.NukeWatch.QueueCapacity);
    }

    [Fact]
    public async Task Automation_is_read_only_paged_and_does_not_request_sources_or_enqueue_work()
    {
        var config = new AppConfig();
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var path = Path.Combine(_root, "watch.json");
        var store = new NukeWatchStore(path, () => config.NukeWatch, () => now);
        for (var index = 0; index < 3; index++)
            store.ObserveOwned(new($"Surface.Film.{index}.2026.1080p.WEB.H264-ALPHA",
                Path.Combine(_root, "library", $"film-{index}.mkv"), now.AddMinutes(-index - 1)));
        var before = File.ReadAllText(path);
        using var watch = new NukeWatch(config, store,
            () => throw new InvalidOperationException("Reading flags must not enumerate downloads"),
            () => throw new InvalidOperationException("Reading flags must not enumerate source facts"),
            _ => throw new InvalidOperationException("Reading flags must not query wishlist"),
            (_, _) => throw new InvalidOperationException("Reading flags must not enqueue a correction"));
        try
        {
            var module = new NukeWatchAutomation(config, () => watch);
            var tool = Assert.Single(module.Tools);
            Assert.Equal("library.nuke_watch", tool.Name);
            Assert.True(tool.ReadOnly);
            Assert.False(tool.Destructive);
            Assert.Equal(500, tool.InputSchema.GetProperty("properties").GetProperty("limit").GetProperty("maximum").GetInt32());
            var page = JsonSerializer.SerializeToNode(await module.ExecuteAsync(tool.Name,
                JsonSerializer.SerializeToElement(new { offset = 1, limit = 1 }), default), Json)!;
            Assert.True(page["enabled"]!.GetValue<bool>());
            Assert.False(page["autoQueueFixes"]!.GetValue<bool>());
            Assert.Equal(3, page["total"]!.GetValue<int>());
            Assert.Equal(1, page["offset"]!.GetValue<int>());
            Assert.Equal("Surface.Film.1.2026.1080p.WEB.H264-ALPHA",
                Assert.Single(page["entries"]!.AsArray())!["owned"]!["releaseName"]!.GetValue<string>());
            var empty = JsonSerializer.SerializeToNode(await module.ExecuteAsync(tool.Name,
                JsonSerializer.SerializeToElement(new { offset = 99, limit = 1 }), default), Json)!;
            Assert.Empty(empty["entries"]!.AsArray());
            Assert.Equal(3, empty["total"]!.GetValue<int>());
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally { watch.Dispose(); await watch.DrainAsync(); }
    }

    [Theory]
    [InlineData(DownloadStatus.Failed)]
    [InlineData(DownloadStatus.Cancelled)]
    public void Correction_provenance_survives_actual_download_store_json_for_terminal_attempts(DownloadStatus status)
    {
        var path = Path.Combine(_root, "downloads-synthetic.json");
        var store = DownloadStore.ForFile(path);
        DownloadStore? reloaded = null;
        try
        {
            var item = new DownloadItem
            {
                ReleaseName = Fix, RemotePath = "/movies/" + Fix, LocalPath = Path.Combine(_root, "staging", Fix),
                Status = status, NukeCorrectionForRelease = Owned,
                QueuedAt = new DateTime(2026, 10, 28, 10, 0, 0, DateTimeKind.Utc)
            };
            store.Add(item);
            store.Flush();
            using (var saved = JsonDocument.Parse(File.ReadAllText(path)))
                Assert.Equal(Owned, saved.RootElement[0].GetProperty("nukeCorrectionForRelease").GetString());
            reloaded = DownloadStore.ForFile(path);
            reloaded.Load();
            var restored = Assert.Single(reloaded.Items);
            Assert.Equal(item.Id, restored.Id);
            Assert.Equal(status, restored.Status);
            Assert.Equal(Owned, restored.NukeCorrectionForRelease);
            Assert.Equal(Fix, restored.ReleaseName);
            Assert.Equal(item.QueuedAt, restored.QueuedAt);
        }
        finally { store.Flush(); reloaded?.Flush(); }
    }

    [Fact]
    public async Task Correction_admission_refuses_unstarted_stopped_and_disposed_managers()
    {
        var store = DownloadStore.ForFile(Path.Combine(_root, "downloads-lifecycle.json"));
        var manager = new DownloadManager(store, new NoNetworkSource(), new DownloadConfig());
        var eligibilityCalls = 0;
        bool Eligible() { eligibilityCalls++; return true; }
        try
        {
            Assert.False(manager.TryEnqueueCorrection(Correction(), Eligible));
            manager.Start();
            await manager.StopAsync();
            Assert.False(manager.TryEnqueueCorrection(Correction(), Eligible));
            manager.Dispose();
            Assert.False(manager.TryEnqueueCorrection(Correction(), Eligible));
            Assert.Equal(0, eligibilityCalls);
            Assert.Empty(store.Items);
        }
        finally { manager.Dispose(); store.Flush(); }
    }

    [Fact]
    public async Task Running_manager_rechecks_correction_eligibility_before_queueing()
    {
        var store = DownloadStore.ForFile(Path.Combine(_root, "downloads-eligibility.json"));
        using var manager = new DownloadManager(store, new NoNetworkSource(), new DownloadConfig());
        try
        {
            manager.Start();
            var calls = 0;
            Assert.False(manager.TryEnqueueCorrection(Correction(), () => { calls++; return false; }));
            Assert.Equal(1, calls);
            Assert.Empty(store.Items);
        }
        finally { await manager.StopAsync(); store.Flush(); }
    }

    [Fact]
    public void Ordinary_enqueue_remains_available_before_manager_start()
    {
        var store = DownloadStore.ForFile(Path.Combine(_root, "downloads-ordinary.json"));
        using var manager = new DownloadManager(store, new NoNetworkSource(), new DownloadConfig());
        try
        {
            var ordinary = Correction();
            ordinary.NukeCorrectionForRelease = null;
            Assert.True(manager.Enqueue(ordinary));
            Assert.Equal(ordinary.Id, Assert.Single(store.Items).Id);
        }
        finally { store.Flush(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Import_observer_receives_only_verified_destination_and_failure_never_voids_import(bool dryRun, bool observerFails)
    {
        var staging = Path.Combine(_root, "stage");
        Directory.CreateDirectory(staging);
        var source = Path.Combine(staging, "movie.mkv");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        var config = new AppConfig
        {
            Library = new() { Enabled = true, DryRun = dryRun, StagingRoot = staging,
                MovieRoot = Path.Combine(_root, "movies"), TvRoot = Path.Combine(_root, "tv") }
        };
        var observations = new List<OwnedReleaseObservation>();
        var module = new LibraryAutomation(config,
            _ => throw new InvalidOperationException("Import observer must not initiate Plex work"),
            new SafeLibraryImporter(_ => { }), ownedObserved: observation =>
            {
                observations.Add(observation);
                if (observerFails) throw new IOException("Synthetic passive observer failure");
            });
        var result = JsonSerializer.SerializeToNode(await module.ExecuteAsync("library.import",
            JsonSerializer.SerializeToElement(new { sourcePath = source, releaseName = Owned }), default), Json)!;
        Assert.Equal(!dryRun, result["import"]!["verified"]!.GetValue<bool>());
        Assert.Equal(dryRun, File.Exists(source));
        if (dryRun)
        {
            Assert.Empty(observations);
            Assert.False(Directory.Exists(config.Library.MovieRoot));
        }
        else
        {
            var observed = Assert.Single(observations);
            Assert.Equal(Owned, observed.ReleaseName);
            Assert.Equal(result["import"]!["destinationPath"]!.GetValue<string>(), observed.LocalPath);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(observed.LocalPath));
        }
    }

    [Fact]
    public async Task Library_index_flags_match_exact_file_or_containing_directory_without_prefix_leak()
    {
        var movies = Path.Combine(_root, "movies");
        var directory = Path.Combine(movies, "owned");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, Owned + ".mkv");
        File.WriteAllBytes(file, [5, 6, 7]);
        var config = new AppConfig { Library = new() { MovieRoot = movies, TvRoot = Path.Combine(_root, "tv") } };
        Directory.CreateDirectory(config.Library.TvRoot);
        OwnedNukeEntry Flag(string release, string path, bool flagged = true) =>
            new(new(release, path, DateTime.UtcNow), flagged, true, "corrupt", ["predb"], []);
        var flags = new[]
        {
            Flag("Exact.2026.1080p.WEB-ALPHA", file.ToUpperInvariant()),
            Flag("Folder.2026.1080p.WEB-ALPHA", directory),
            Flag("Prefix.2026.1080p.WEB-ALPHA", Path.Combine(movies, "own")),
            Flag("Inactive.2026.1080p.WEB-ALPHA", file, flagged: false)
        };
        var module = new LibraryAutomation(config, _ => null, nukeWatch: () => flags);
        await module.ExecuteAsync("library.index_refresh", JsonSerializer.SerializeToElement(new { }), default);
        var result = JsonSerializer.SerializeToNode(await module.ExecuteAsync("library.index",
            JsonSerializer.SerializeToElement(new { }), default), Json)!;
        Assert.Single(result["entries"]!.AsArray());
        var shown = result["nukeFlags"]!.AsArray().Select(row => row!["owned"]!["releaseName"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "Exact.2026.1080p.WEB-ALPHA", "Folder.2026.1080p.WEB-ALPHA" }, shown);
        Assert.Equal(new byte[] { 5, 6, 7 }, File.ReadAllBytes(file));
    }

    private DownloadItem Correction() => new()
    {
        ReleaseName = Fix, NukeCorrectionForRelease = Owned,
        RemotePath = "/movies/" + Fix, LocalPath = Path.Combine(_root, "staging", Fix)
    };

    private sealed class NoNetworkSource : IDownloadSource
    {
        public bool IsInCooldown => false;
        public Task<DownloadTreePlan> PlanAsync(string remotePath, string localPath, CancellationToken ct) =>
            throw new InvalidOperationException("No source work is authorized by this test");
        public Task DownloadAsync(IReadOnlyList<FtpDownloadFile> files, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
            throw new InvalidOperationException("No source work is authorized by this test");
        public Task RefreshManifestAsync(FtpDownloadFile file, CancellationToken ct) =>
            throw new InvalidOperationException("No source work is authorized by this test");
    }
}
