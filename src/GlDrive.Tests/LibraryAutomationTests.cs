using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Library;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class LibraryAutomationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-library-automation-" + Guid.NewGuid().ToString("N"));
    private readonly AppConfig _config;
    private readonly string _source;
    public LibraryAutomationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "stage"));
        _source = Path.Combine(_root, "stage", "movie.mkv");
        File.WriteAllBytes(_source, [1, 2, 3, 4]);
        _config = new AppConfig { Library = new() { StagingRoot = Path.Combine(_root, "stage"), MovieRoot = Path.Combine(_root, "movies"), TvRoot = Path.Combine(_root, "tv") } };
    }
    public void Dispose() => Directory.Delete(_root, true);
    private JsonElement Arguments() => JsonSerializer.SerializeToElement(new { sourcePath = _source, releaseName = "Example.Movie.2026.1080p.WEB-DL" });

    [Fact]
    public async Task Read_only_plan_is_available_while_disabled_without_network_or_writes()
    {
        var module = new LibraryAutomation(_config, _ => throw new InvalidOperationException("Network must not be reached"));
        var result = JsonSerializer.SerializeToNode(await module.ExecuteAsync("library.plan", Arguments(), default))!;
        Assert.Contains("Example Movie (2026)", result["DestinationPath"]!.GetValue<string>());
        Assert.True(File.Exists(_source));
        Assert.False(Directory.Exists(_config.Library.MovieRoot));
    }

    [Fact]
    public async Task Disabled_import_refuses_all_file_mutation()
    {
        var module = new LibraryAutomation(_config);
        await Assert.ThrowsAsync<InvalidOperationException>(() => module.ExecuteAsync("library.import", Arguments(), default));
        Assert.True(File.Exists(_source));
        Assert.False(Directory.Exists(_config.Library.MovieRoot));
    }

    [Fact]
    public async Task Dry_run_never_scans_Plex_even_if_scan_setting_is_true()
    {
        _config.Library.Enabled = true;
        _config.Library.ScanPlexAfterImport = true;
        var module = new LibraryAutomation(_config, _ => throw new InvalidOperationException("Plex must not be reached"));
        var result = JsonSerializer.SerializeToNode(await module.ExecuteAsync("library.import", Arguments(), default))!;
        Assert.Equal("dry-run", result["import"]!["Status"]!.GetValue<string>());
        Assert.Equal("disabled", result["plexScan"]!.GetValue<string>());
        Assert.True(File.Exists(_source));
        Assert.False(Directory.Exists(_config.Library.MovieRoot));
    }

    [Fact]
    public async Task Plex_scan_failure_keeps_verified_import_successful_and_destination_intact()
    {
        _config.Library.Enabled = true;
        _config.Library.DryRun = false;
        _config.Library.ScanPlexAfterImport = true;
        var module = new LibraryAutomation(_config, _ => throw new IOException("synthetic provider failure"), new SafeLibraryImporter(_ => { }));
        var result = JsonSerializer.SerializeToNode(await module.ExecuteAsync("library.import", Arguments(), default))!;
        Assert.True(result["import"]!["Verified"]!.GetValue<bool>());
        Assert.Equal("failed-after-import", result["plexScan"]!.GetValue<string>());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(result["import"]!["DestinationPath"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("copy-chunk", "disabled")]
    [InlineData("copy-chunk", "dry-run")]
    [InlineData("copy-chunk", "root")]
    [InlineData("verified", "disabled")]
    [InlineData("verified", "dry-run")]
    [InlineData("verified", "root")]
    [InlineData("before-rename", "disabled")]
    public async Task In_flight_import_rechecks_live_permission_before_source_removal(string checkpoint, string change)
    {
        _config.Library.Enabled = true;
        _config.Library.DryRun = false;
        var plan = LibraryPlanner.Plan(_config.Library, _source, "Example.Movie.2026.1080p.WEB-DL");
        var importer = new SafeLibraryImporter(_ => { })
        {
            ForceCopyForTests = checkpoint != "before-rename",
            Checkpoint = step =>
            {
                if (step != checkpoint) return;
                if (change == "disabled") _config.Library.Enabled = false;
                else if (change == "dry-run") _config.Library.DryRun = true;
                else _config.Library.MovieRoot = Path.Combine(_root, "new-movies");
            }
        };
        var module = new LibraryAutomation(_config, _ => throw new InvalidOperationException("Plex must not be reached"), importer);
        await Assert.ThrowsAsync<IOException>(() => module.ExecuteAsync("library.import", Arguments(), default));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(_source));
        Assert.Equal(checkpoint == "verified", File.Exists(plan.DestinationPath));
        Assert.Empty(Directory.GetFiles(plan.LibraryRoot, ".*.gldrive-tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Index_and_lookup_require_explicit_refresh_and_never_trigger_network_implicitly()
    {
        var module = new LibraryAutomation(_config, _ => throw new InvalidOperationException("Network must not be reached"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => module.ExecuteAsync("library.index", JsonSerializer.SerializeToElement(new { }), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => module.ExecuteAsync("library.lookup", JsonSerializer.SerializeToElement(new { releaseName = "Example.Movie.2026.1080p" }), default));
    }

    [Fact]
    public async Task Unavailable_Plex_retains_read_only_disk_results_and_reports_incomplete_evidence()
    {
        Directory.CreateDirectory(_config.Library.MovieRoot);
        Directory.CreateDirectory(_config.Library.TvRoot);
        var owned = Path.Combine(_config.Library.MovieRoot, "Example.Movie.2026.2160p.mkv");
        File.WriteAllBytes(owned, [5, 6, 7]);
        var module = new LibraryAutomation(_config, _ => throw new IOException("synthetic Plex read failure"));
        var refreshed = JsonSerializer.SerializeToNode(await module.ExecuteAsync("library.index_refresh", JsonSerializer.SerializeToElement(new { }), default))!;
        Assert.False(refreshed["Complete"]!.GetValue<bool>());
        Assert.Equal("incomplete", refreshed["plex"]!.GetValue<string>());
        Assert.Equal(1, refreshed["total"]!.GetValue<int>());
        var lookup = JsonSerializer.SerializeToNode(await module.ExecuteAsync("library.lookup", JsonSerializer.SerializeToElement(new { releaseName = "Example.Movie.2026.1080p" }), default))!;
        Assert.False(lookup["comparison"]!["CompleteEvidence"]!.GetValue<bool>());
        Assert.True(lookup["comparison"]!["MeetsCutoff"]!.GetValue<bool>());
        Assert.Equal(new byte[] { 5, 6, 7 }, File.ReadAllBytes(owned));
        Assert.False(_config.Library.Enabled);
    }
}
