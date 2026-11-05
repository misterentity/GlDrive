using System.IO;
using System.IO.Compression;
using System.Text.Json;
using GlDrive.Downloads;
using GlDrive.Downloads.ArchiveExtraction;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// The extraction engine was a 2,400-line WPF window until it was pulled out into
/// <c>Downloads/ArchiveExtraction/</c>; these exercise it directly, with temp folders and a
/// fake external tool, where the older suites could only read its source.
/// </summary>
public sealed class ArchiveExtractionEngineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gldrive-engine-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── First-volume detection ──

    [Theory]
    [InlineData("movie.rar", true)]
    [InlineData("movie.part01.rar", true)]
    [InlineData("movie.part1.rar", true)]
    [InlineData("movie.part001.rar", true)]
    [InlineData("movie.zip", true)]
    [InlineData("movie.7z", true)]
    [InlineData("movie.tar.gz", true)]
    [InlineData("movie.iso", true)]
    [InlineData("movie.r00", false)]
    [InlineData("movie.r101", false)]
    [InlineData("movie.s01", false)]
    [InlineData("movie.part02.rar", false)]
    [InlineData("movie.part10.rar", false)]
    [InlineData("movie.7z.001", false)]
    [InlineData("movie.001", false)]
    [InlineData("movie.nfo", false)]
    [InlineData("movie.sfv", false)]
    [InlineData("README", false)]
    public void IsFirstVolume_queues_only_the_file_that_opens_a_set(string name, bool expected) =>
        Assert.Equal(expected, ArchiveVolumes.IsFirstVolume(Path.Combine(_root, name)));

    [Theory]
    [InlineData("movie.r05", "movie.rar")]
    [InlineData("movie.s01", "movie.rar")]
    [InlineData("movie.part07.rar", "movie.part01.rar")]
    [InlineData("movie.part007.rar", "movie.part001.rar")]
    [InlineData("movie.part01.rar", "movie.part01.rar")]
    [InlineData("movie.zip", "movie.zip")]
    [InlineData("movie.7z.001", null)]
    [InlineData("movie.nfo", null)]
    public void Any_volume_resolves_to_its_first_volume(string name, string? expected)
    {
        var resolved = ArchiveVolumes.ResolveFirstVolumePath(Path.Combine(_root, name));
        Assert.Equal(expected == null ? null : Path.Combine(_root, expected), resolved);
    }

    // ── Volume discovery ──

    [Fact]
    public void Old_style_rar_set_is_discovered_r_then_s_in_numeric_order()
    {
        var first = Touch("movie.rar");
        foreach (var name in new[] { "movie.r10", "movie.r00", "movie.r02", "movie.s00", "movie.r01", "other.r00", "movie.sfv" })
            Touch(name);

        var volumes = ArchiveVolumes.TryDiscoverRarVolumes(first);

        Assert.NotNull(volumes);
        Assert.Equal(new[] { "movie.rar", "movie.r00", "movie.r01", "movie.r02", "movie.r10", "movie.s00" },
            volumes!.Select(v => v.Name));
    }

    [Theory]
    [InlineData("single.rar")]
    [InlineData("modern.part01.rar")]
    [InlineData("archive.zip")]
    [InlineData("split.7z.001")]
    public void Discovery_declines_everything_but_an_old_style_set(string name) =>
        Assert.Null(ArchiveVolumes.TryDiscoverRarVolumes(Touch(name)));

    [Fact]
    public void Queue_item_sizes_the_whole_set()
    {
        var oldStyle = Touch("old.rar", 10);
        Touch("old.r00", 20);
        Touch("old.r01", 30);
        var modern = Touch("new.part01.rar", 5);
        Touch("new.part02.rar", 7);
        var zip = Touch("single.zip", 3);

        var oldItem = ArchiveVolumes.CreateItem(oldStyle);
        var modernItem = ArchiveVolumes.CreateItem(modern);
        var zipItem = ArchiveVolumes.CreateItem(zip);

        Assert.Equal(("RAR×3", 60L, "Queued"), (oldItem.ArchiveType, oldItem.Size, oldItem.Status));
        Assert.Equal(("RAR×2", 12L), (modernItem.ArchiveType, modernItem.Size));
        Assert.Equal(("ZIP", 3L), (zipItem.ArchiveType, zipItem.Size));
    }

    [Fact]
    public void Folder_scan_collects_first_volumes_and_recurses_only_when_asked()
    {
        Touch("a.rar"); Touch("a.r00"); Touch("b.part01.rar"); Touch("b.part02.rar");
        Touch("c.zip"); Touch("d.7z.001"); Touch("e.nfo");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Touch(Path.Combine("sub", "f.7z"));

        var flat = new List<string>();
        ArchiveVolumes.CollectArchivePaths(_root, recursive: false, flat);
        var deep = new List<string>();
        ArchiveVolumes.CollectArchivePaths(_root, recursive: true, deep);

        Assert.Equal(new[] { "a.rar", "b.part01.rar", "c.zip" }, flat.Select(Path.GetFileName).Order());
        Assert.Equal(new[] { "a.rar", "b.part01.rar", "c.zip", "f.7z" }, deep.Select(Path.GetFileName).Order());
    }

    // ── Extraction and fallback selection ──

    [Fact]
    public async Task Readable_archive_extracts_without_touching_the_external_tool()
    {
        var zip = Zip("good.zip", ("inner/readme.txt", "hello"));
        var tool = new FakeExternalTool(_ => true);
        using var engine = Engine(tool);
        engine.AddPaths([zip]);

        await engine.ExtractAllAsync();

        var item = Assert.Single(engine.Archives);
        Assert.Equal("Done", item.Status);
        Assert.Equal(100, item.Progress);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_root, "inner", "readme.txt")));
        Assert.Empty(tool.Calls);
    }

    [Fact]
    public async Task SharpCompress_failure_falls_back_to_the_external_tool()
    {
        var broken = BrokenRar("broken.rar");
        var tool = new FakeExternalTool(call =>
        {
            File.WriteAllText(Path.Combine(call.OutputDir, "from-tool.txt"), "ok");
            return true;
        });
        using var engine = Engine(tool);
        engine.OverwriteMode = 2;
        engine.Password = "secret";
        engine.AddPaths([broken]);

        await engine.ExtractAllAsync();

        var call = Assert.Single(tool.Calls);
        Assert.Equal((broken, _root, "secret", 2), (call.ArchivePath, call.OutputDir, call.Password, call.OverwriteMode));
        Assert.Equal("Done", engine.Archives[0].Status);
        Assert.True(File.Exists(Path.Combine(_root, "from-tool.txt")));
    }

    [Fact]
    public async Task No_external_tool_falls_through_to_the_streaming_reader_and_reports_the_error()
    {
        var broken = BrokenRar("broken.rar");
        var tool = new FakeExternalTool(_ => false);
        using var engine = Engine(tool);
        engine.AddPaths([broken]);

        await engine.ExtractAllAsync();

        Assert.Single(tool.Calls);
        var item = engine.Archives[0];
        Assert.Equal("Error", item.Status);
        Assert.False(string.IsNullOrEmpty(item.ErrorMessage));
    }

    /// <summary>
    /// The fallback is for archives SharpCompress recognizes but cannot read (the scene
    /// multi-volume "Unknown Rar Header" family). A file it cannot even identify is an error
    /// straight away — no external tool is launched against it.
    /// </summary>
    [Fact]
    public async Task Unidentifiable_file_is_an_error_without_trying_the_external_tool()
    {
        var tool = new FakeExternalTool(_ => true);
        using var engine = Engine(tool);
        engine.AddPaths([Touch("noise.rar", 64)]);

        await engine.ExtractAllAsync();

        Assert.Equal("Error", engine.Archives[0].Status);
        Assert.Empty(tool.Calls);
    }

    [Fact]
    public async Task Cancelling_a_batch_cancels_the_running_archive_and_stops_the_queue()
    {
        var started = new ManualResetEventSlim();
        var tool = new FakeExternalTool(call =>
        {
            started.Set();
            call.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            call.Token.ThrowIfCancellationRequested();
            return true;
        });
        using var engine = Engine(tool);
        engine.AddPaths([BrokenRar("first.rar"), BrokenRar("second.rar")]);

        var batch = engine.ExtractAllAsync();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "The external tool never started.");
        Assert.True(engine.IsExtracting);
        await engine.ExtractAllAsync(); // While running, a second start is the Cancel button.
        await batch.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(engine.IsExtracting);
        Assert.Equal(new[] { "Cancelled", "Queued" }, engine.Archives.Select(a => a.Status));
        Assert.Single(tool.Calls);
    }

    [Fact]
    public async Task Batch_reports_progress_and_extracting_state_through_events()
    {
        var zip = Zip("one.zip", ("a.txt", "a"));
        using var engine = Engine(new FakeExternalTool(_ => true));
        var extracting = new List<bool>();
        var progress = new List<double>();
        engine.ExtractingChanged += extracting.Add;
        engine.BatchProgressChanged += progress.Add;
        engine.AddPaths([zip]);

        await engine.ExtractAllAsync();

        Assert.Equal(new[] { true, false }, extracting);
        Assert.Equal(new[] { 100d }, progress);
    }

    [Fact]
    public void Staged_output_honours_skip_rename_and_overwrite()
    {
        var output = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        File.WriteAllText(Path.Combine(output, "keep.txt"), "old");

        foreach (var (mode, expectOld, expectRenamed) in new[] { (1, true, false), (2, true, true), (0, false, false) })
        {
            using var staging = new ExternalArchiveTool.ExtractionStagingDirectory(output);
            File.WriteAllText(Path.Combine(staging.Path, "keep.txt"), "new");

            ExternalArchiveTool.CommitStagedOutput(staging.Path, output, mode, CancellationToken.None);

            Assert.Equal(expectOld ? "old" : "new", File.ReadAllText(Path.Combine(output, "keep.txt")));
            Assert.Equal(expectRenamed, File.Exists(Path.Combine(output, "keep (1).txt")));
            File.Delete(Path.Combine(output, "keep (1).txt"));
        }

        Assert.Empty(Directory.GetDirectories(output)); // Every staging directory removed itself.
    }

    [Theory]
    [InlineData(0, false, "")]
    [InlineData(0, true, "Release")]
    [InlineData(1, false, "Release")]
    [InlineData(2, false, "custom")]
    [InlineData(2, true, "custom/Release")]
    public void Output_folder_follows_the_output_mode(int mode, bool subfolder, string expected)
    {
        using var engine = Engine(new FakeExternalTool(_ => true));
        engine.OutputMode = mode;
        engine.CreateSubfolder = subfolder;
        engine.CustomOutputPath = Path.Combine(_root, "custom");
        var item = new ArchiveItem { FilePath = Path.Combine(_root, "Release.tar.gz") };

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, expected)), Path.GetFullPath(engine.GetOutputDir(item)));
    }

    // ── Abandon records ──

    [Fact]
    public async Task Abandon_record_round_trips_through_the_engine_and_a_success_retires_it()
    {
        var zip = Zip("repaired.zip", ("a.txt", "a"));
        var storePath = Path.Combine(_root, "extract-abandoned.json");
        var (volumes, bytes) = ArchiveVolumes.ComputeVolumeSetFingerprint(zip);
        new ExtractAbandonStore(storePath).Record(zip, volumes, bytes, "unrecoverable (test)");

        var store = new ExtractAbandonStore(storePath);
        store.Load();
        using var engine = Engine(new FakeExternalTool(_ => true), store);
        Assert.True(engine.SkipIfAbandoned(zip), "The engine must honour a verdict recorded by a previous process.");

        engine.AddPaths([zip]);
        await engine.ExtractAllAsync();

        var reloaded = new ExtractAbandonStore(storePath);
        reloaded.Load();
        Assert.False(reloaded.ShouldSkip(zip, volumes, bytes), "A successful extraction must forget the durable verdict.");
    }

    // ── Persisted settings ──

    [Fact]
    public void Settings_file_keeps_its_name_and_camelCase_shape()
    {
        var watch = Directory.CreateDirectory(Path.Combine(_root, "watch")).FullName;
        using (var engine = SettingsEngine(startWatchers: false))
        {
            engine.ApplySettings(new ExtractorSettings
            {
                WatchFolders = [watch], WatchEnabled = false, DeleteAfterExtract = true, ScanSubfolders = false,
                CreateSubfolder = true, OutputMode = 2, OverwriteMode = 1, CustomOutputPath = @"D:\out"
            });
        }

        var json = File.ReadAllText(Path.Combine(_root, "extractor-settings.json"));
        var expected = JsonSerializer.Serialize(new
        {
            watchFolders = new[] { watch }, watchEnabled = false, deleteAfterExtract = true, scanSubfolders = false,
            createSubfolder = true, outputMode = 2, overwriteMode = 1, customOutputPath = @"D:\out"
        }, new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(expected, json);
    }

    [Fact]
    public void Saved_settings_load_and_an_automation_engine_defers_saved_watchers()
    {
        var watch = Directory.CreateDirectory(Path.Combine(_root, "watch")).FullName;
        File.WriteAllText(Path.Combine(_root, "extractor-settings.json"), JsonSerializer.Serialize(new
        {
            watchFolders = new[] { watch, Path.Combine(_root, "gone") }, watchEnabled = true, deleteAfterExtract = true,
            scanSubfolders = false, createSubfolder = true, outputMode = 1, overwriteMode = 2, customOutputPath = @"E:\x"
        }));

        using (var engine = SettingsEngine(startWatchers: false))
        {
            Assert.Equal(new[] { watch }, engine.WatchFolders);
            Assert.False(engine.WatchEnabled);
            Assert.True(engine.WatchResumeDeferred);
            Assert.Equal((true, false, true, 1, 2, @"E:\x"),
                (engine.DeleteAfterExtract, engine.ScanSubfolders, engine.CreateSubfolder, engine.OutputMode, engine.OverwriteMode, engine.CustomOutputPath));
        }

        // Disposing saved, and the deferred preference survived the save.
        var saved = JsonSerializer.Deserialize<ExtractorSettings>(
            File.ReadAllText(Path.Combine(_root, "extractor-settings.json")), ArchiveExtractionEngine.JsonOptions)!;
        Assert.True(saved.WatchEnabled);
        Assert.Equal(new[] { watch }, saved.WatchFolders);
    }

    [Fact]
    public void Legacy_watch_folder_list_is_migrated()
    {
        var watch = Directory.CreateDirectory(Path.Combine(_root, "watch")).FullName;
        File.WriteAllText(Path.Combine(_root, "watch-folders.json"), JsonSerializer.Serialize(new[] { watch }));

        using var engine = SettingsEngine(startWatchers: false);

        Assert.Equal(new[] { watch }, engine.WatchFolders);
        Assert.False(File.Exists(Path.Combine(_root, "watch-folders.json")));
        Assert.True(File.Exists(Path.Combine(_root, "extractor-settings.json")));
    }

    [Fact]
    public void Watch_cannot_be_enabled_without_folders_and_saves_nothing()
    {
        using var engine = SettingsEngine(startWatchers: false);

        Assert.False(engine.ToggleWatch());
        Assert.False(engine.WatchEnabled);
        Assert.False(File.Exists(Path.Combine(_root, "extractor-settings.json")));
    }

    // ── Helpers ──

    private ArchiveExtractionEngine Engine(IExternalArchiveTool tool, ExtractAbandonStore? store = null) => new(null, new ExtractorEngineOptions
    {
        DemoMode = true,
        SettingsDirectory = _root,
        ExternalTool = tool,
        AbandonStore = store ?? new ExtractAbandonStore(Path.Combine(_root, "extract-abandoned.json")),
        DownloadGuard = new DownloadExtractionGuard(_root, () => []),
    });

    private ArchiveExtractionEngine SettingsEngine(bool startWatchers) => new(null, new ExtractorEngineOptions
    {
        StartWatchers = startWatchers,
        SettingsDirectory = _root,
        ExternalTool = new FakeExternalTool(_ => true),
        AbandonStore = new ExtractAbandonStore(Path.Combine(_root, "extract-abandoned.json")),
        DownloadGuard = new DownloadExtractionGuard(_root, () => []),
    });

    private string Touch(string name, int bytes = 1)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Enumerable.Range(0, bytes).Select(i => (byte)(i * 37 + 11)).ToArray());
        return path;
    }

    /// <summary>A RAR signature followed by garbage: identified as RAR, unreadable as one.</summary>
    private string BrokenRar(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, .. Enumerable.Range(0, 64).Select(i => (byte)(i * 37 + 11))]);
        return path;
    }

    private string Zip(string name, params (string Entry, string Text)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write(text);
        }
        return path;
    }

    private sealed record ToolCall(string ArchivePath, string OutputDir, string Password, int OverwriteMode, CancellationToken Token);

    private sealed class FakeExternalTool(Func<ToolCall, bool> behaviour) : IExternalArchiveTool
    {
        private readonly List<ToolCall> _calls = [];

        public IReadOnlyList<ToolCall> Calls
        {
            get { lock (_calls) return _calls.ToList(); }
        }

        public bool TryExtract(string archivePath, string outputDir, string password, int overwriteMode,
            IProgress<ArchiveProgress> progress, CancellationToken ct)
        {
            var call = new ToolCall(archivePath, outputDir, password, overwriteMode, ct);
            lock (_calls) _calls.Add(call);
            return behaviour(call);
        }
    }
}
