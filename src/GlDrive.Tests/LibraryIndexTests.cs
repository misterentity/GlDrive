using System.IO;
using GlDrive.Downloads;
using GlDrive.Library;
using GlDrive.Plex;
using Xunit;

namespace GlDrive.Tests;

public sealed class LibraryIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-index-" + Guid.NewGuid().ToString("N"));
    public LibraryIndexTests() => Directory.CreateDirectory(_root);

    private string Write(string relative, int size = 3)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)7, size).ToArray());
        return path;
    }

    private static LibraryMediaIdentity Movie(QualityProfile quality = QualityProfile.Q1080p, int? year = 2024)
        => new("Synthetic Film", year, null, null, quality);
    private static LibraryIndexEntry Entry(LibraryMediaIdentity identity, bool verified = true)
        => new(identity, "synthetic.mkv", verified ? "disk" : "plex", verified, null, []);
    private static LibraryIndexSnapshot Snapshot(params LibraryIndexEntry[] entries) => new(DateTime.UtcNow, entries, 1, entries.Length, 0, true);
    private static PlexLibraryItem Plex(string path, string resolution = "1080", string title = "Synthetic Film", int? year = 2024)
        => new("synthetic-key", title, "movie", year, null, null, null, ["imdb://synthetic"], [new(path, resolution)]);

    [Fact]
    public void Explicit_scan_is_read_only_and_ignores_metadata_placeholders_and_extras()
    {
        var expected = Write("Synthetic.Film.2024.1080p.mkv");
        Write("Other.Film.2024.720p.mkv", 0);
        Write(".Hidden.Film.2024.1080p.mkv");
        Write("-MISSING-Absent.Film.2024.1080p.mkv");
        Write("Sample/Synthetic.Sample.2024.1080p.mkv");
        Write("Proof/Synthetic.Proof.2024.1080p.mkv");
        Write("Synthetic.Film.2024.1080p.nfo");
        Write("Synthetic.Film.2024.1080p.mkv.gdpart");
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

        var snapshot = new LibraryIndex().Refresh([_root], []);

        var item = Assert.Single(snapshot.Entries);
        Assert.Equal(expected, item.Path);
        Assert.Equal(Movie(), item.Identity);
        Assert.True(item.VerifiedOnDisk);
        Assert.Equal("disk", item.Source);
        Assert.True(snapshot.Complete);
        Assert.Equal(1, snapshot.VideoFiles);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Generic_movie_filename_uses_its_immediate_release_folder()
    {
        Write("Synthetic Film (2024)/main.mkv");
        var item = Assert.Single(new LibraryIndex().Refresh([_root], []).Entries);
        Assert.Equal(Movie(QualityProfile.Any), item.Identity);
    }

    [Fact]
    public void Generic_episode_filename_does_not_guess_an_episode_ordinal()
    {
        Write("Synthetic.Show.S01E02.1080p/02.mkv");
        var snapshot = new LibraryIndex().Refresh([_root], []);
        Assert.Empty(snapshot.Entries);
        Assert.Equal(1, snapshot.VideoFiles);
    }

    [Fact]
    public void Overlapping_roots_do_not_duplicate_files()
    {
        Write("child/Synthetic.Film.2024.1080p.mkv");
        var snapshot = new LibraryIndex().Refresh([_root, Path.Combine(_root, "child"), _root], []);
        Assert.Single(snapshot.Entries);
        Assert.Equal(1, snapshot.VideoFiles);
    }

    [Fact]
    public void Missing_or_unconfigured_roots_cannot_prove_absence()
    {
        var missing = new LibraryIndex().Refresh([Path.Combine(_root, "missing")], []);
        Assert.False(missing.Complete);
        Assert.Equal(1, missing.ScanIssues);
        Assert.Equal("unknown", LibraryIndex.Lookup(missing, Movie()).Status);
        Assert.False(new LibraryIndex().Refresh([], []).Complete);
    }

    [Fact]
    public void Cancelled_refresh_preserves_last_complete_snapshot()
    {
        Write("Synthetic.Film.2024.1080p.mkv");
        var index = new LibraryIndex();
        var prior = index.Refresh([_root], []);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => index.Refresh([_root], [], cancelled.Token));
        Assert.Same(prior, index.Snapshot);
    }

    [Fact]
    public void Plex_catalog_only_does_not_claim_local_ownership_even_when_file_exists_outside_scanned_root()
    {
        var outside = Write("outside/Synthetic.Film.2024.2160p.mkv");
        var allowed = Path.Combine(_root, "allowed");
        Directory.CreateDirectory(allowed);
        var snapshot = new LibraryIndex().Refresh([allowed], [Plex(outside, "4k")]);
        var comparison = LibraryIndex.Lookup(snapshot, Movie());
        Assert.Equal("catalog-only", comparison.Status);
        Assert.Equal(QualityProfile.Q2160p, comparison.BestObservedQuality);
        Assert.False(comparison.VerifiedOnDisk);
        Assert.False(comparison.MeetsCutoff);
        Assert.False(comparison.CandidateIsUpgrade);
    }

    [Fact]
    public void Plex_and_scanned_path_merge_and_use_reported_resolution()
    {
        var path = Write("Synthetic.Film.2024.720p.mkv");
        var item = Assert.Single(new LibraryIndex().Refresh([_root], [Plex(path, "1080")]).Entries);
        Assert.Equal("both", item.Source);
        Assert.True(item.VerifiedOnDisk);
        Assert.Equal(QualityProfile.Q1080p, item.Identity.Quality);
        Assert.Equal(["imdb://synthetic"], item.Guids);
    }

    [Fact]
    public void Plex_unknown_resolution_uses_verified_filename_quality()
    {
        var path = Write("Synthetic.Film.2024.720p.mkv");
        var item = Assert.Single(new LibraryIndex().Refresh([_root], [Plex(path, "")]).Entries);
        Assert.Equal(QualityProfile.Q720p, item.Identity.Quality);
    }

    [Fact]
    public void Plex_item_without_file_remains_catalog_observation()
    {
        var item = Plex("") with { Files = [] };
        var snapshot = new LibraryIndex().Refresh([_root], [item]);
        Assert.Equal("catalog-only", LibraryIndex.Lookup(snapshot, Movie()).Status);
        Assert.False(Assert.Single(snapshot.Entries).VerifiedOnDisk);
    }

    [Fact]
    public void Plex_episode_uses_series_title_and_does_not_treat_air_year_as_series_year()
    {
        var item = new PlexLibraryItem("episode-key", "Episode Title", "episode", 2024, "Synthetic Show", 1, 2, [], [new("remote", "720")]);
        var identity = Assert.Single(new LibraryIndex().Refresh([_root], [item]).Entries).Identity;
        Assert.Equal(new("Synthetic Show", null, 1, 2, QualityProfile.Q720p), identity);
    }

    [Theory]
    [InlineData("Synthetic Show", 2021)]
    [InlineData("Different Show", null)]
    public void Verified_filename_series_year_is_retained_only_when_episode_identity_agrees(string series, int? expectedYear)
    {
        var path = Write("Synthetic.Show.2021.S01E02.1080p.mkv");
        var item = new PlexLibraryItem("episode-key", "Episode Title", "episode", 2024, series, 1, 2, [], [new(path, "1080")]);
        var identity = Assert.Single(new LibraryIndex().Refresh([_root], [item]).Entries).Identity;
        Assert.Equal(expectedYear, identity.Year);
    }

    [Fact]
    public void Catalog_paths_preserve_case_distinct_entries_on_remote_filesystems()
    {
        var item = Plex("/media/Synthetic.mkv") with
        {
            Files = [new("/media/Synthetic.mkv", "1080"), new("/media/synthetic.mkv", "720")]
        };
        Assert.Equal(2, new LibraryIndex().Refresh([_root], [item]).Entries.Count);
    }

    [Fact]
    public void Exact_normalized_title_year_season_and_episode_establish_identity()
    {
        Assert.True(LibraryIndex.SameIdentity(Movie() with { Title = "synthetic-film" }, Movie()));
        Assert.False(LibraryIndex.SameIdentity(Movie() with { Title = "Synthetic Film Returns" }, Movie()));
        Assert.False(LibraryIndex.SameIdentity(Movie(year: 2023), Movie()));
        Assert.False(LibraryIndex.SameIdentity(Movie(year: null), Movie()));
        Assert.False(LibraryIndex.SameIdentity(Movie() with { Title = "!!!" }, Movie() with { Title = "???" }));
        var episode = new LibraryMediaIdentity("Synthetic Show", null, 1, 2, QualityProfile.Q720p);
        Assert.True(LibraryIndex.SameIdentity(episode, episode));
        Assert.False(LibraryIndex.SameIdentity(episode with { Episode = 3 }, episode));
        Assert.False(LibraryIndex.SameIdentity(episode with { Season = 2 }, episode));
        Assert.False(LibraryIndex.SameIdentity(episode with { Season = null, Episode = null }, episode));
    }

    [Fact]
    public void Unknown_requested_year_is_ambiguous_across_known_remakes()
    {
        var comparison = LibraryIndex.Lookup(Snapshot(Entry(Movie(year: 1984)), Entry(Movie())), Movie(year: null));
        Assert.Equal("ambiguous", comparison.Status);
        Assert.False(comparison.MeetsCutoff);
        Assert.False(comparison.CandidateIsUpgrade);
    }

    [Theory]
    [InlineData(QualityProfile.Q2160p, QualityProfile.Q1080p, "owned", true, false)]
    [InlineData(QualityProfile.Q1080p, QualityProfile.Q1080p, "owned", true, false)]
    [InlineData(QualityProfile.Q720p, QualityProfile.Q1080p, "upgrade", false, true)]
    [InlineData(QualityProfile.Any, QualityProfile.Q1080p, "unknown-quality", false, false)]
    [InlineData(QualityProfile.Any, QualityProfile.Any, "owned", true, false)]
    public void Quality_comparison_requires_known_resolution_for_a_concrete_cutoff(QualityProfile owned,
        QualityProfile wanted, string status, bool meets, bool upgrade)
    {
        var comparison = LibraryIndex.Lookup(Snapshot(Entry(Movie(owned))), Movie(wanted));
        Assert.Equal(status, comparison.Status);
        Assert.Equal(meets, comparison.MeetsCutoff);
        Assert.Equal(upgrade, comparison.CandidateIsUpgrade);
        Assert.True(comparison.VerifiedOnDisk);
    }

    [Fact]
    public void Unverified_higher_quality_cannot_override_verified_lower_quality()
    {
        var comparison = LibraryIndex.Lookup(Snapshot(Entry(Movie(QualityProfile.Q720p)), Entry(Movie(QualityProfile.Q2160p), false)), Movie());
        Assert.Equal("upgrade", comparison.Status);
        Assert.Equal(QualityProfile.Q720p, comparison.BestObservedQuality);
    }

    [Fact]
    public void Incomplete_scan_retains_positive_local_evidence_but_never_proves_absence()
    {
        var incomplete = Snapshot(Entry(Movie())) with { Complete = false, ScanIssues = 1 };
        var owned = LibraryIndex.Lookup(incomplete, Movie());
        Assert.True(owned.MeetsCutoff);
        Assert.False(owned.CompleteEvidence);
        Assert.Equal("unknown", LibraryIndex.Lookup(incomplete, Movie() with { Title = "Absent Film" }).Status);
        Assert.Equal("not-observed", LibraryIndex.Lookup(Snapshot(), Movie()).Status);
    }

    [Theory]
    [InlineData("4k", QualityProfile.Q2160p)]
    [InlineData("2160p", QualityProfile.Q2160p)]
    [InlineData("1080", QualityProfile.Q1080p)]
    [InlineData("1080i", QualityProfile.Q1080p)]
    [InlineData("720", QualityProfile.Q720p)]
    [InlineData("SD", QualityProfile.SD)]
    [InlineData("480", QualityProfile.SD)]
    [InlineData("", QualityProfile.Any)]
    [InlineData("not-a-resolution", QualityProfile.Any)]
    [InlineData(null, QualityProfile.Any)]
    public void Plex_resolution_is_interpreted_conservatively(string? text, QualityProfile expected)
        => Assert.Equal(expected, LibraryIndex.ParseResolution(text));

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
