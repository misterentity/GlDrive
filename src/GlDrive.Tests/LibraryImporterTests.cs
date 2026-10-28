using System.IO;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Library;
using Xunit;

namespace GlDrive.Tests;

public sealed class LibraryImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-library-" + Guid.NewGuid().ToString("N"));
    private const string Movie = "Synthetic.Film.2024.1080p.WEB-TEST";
    private readonly LibraryConfig _config;

    public LibraryImporterTests()
    {
        _config = new LibraryConfig { Enabled = true, DryRun = false,
            StagingRoot = Path.Combine(_root, "staging"), MovieRoot = Path.Combine(_root, "movies"),
            TvRoot = Path.Combine(_root, "shows") };
        Directory.CreateDirectory(_config.StagingRoot);
    }

    private string Source(string name = "fixture.mkv", string content = "synthetic video bytes")
    {
        var path = Path.Combine(_config.StagingRoot, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Defaults_leave_library_disabled_and_dry_run()
    {
        var config = new AppConfig().Library;
        Assert.False(config.Enabled);
        Assert.True(config.DryRun);
        Assert.False(config.ScanPlexAfterImport);
        Assert.Empty(config.StagingRoot);
        Assert.Empty(config.MovieRoot);
        Assert.Empty(config.TvRoot);
    }

    [Fact]
    public void Movie_plan_uses_plex_title_year_and_retains_quality()
    {
        var plan = LibraryPlanner.Plan(_config, Source(), Movie);
        Assert.Equal(Path.Combine(_config.MovieRoot, "Synthetic Film (2024)", "Synthetic Film (2024) - 1080p.mkv"), plan.DestinationPath);
        Assert.Equal(new LibraryMediaIdentity("Synthetic Film", 2024, null, null, QualityProfile.Q1080p), plan.Identity);
        Assert.False(Directory.Exists(_config.MovieRoot));
    }

    [Fact]
    public void Episode_plan_uses_season_and_episode_names()
    {
        var plan = LibraryPlanner.Plan(_config, Source(), "Synthetic.Show.S02E03.2160p.WEB-TEST");
        Assert.Equal(Path.Combine(_config.TvRoot, "Synthetic Show", "Season 02", "Synthetic Show - S02E03 - 2160p.mkv"), plan.DestinationPath);
        Assert.Equal("episode", plan.Identity.Kind);
    }

    [Theory]
    [InlineData("HDCAMRIP", "CAM")]
    [InlineData("HDTSRIP", "TS")]
    [InlineData("HDTCRIP", "TC")]
    public async Task Imported_low_source_evidence_survives_normalized_name_and_index_refresh(string source, string retained)
    {
        var release = $"Synthetic.Film.2024.1080p.{source}-TEST";
        var plan = LibraryPlanner.Plan(_config, Source(), release);
        Assert.Contains(" - " + retained + " - 1080p", plan.DestinationPath);
        var imported = await new SafeLibraryImporter(_ => { }).ImportAsync(plan, _config);
        Assert.True(imported.Verified);
        var snapshot = new LibraryIndex().Refresh([_config.MovieRoot], []);
        var entry = Assert.Single(snapshot.Entries);
        Assert.True(WishlistSourceFloor.IsLowQualityLibrary(entry));
        Assert.Equal("upgrade", LibraryIndex.Lookup(snapshot, plan.Identity).Status);
        var movie = new WishlistItem { Title = "Synthetic Film", Year = 2024, Quality = QualityProfile.Q1080p, AllowLowQualitySources = true };
        Assert.False(WishlistOwnership.IsOwned(movie, new(), snapshot, [], DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("Synthetic.Film.2024.1080p.WEB-TEST")]
    [InlineData("Synthetic.Show.2024.S02E03.480p.WEB-TEST")]
    public void Planned_names_round_trip_through_read_only_identity_parser(string release)
    {
        var plan = LibraryPlanner.Plan(_config, Source(), release);
        Assert.Equal(plan.Identity, LibraryPlanner.TryIdentity(Path.GetFileNameWithoutExtension(plan.DestinationPath)));
    }

    [Theory]
    [InlineData("Synthetic.Show.S02.1080p.WEB-TEST")]
    [InlineData("Synthetic.Show.S02E03E04.1080p.WEB-TEST")]
    [InlineData("Synthetic.Show.S02E03-E04.1080p.WEB-TEST")]
    [InlineData("Synthetic.Show.S02E03.S02E04.1080p.WEB-TEST")]
    [InlineData("Synthetic.Film.1080p.WEB-TEST")]
    [InlineData("2024.1080p.WEB-TEST")]
    [InlineData("Synthetic.Daily.2026.10.28.Guest.1080p.WEB-TEST")]
    [InlineData("Synthetic.Sports.2026.10.26.TeamA.vs.TeamB.1080p.WEB-TEST")]
    public void Ambiguous_names_stay_staged(string release) => Assert.Null(LibraryPlanner.TryIdentity(release));

    [Theory]
    [InlineData("Sample/fixture.mkv")]
    [InlineData("Extras/fixture.mkv")]
    [InlineData("Featurettes/fixture.mkv")]
    [InlineData("Proof/fixture.mkv")]
    [InlineData("Trailers/fixture.mkv")]
    [InlineData("release/fixture-sample.mkv")]
    [InlineData("sample.mkv")]
    public void Samples_and_extras_cannot_be_imported_as_the_feature(string relative)
    {
        var source = Path.Combine(_config.StagingRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "sample bytes");
        Assert.Throws<InvalidOperationException>(() => LibraryPlanner.Plan(_config, source, Movie));
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(_config.MovieRoot));
    }

    [Theory]
    [InlineData("Other.Film.2024.1080p.mkv", Movie)]
    [InlineData("Synthetic.Film.2023.1080p.mkv", Movie)]
    [InlineData("Synthetic.Show.S01E02.1080p.mkv", "Synthetic.Show.S01E01.1080p.WEB-TEST")]
    public void Parseable_source_identity_must_match_requested_release(string filename, string release)
    {
        var source = Source(filename);
        Assert.Throws<InvalidOperationException>(() => LibraryPlanner.Plan(_config, source, release));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void Matching_source_identity_is_accepted()
    {
        var source = Source(Movie + ".mkv");
        Assert.Equal(source, LibraryPlanner.Plan(_config, source, Movie).SourcePath);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("overlap")]
    [InlineData("placeholder")]
    [InlineData("hidden")]
    public void Invalid_import_paths_do_not_produce_a_plan(string scenario)
    {
        var source = Source(scenario == "placeholder" ? "video-MISSING-data.mkv" : scenario == "hidden" ? ".hidden.mkv" : "fixture.mkv");
        if (scenario == "outside") { source = Path.Combine(_root, "outside.mkv"); File.WriteAllText(source, "synthetic"); }
        if (scenario == "overlap") _config.MovieRoot = Path.Combine(_config.StagingRoot, "watched");
        Assert.Throws<InvalidOperationException>(() => LibraryPlanner.Plan(_config, source, Movie));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Dry_run_creates_no_destination_directories_and_keeps_source()
    {
        _config.DryRun = true;
        var source = Source();
        var plan = LibraryPlanner.Plan(_config, source, Movie);
        var result = await new SafeLibraryImporter(_ => { }).ImportAsync(plan, _config);
        Assert.Equal("dry-run", result.Status);
        Assert.False(result.Verified);
        Assert.False(result.SourceRemoved);
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(_config.MovieRoot));
    }

    [Fact]
    public async Task Disabled_import_never_moves_a_planned_file()
    {
        var source = Source();
        var plan = LibraryPlanner.Plan(_config, source, Movie);
        _config.Enabled = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SafeLibraryImporter(_ => { }).ImportAsync(plan, _config));
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(_config.MovieRoot));
    }

    [Fact]
    public async Task Actual_windows_same_volume_move_is_verified_rename()
    {
        var source = Source();
        var expected = File.ReadAllBytes(source);
        var plan = LibraryPlanner.Plan(_config, source, Movie);
        var result = await new SafeLibraryImporter(_ => { }) { Checkpoint = step =>
        {
            if (step != "before-rename") return;
            Assert.Throws<IOException>(() => File.WriteAllText(source, "changed"));
            Assert.Throws<IOException>(() => Directory.Move(_config.StagingRoot, _config.StagingRoot + ".moved"));
        } }.ImportAsync(plan, _config);
        Assert.True(result.Verified);
        Assert.True(result.SourceRemoved);
        Assert.Equal("rename:size+sha256", result.Verification);
        Assert.Equal(expected, File.ReadAllBytes(plan.DestinationPath));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task Native_rename_preserves_unicode_destination_names()
    {
        var source = Source();
        var plan = LibraryPlanner.Plan(_config, source, "Synthetïc.Fïlm.2024.1080p.WEB-TEST");
        var result = await new SafeLibraryImporter(_ => { }).ImportAsync(plan, _config);
        Assert.Equal("rename:size+sha256", result.Verification);
        Assert.Contains("Synthetïc Fïlm", result.DestinationPath);
        Assert.Equal("synthetic video bytes", File.ReadAllText(result.DestinationPath));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task Unheld_ancestor_cannot_be_renamed_while_import_descendant_handles_are_open()
    {
        // Both configured roots exist, so importer needs no handle to their shared parent.
        Directory.CreateDirectory(_config.MovieRoot);
        var source = Source();
        var plan = LibraryPlanner.Plan(_config, source, Movie);
        var tested = false;
        var importer = new SafeLibraryImporter(_ => { }) { Checkpoint = step =>
        {
            if (step != "before-rename") return;
            tested = true;
            Assert.Throws<IOException>(() => Directory.Move(_root, _root + ".moved"));
        } };
        var result = await importer.ImportAsync(plan, _config);
        Assert.True(tested);
        Assert.True(result.Verified);
        Assert.True(File.Exists(plan.DestinationPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Collision_created_after_preflight_never_overwrites_or_removes_either_file(bool copy)
    {
        var source = Source();
        var plan = LibraryPlanner.Plan(_config, source, Movie);
        var importer = new SafeLibraryImporter(_ => { }) { ForceCopyForTests = copy, Checkpoint = step =>
        {
            if (step == (copy ? "before-copy" : "before-rename")) File.WriteAllText(plan.DestinationPath, "existing destination");
        } };
        await Assert.ThrowsAsync<IOException>(() => importer.ImportAsync(plan, _config));
        Assert.Equal("existing destination", File.ReadAllText(plan.DestinationPath));
        Assert.Equal("synthetic video bytes", File.ReadAllText(source));
    }

    [Fact]
    public async Task Copy_verifies_size_and_hash_before_removing_locked_source()
    {
        var source = Source();
        var expected = File.ReadAllBytes(source);
        var plan = LibraryPlanner.Plan(_config, source, Movie);
        var steps = new List<string>();
        var importer = new SafeLibraryImporter(_ => { }) { ForceCopyForTests = true, Checkpoint = step =>
        {
            steps.Add(step);
            if (step != "verified") return;
            Assert.True(File.Exists(source));
            using (var reader = new FileStream(plan.DestinationPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var memory = new MemoryStream())
            {
                reader.CopyTo(memory);
                Assert.Equal(expected, memory.ToArray());
            }
            Assert.Throws<IOException>(() => File.WriteAllText(source, "changed"));
            Assert.Throws<IOException>(() => File.Move(source, source + ".moved"));
            Assert.Throws<IOException>(() => File.WriteAllText(plan.DestinationPath, "changed"));
            Assert.Throws<IOException>(() => Directory.Move(Path.GetDirectoryName(plan.DestinationPath)!, Path.GetDirectoryName(plan.DestinationPath)! + ".moved"));
        } };
        var result = await importer.ImportAsync(plan, _config);
        Assert.Equal(new[] { "before-copy", "copy-chunk", "copied", "copy-verified", "verified" }, steps);
        Assert.Equal("copy:size+sha256", result.Verification);
        Assert.False(File.Exists(source));
        Assert.Equal(expected, File.ReadAllBytes(plan.DestinationPath));
    }

    [Theory]
    [InlineData("copied")]
    [InlineData("verified")]
    public async Task Copy_fault_retains_source_and_only_keeps_a_published_destination(string faultAt)
    {
        var source = Source();
        var plan = LibraryPlanner.Plan(_config, source, Movie);
        var importer = new SafeLibraryImporter(_ => { }) { ForceCopyForTests = true, Checkpoint = step =>
        {
            if (step == faultAt) throw new IOException("synthetic verification fault");
        } };
        await Assert.ThrowsAsync<IOException>(() => importer.ImportAsync(plan, _config));
        Assert.Equal("synthetic video bytes", File.ReadAllText(source));
        if (faultAt == "verified") Assert.Equal("synthetic video bytes", File.ReadAllText(plan.DestinationPath));
        else Assert.False(File.Exists(plan.DestinationPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(plan.DestinationPath)!, ".*.gldrive-tmp"));
    }

    [Fact]
    public async Task Cancellation_after_copy_verification_retains_both_files()
    {
        var source = Source();
        var plan = LibraryPlanner.Plan(_config, source, Movie);
        using var cancellation = new CancellationTokenSource();
        var importer = new SafeLibraryImporter(_ => { }) { ForceCopyForTests = true, Checkpoint = step =>
        {
            if (step == "verified") cancellation.Cancel();
        } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.ImportAsync(plan, _config, cancellation.Token));
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(plan.DestinationPath));
    }

    [Fact]
    public async Task Cancellation_during_copy_removes_private_temporary_file_and_keeps_full_source()
    {
        var source = Source(content: new string('x', 300_000));
        var plan = LibraryPlanner.Plan(_config, source, Movie);
        using var cancellation = new CancellationTokenSource();
        var importer = new SafeLibraryImporter(_ => { }) { ForceCopyForTests = true, Checkpoint = step =>
        {
            if (step != "copy-chunk") return;
            Assert.False(File.Exists(plan.DestinationPath));
            var temp = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(plan.DestinationPath)!, ".*.gldrive-tmp"));
            Assert.StartsWith(".", Path.GetFileName(temp));
            Assert.Empty(new LibraryIndex().Refresh([_config.MovieRoot], []).Entries);
            cancellation.Cancel();
        } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.ImportAsync(plan, _config, cancellation.Token));
        Assert.Equal(plan.ExpectedLength, new FileInfo(source).Length);
        Assert.False(File.Exists(plan.DestinationPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(plan.DestinationPath)!, ".*.gldrive-tmp"));
        Assert.Equal(plan, LibraryPlanner.Plan(_config, source, Movie));
    }

    [Fact]
    public async Task Collision_after_copy_verification_deletes_only_its_temporary_file()
    {
        var source = Source();
        var plan = LibraryPlanner.Plan(_config, source, Movie);
        var unrelated = Path.Combine(Path.GetDirectoryName(plan.DestinationPath)!, ".unrelated.gldrive-tmp");
        var importer = new SafeLibraryImporter(_ => { }) { ForceCopyForTests = true, Checkpoint = step =>
        {
            if (step != "copy-verified") return;
            Assert.False(File.Exists(plan.DestinationPath));
            File.WriteAllText(plan.DestinationPath, "other owner");
            File.WriteAllText(unrelated, "unrelated temporary file");
        } };
        await Assert.ThrowsAsync<IOException>(() => importer.ImportAsync(plan, _config));
        Assert.Equal("other owner", File.ReadAllText(plan.DestinationPath));
        Assert.Equal("synthetic video bytes", File.ReadAllText(source));
        Assert.Equal(unrelated, Assert.Single(Directory.GetFiles(Path.GetDirectoryName(plan.DestinationPath)!, ".*.gldrive-tmp")));
        Assert.Equal("unrelated temporary file", File.ReadAllText(unrelated));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_destination_directory_cannot_be_swapped_before_creation_or_rename(bool copy)
    {
        var source = Source();
        var plan = LibraryPlanner.Plan(_config, source, Movie);
        var tested = false;
        var importer = new SafeLibraryImporter(_ => { }) { ForceCopyForTests = copy, Checkpoint = step =>
        {
            if (step != (copy ? "before-copy" : "before-rename")) return;
            tested = true;
            var directory = Path.GetDirectoryName(plan.DestinationPath)!;
            Assert.Empty(Directory.GetFileSystemEntries(directory));
            Assert.Throws<IOException>(() => Directory.Move(directory, directory + ".moved"));
            Assert.Throws<IOException>(() => Directory.Delete(directory));
        } };
        var result = await importer.ImportAsync(plan, _config);
        Assert.True(tested);
        Assert.True(result.Verified);
    }

    [Fact]
    public async Task Source_edits_after_planning_require_a_new_plan()
    {
        var source = Source();
        var plan = LibraryPlanner.Plan(_config, source, Movie);
        File.WriteAllText(source, "a later user edit with another size");
        await Assert.ThrowsAsync<IOException>(() => new SafeLibraryImporter(_ => { }).ImportAsync(plan, _config));
        Assert.Equal("a later user edit with another size", File.ReadAllText(source));
        Assert.False(Directory.Exists(_config.MovieRoot));
    }

    [Fact]
    public async Task Existing_writer_prevents_any_source_removal()
    {
        var source = Source();
        var plan = LibraryPlanner.Plan(_config, source, Movie);
        using var writer = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        await Assert.ThrowsAsync<IOException>(() => new SafeLibraryImporter(_ => { }).ImportAsync(plan, _config));
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(plan.DestinationPath));
    }

    [Fact]
    public async Task Forged_destination_is_not_accepted()
    {
        var source = Source();
        var plan = LibraryPlanner.Plan(_config, source, Movie) with { DestinationPath = Path.Combine(_root, "forged.mkv") };
        await Assert.ThrowsAsync<IOException>(() => new SafeLibraryImporter(_ => { }).ImportAsync(plan, _config));
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(plan.DestinationPath));
    }

    [Fact]
    public async Task Files_above_hash_threshold_still_require_verified_size_and_identity()
    {
        _config.HashVerifyMaxBytes = 1;
        var source = Source();
        var plan = LibraryPlanner.Plan(_config, source, Movie);
        var result = await new SafeLibraryImporter(_ => { }) { ForceCopyForTests = true }.ImportAsync(plan, _config);
        Assert.Equal("copy:size", result.Verification);
        Assert.True(result.Verified);
        Assert.False(File.Exists(source));
    }

    public void Dispose()
    {
        // This synthetic fixture owns only its unique temp directory.
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
