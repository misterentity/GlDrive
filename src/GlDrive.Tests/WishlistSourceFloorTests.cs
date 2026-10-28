using System.IO;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Library;
using Xunit;

namespace GlDrive.Tests;

public sealed class WishlistSourceFloorTests
{
    private const string Cam = "Example.Movie.2026.1080p.CAM.H264-GROUP";
    private const string Web = "Example.Movie.2026.1080p.WEB.H264-GROUP";
    private static WishlistItem Movie() => new() { Title = "Example Movie", Year = 2026, Quality = QualityProfile.Q1080p };

    [Theory]
    [InlineData("CAM")]
    [InlineData("HDCAM")]
    [InlineData("TS")]
    [InlineData("HDTS")]
    [InlineData("TELESYNC")]
    [InlineData("TC")]
    [InlineData("HDTC")]
    [InlineData("TELECINE")]
    [InlineData("CAMRIP")]
    [InlineData("HDCAMRIP")]
    [InlineData("HQCAM")]
    [InlineData("HQCAMRIP")]
    [InlineData("TSRIP")]
    [InlineData("HDTSRIP")]
    [InlineData("HQTS")]
    [InlineData("TCRIP")]
    [InlineData("HDTCRIP")]
    [InlineData("PDVD")]
    [InlineData("PREDVD")]
    [InlineData("PDVDRIP")]
    [InlineData("PREDVDRIP")]
    [InlineData("TELESYNCH")]
    [InlineData("TELESYNCHRIP")]
    public void Legacy_default_floor_rejects_known_camera_sources(string source)
    {
        var config = new DownloadConfig();
        Assert.True(config.BlockLowQualityWishlistSources);
        Assert.False(WishlistSourceFloor.Allows(Movie(), $"Example.Movie.2026.1080p.{source}.H264-GROUP", config));
    }

    [Theory]
    [InlineData("Example.Movie.2026.1080p.WEB.H264-GROUP")]
    [InlineData("Example.Movie.2026.1080p.BluRay.H264-GROUP")]
    [InlineData("Example.Movie.2026.1080p.H264-CAM")]
    [InlineData("Cam.Story.2026.1080p.WEB.H264-GROUP")]
    [InlineData("Example.Movie.2026.1080p.H264-GROUP")]
    public void Unknown_source_title_words_and_group_names_do_not_block_work(string release) =>
        Assert.True(WishlistSourceFloor.Allows(Movie(), release, new()));

    [Theory]
    [InlineData("DTS")]
    [InlineData("DTS-HD")]
    [InlineData("AAC")]
    [InlineData("H264")]
    [InlineData("X265")]
    [InlineData("DVDSCR")]
    [InlineData("BDSCR")]
    [InlineData("WEBSCR")]
    [InlineData("SCREENER")]
    public void Audio_codec_and_screener_tokens_are_not_cinema_recording_evidence(string token)
        => Assert.False(WishlistSourceFloor.IsLowQuality($"Example.Movie.2026.1080p.{token}-GROUP"));

    [Theory]
    [InlineData("Example.Show.S02E05.Cam.1080p.WEB.H264-GROUP")]
    [InlineData("Example.Show.S02E05.TS.1080p.WEB.H264-GROUP")]
    [InlineData("Example.Show.S02E05.TC.1080p.WEB.H264-GROUP")]
    [InlineData("Example.Show.2026.10.27.Cam.Guest.1080p.WEB.H264-GROUP")]
    public void Tv_title_tokens_do_not_activate_movie_floor_or_scored_cutoff(string release)
    {
        var item = new WishlistItem { Title = "Example Show", Type = MediaType.TvShow, Quality = QualityProfile.Q1080p };
        Assert.True(WishlistSourceFloor.Allows(item, release, new()));
        Assert.True(WishlistOwnership.MeetsReleaseCutoff(item, release, new()));
        var scored = ReleaseScorer.Score(release, ScoredQualityProfile.Recommended(QualityProfile.Q1080p));
        Assert.True(scored.Accepted);
        Assert.True(scored.MeetsCutoff);
        Assert.Equal(ReleaseSource.Web, scored.Traits.Source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_and_scored_Tv_admission_accept_episode_title_Cam(bool scored)
    {
        var item = new WishlistItem { Title = "Example Show", Type = MediaType.TvShow, Quality = QualityProfile.Q1080p };
        var store = new WishlistStore(() => null, _ => { }); store.Add(item);
        var downloads = new List<DownloadItem>();
        var matcher = new WishlistMatcher(store, d => { downloads.Add(d); return true; },
            new() { ScoredQualityEnabled = scored }, "site", "Site");
        const string release = "Example.Show.S02E05.Cam.1080p.WEB.H264-GROUP";
        matcher.OnNewRelease("tv", release, "/tv/" + release);
        Assert.Single(downloads);
        var entry = new LibraryIndexEntry(new(item.Title, null, 2, 5, QualityProfile.Q1080p),
            @"C:\Library\" + release + ".mkv", "disk", true, null, []);
        Assert.False(WishlistSourceFloor.IsLowQualityLibrary(entry));
        Assert.True(WishlistOwnership.MeetsLibraryCutoff(item, entry, new()));
        Assert.True(LibraryIndex.Lookup(new(DateTime.UtcNow, [entry], 1, 1, 0, true), entry.Identity).MeetsCutoff);
    }

    [Theory]
    [InlineData(@"C:\Library\Cam (2018)\Cam (2018) WEBDL-1080p.mkv")]
    [InlineData(@"C:\Library\Cam (2018)\Cam (2018) - 1080p.mkv")]
    public void Parenthesized_movie_year_separates_title_Cam_from_source(string path)
    {
        var entry = new LibraryIndexEntry(new("Cam", 2018, null, null, QualityProfile.Q1080p), path, "disk", true, null, []);
        Assert.False(WishlistSourceFloor.IsLowQualityLibrary(entry));
        Assert.Equal("owned", LibraryIndex.Lookup(new(DateTime.UtcNow, [entry], 1, 1, 0, true), entry.Identity).Status);
    }

    [Fact]
    public void Explicit_item_and_existing_profile_allowances_are_preserved()
    {
        var item = Movie();
        item.AllowLowQualitySources = true;
        Assert.True(WishlistSourceFloor.Allows(item, Cam, new()));
        item.AllowLowQualitySources = null;
        item.ProfileId = "existing-profile";
        var config = new DownloadConfig { Profiles = [new() { Id = item.ProfileId, AllowLowQualitySources = true }] };
        Assert.True(WishlistSourceFloor.Allows(item, Cam, config));
        item.AllowLowQualitySources = false;
        Assert.False(WishlistSourceFloor.Allows(item, Cam, config));
    }

    [Fact]
    public void Global_floor_can_be_disabled_but_does_not_clear_an_explicit_item_denial()
    {
        var item = Movie();
        var config = new DownloadConfig { BlockLowQualityWishlistSources = false };
        Assert.True(WishlistSourceFloor.Allows(item, Cam, config));
        item.AllowLowQualitySources = false;
        Assert.False(WishlistSourceFloor.Allows(item, Cam, config));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_wishlist_admission_paths_apply_the_floor(bool scored)
    {
        var item = Movie();
        var store = new WishlistStore(() => null, _ => { });
        store.Add(item);
        var queued = new List<DownloadItem>();
        var config = new DownloadConfig { ScoredQualityEnabled = scored, LocalPath = Path.GetTempPath() };
        var matcher = new WishlistMatcher(store, d => { queued.Add(d); return true; }, config, "test-site", "Test site");
        matcher.OnNewRelease("movies", Cam, "/movies/" + Cam);
        Assert.Empty(queued);
        Assert.Equal(WishlistStatus.Watching, item.Status);
        matcher.OnNewRelease("movies", Web, "/movies/" + Web);
        Assert.Single(queued);
    }

    [Fact]
    public void Explicitly_allowed_cam_can_be_grabbed_but_never_closes_the_wanted_movie()
    {
        var item = Movie();
        item.ProfileId = "allow-camera";
        var config = new DownloadConfig
        {
            ScoredQualityEnabled = true, LocalPath = Path.GetTempPath(),
            Profiles = [new() { Id = item.ProfileId, AllowLowQualitySources = true, CutoffScore = 0 }]
        };
        var store = new WishlistStore(() => null, _ => { });
        store.Add(item);
        var queued = new List<DownloadItem>();
        var matcher = new WishlistMatcher(store, d => { queued.Add(d); return true; }, config, "test-site", "Test site");
        matcher.OnNewRelease("movies", Cam, "/movies/" + Cam);
        var download = Assert.Single(queued);
        download.Status = DownloadStatus.Completed;
        store.ObserveDownloadStatus(download, config);
        Assert.Equal(WishlistStatus.Watching, item.Status);
        Assert.False(WishlistOwnership.MeetsReleaseCutoff(item, Cam, config));
        Assert.False(WishlistOwnership.IsOwned(item, config, LibraryIndexSnapshot.Empty, [download], DateTimeOffset.UtcNow));
        matcher.OnNewRelease("movies", Web, "/movies/" + Web);
        Assert.Equal(2, queued.Count);
    }

    [Theory]
    [InlineData(@"C:\Library\Example.Movie.2026.1080p.WEB.H264-GROUP.ts", true)]
    [InlineData(@"C:\Library\Example.Movie.2026.1080p.WEB.H264-GROUP.m2ts", true)]
    [InlineData(@"C:\Library\Example.Movie.2026.1080p.H264-CAM.mkv", true)]
    [InlineData(@"C:\CAM\Example.Movie.2026.1080p.WEB.H264-GROUP.mkv", true)]
    [InlineData(@"C:\Library\Example.Movie.2026.1080p.CAM.H264-GROUP.mkv", false)]
    [InlineData(@"C:\Library\Example.Movie.2026.1080p.TS.H264-GROUP.mp4", false)]
    [InlineData(@"C:\Library\Example.Movie.2026.1080p.TC.H264-GROUP\video.mkv", false)]
    [InlineData(@"C:\Library\Example.Movie.2026.1080p.CAM.H264-GROUP\Example.Movie.2026.1080p.WEB.H264-GROUP.mkv", false)]
    public void Library_cutoff_uses_media_evidence_without_treating_container_or_parent_words_as_sources(string path, bool cutoff)
    {
        var item = Movie();
        item.AllowLowQualitySources = true;
        var entry = new LibraryIndexEntry(new(item.Title, item.Year, null, null, QualityProfile.Q1080p), path, "disk", true, null, []);
        Assert.Equal(cutoff, WishlistOwnership.MeetsLibraryCutoff(item, entry, new()));
    }

    [Theory]
    [InlineData(QualityProfile.Q720p, QualityProfile.Q2160p, false)]
    [InlineData(QualityProfile.Q2160p, QualityProfile.Q720p, true)]
    public void Selected_builtin_profile_controls_cutoff_after_quality_choice_changes(QualityProfile oldQuality, QualityProfile selected, bool cutoff)
    {
        var item = Movie();
        item.Quality = oldQuality;
        item.ProfileId = ScoredQualityProfile.Legacy(selected).Id;
        Assert.Equal(cutoff, WishlistOwnership.MeetsReleaseCutoff(item, Web, new()));
        var entry = new LibraryIndexEntry(new(item.Title, item.Year, null, null, QualityProfile.Q1080p),
            @"C:\Library\Example.Movie.2026.1080p.WEB.H264-GROUP.mkv", "disk", true, null, []);
        Assert.Equal(cutoff, WishlistOwnership.MeetsLibraryCutoff(item, entry, new()));
    }

    [Fact]
    public void Library_comparison_reports_only_owned_cam_as_needing_an_upgrade()
    {
        var identity = new LibraryMediaIdentity("Example Movie", 2026, null, null, QualityProfile.Q1080p);
        var cam = new LibraryIndexEntry(identity, @"C:\Library\Example.Movie.2026.1080p.CAM.H264-GROUP.mkv", "disk", true, null, []);
        var snapshot = new LibraryIndexSnapshot(DateTime.UtcNow, [cam], 1, 1, 0, true);
        var comparison = LibraryIndex.Lookup(snapshot, identity);
        Assert.Equal("upgrade", comparison.Status);
        Assert.True(comparison.VerifiedOnDisk);
        Assert.False(comparison.MeetsCutoff);
        var web = cam with { Path = @"C:\Library\Example.Movie.2026.1080p.WEB.H264-GROUP.mkv" };
        Assert.True(LibraryIndex.Lookup(snapshot with { Entries = [cam, web] }, identity).MeetsCutoff);
    }

    [Fact]
    public void Unknown_normalized_library_source_cannot_override_matching_completed_camera_evidence()
    {
        var item = Movie();
        var imported = new LibraryIndexEntry(new(item.Title, item.Year, null, null, item.Quality),
            @"C:\Library\Example Movie (2026)\Example Movie (2026) - 1080p.mkv", "disk", true, null, []);
        var snapshot = new LibraryIndexSnapshot(DateTime.UtcNow, [imported], 1, 1, 0, true);
        var camera = new DownloadItem { ReleaseName = Cam, Status = DownloadStatus.Completed, WishlistItemId = item.Id };
        Assert.False(WishlistOwnership.IsOwned(item, new(), snapshot, [camera], DateTimeOffset.UtcNow));
        var web = imported with { Path = @"C:\Library\Example Movie (2026)\Example Movie (2026) - WEB - 1080p.mkv" };
        Assert.True(WishlistOwnership.IsOwned(item, new(), snapshot with { Entries = [web] }, [camera], DateTimeOffset.UtcNow));
    }

    [Fact]
    public void General_legacy_scorer_keeps_explicit_low_source_acceptance()
    {
        var score = ReleaseScorer.Score(Cam, ScoredQualityProfile.Legacy(QualityProfile.Q1080p));
        Assert.True(score.Accepted);
        Assert.False(score.MeetsCutoff);
    }
}
