using System.IO;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services.Automation;
using System.Text.Json;
using Xunit;

namespace GlDrive.Tests;

public class DownloadCategoryPathTests
{
    private const string Episode = "Example.Show.S02E03.1080p.WEB-GROUP";
    private const string SeasonPack = "Example.Show.S02.1080p.WEB-GROUP";
    private const string Movie = "Example.Film.2026.2160p.BluRay-GROUP";
    private const string Dated = "Daily.Show.2024.02.29.1080p.HDTV-GROUP";

    private static DownloadConfig Config() => new()
    {
        LocalPath = @"C:\downloads",
        CategoryPaths = new() { ["TV"] = @"D:\TV", ["Movies"] = @"D:\Movies" }
    };

    [Theory]
    [InlineData(Episode, "TV")]
    [InlineData("Example.Show.S02E03E04.1080p.WEB-GROUP", "TV")]
    [InlineData(SeasonPack, "TV")]
    [InlineData(Dated, "TV")]
    [InlineData("Daily.Show.2026-10-28.720p.WEB-GROUP", "TV")]
    [InlineData("Example.Show.S01-GROUP", "TV")]
    [InlineData(Movie, "Movies")]
    [InlineData("Example.Film.BDRip.x264-GROUP", "Movies")]
    [InlineData("Example.Film.2026.UHD-GROUP", "Movies")]
    [InlineData("Example.Film.2026.4K-GROUP", "Movies")]
    [InlineData("Artist-Album-2026-FLAC-GROUP", null)]
    [InlineData("Artist-Album-2026-WEB-GROUP", null)]
    [InlineData("Artist.Live.2024.02.29.FLAC-GROUP", null)]
    [InlineData("Editor.2026.WIN64-GROUP", null)]
    [InlineData("Tool.S02.WIN64-GROUP", null)]
    [InlineData("4K.Video.Tool.2026.x64-GROUP", null)]
    [InlineData("Unclassified.Release.2026-GROUP", null)]
    public void Classifies_only_supported_media_evidence(string release, string? category)
        => Assert.Equal(category, SceneNameParser.ClassifyCategory(release));

    [Theory]
    [InlineData("Daily.Show.2024.02.29.1080p", true)]
    [InlineData("Daily.Show.2026.02.29.1080p", false)]
    [InlineData("Daily.Show.2026.02.30.1080p", false)]
    [InlineData("Daily.Show.2026.13.01.1080p", false)]
    [InlineData("Daily.Show.2026.01.00.1080p", false)]
    public void Date_episodes_require_a_real_calendar_date(string release, bool valid)
        => Assert.Equal(valid, SceneNameParser.TryEpisodeDate(release, out _));

    [Fact]
    public void Exact_section_wins_over_logical_and_default_paths()
    {
        var config = Config();
        config.CategoryPaths["tv-hd"] = @"E:\section";
        var resolved = DownloadPathResolver.Resolve(config, "tv-hd", Episode);
        Assert.Equal(DownloadPathSource.ExactSection, resolved.Source);
        Assert.Equal(@"E:\section", resolved.BasePath);
        Assert.Equal(Path.Combine(@"E:\section", "TV", "Example Show", "Season 02", Episode),
            DownloadPathResolver.ForScene(config, "tv-hd", Episode));
        Assert.Equal(@"E:\section", config.GetPathForCategory("tv-hd"));
    }

    [Fact]
    public void Exact_canonical_section_keeps_its_existing_layout()
    {
        var config = Config();
        Assert.Equal(DownloadPathSource.ExactSection, DownloadPathResolver.Resolve(config, "TV", Episode).Source);
        Assert.Equal(Path.Combine(@"D:\TV", "TV", "Example Show", "Season 02", Episode),
            DownloadPathResolver.ForScene(config, "TV", Episode));
    }

    [Theory]
    [InlineData(Episode, "Example Show", "Season 02")]
    [InlineData("Example.Show.S02E03E04.1080p.WEB-GROUP", "Example Show", "Season 02")]
    [InlineData("Example.Show.S02-GROUP", "Example Show", "Season 02")]
    [InlineData(SeasonPack, "Example Show", "Season 02")]
    [InlineData(Dated, "Daily Show", "Season 01")]
    public void Logical_TV_root_has_no_repeated_class_folder(string release, string title, string season)
    {
        var config = Config();
        var resolved = DownloadPathResolver.Resolve(config, "series-section", release);
        Assert.Equal(DownloadPathSource.LogicalClass, resolved.Source);
        Assert.Equal(@"D:\TV", resolved.BasePath);
        Assert.Equal(Path.Combine(@"D:\TV", title, season, release),
            DownloadPathResolver.ForScene(config, "series-section", release));
    }

    [Theory]
    [InlineData(Movie, "Example Film (2026)")]
    [InlineData("Example.Film.BDRip.x264-GROUP", "Example Film")]
    public void Logical_movie_root_has_no_repeated_class_folder(string release, string folder)
    {
        Assert.Equal(Path.Combine(@"D:\Movies", folder, release), DownloadPathResolver.ForScene(Config(), "video", release));
    }

    [Theory]
    [InlineData(Episode, MediaType.TvShow, "Wanted Show", null, "Season 02")]
    [InlineData(Dated, MediaType.TvShow, "Wanted Daily Show", null, "Season 01")]
    [InlineData(Movie, MediaType.Movie, "Wanted Film", 2026, null)]
    public void Wishlist_uses_logical_roots_and_preserves_its_metadata_title(string release, MediaType kind, string title, int? year, string? season)
    {
        var item = new WishlistItem { Type = kind, Title = title, Year = year };
        var expected = kind == MediaType.Movie
            ? Path.Combine(@"D:\Movies", title + " (2026)", release)
            : Path.Combine(@"D:\TV", title, season!, release);
        Assert.Equal(expected, DownloadPathResolver.ForWishlist(Config(), item, "raw-section", release));
    }

    [Fact]
    public void Wishlist_exact_key_retains_class_folder()
    {
        var config = Config();
        config.CategoryPaths["video"] = @"E:\raw";
        var item = new WishlistItem { Type = MediaType.Movie, Title = "Wanted Film", Year = 2026 };
        Assert.Equal(Path.Combine(@"E:\raw", "Movies", "Wanted Film (2026)", Movie),
            DownloadPathResolver.ForWishlist(config, item, "video", Movie));
    }

    [Fact]
    public void Missing_logical_mapping_and_unknown_releases_use_default_root()
    {
        var config = Config();
        config.CategoryPaths.Clear();
        Assert.Equal(DownloadPathSource.Default, DownloadPathResolver.Resolve(config, "video", Movie).Source);
        Assert.Equal(Path.Combine(config.LocalPath, "Movies", "Example Film (2026)", Movie),
            DownloadPathResolver.ForScene(config, "video", Movie));
        config = Config();
        const string release = "Unclassified.Release-GROUP";
        Assert.Equal(DownloadPathSource.Default, DownloadPathResolver.Resolve(config, "misc", release).Source);
        Assert.Equal(Path.Combine(config.LocalPath, "misc", release), DownloadPathResolver.ForScene(config, "misc", release));
    }

    [Fact]
    public void Blank_exact_and_logical_paths_fall_through_in_order()
    {
        var config = Config();
        config.CategoryPaths["video"] = " ";
        Assert.Equal(@"D:\Movies", config.GetPathForCategory("video", Movie));
        config.CategoryPaths["Movies"] = "";
        Assert.Equal(config.LocalPath, config.GetPathForCategory("video", Movie));
    }

    [Fact]
    public void Logical_keys_are_case_insensitive_but_exact_raw_lookup_retains_dictionary_semantics()
    {
        var config = Config();
        config.CategoryPaths.Clear();
        config.CategoryPaths["tV"] = @"E:\shows";
        config.CategoryPaths["RAW"] = @"E:\raw";
        Assert.Equal(@"E:\shows", DownloadPathResolver.Resolve(config, "raw", Episode).BasePath);
        Assert.Equal(DownloadPathSource.ExactSection, DownloadPathResolver.Resolve(config, "RAW", Episode).Source);
    }

    [Fact]
    public void Flat_automation_default_uses_logical_root_but_preserves_explicit_path()
    {
        var config = Config();
        Assert.Equal(Path.Combine(@"D:\Movies", Movie), DownloadPathResolver.Flat(config, "video", Movie));
        Assert.Equal(@"E:\chosen\complete-path", DownloadPathResolver.Flat(config, "video", Movie, @"E:\chosen\complete-path"));
    }

    [Fact]
    public void Exact_season_pack_layout_is_unchanged_while_logical_layout_removes_season_from_title()
    {
        var config = Config();
        config.CategoryPaths["raw"] = @"E:\raw";
        Assert.Equal(Path.Combine(@"E:\raw", "TV", "Example Show S02", "Season 02", SeasonPack),
            DownloadPathResolver.ForScene(config, "raw", SeasonPack));
        Assert.Equal(Path.Combine(@"D:\TV", "Example Show", "Season 02", SeasonPack),
            DownloadPathResolver.ForScene(config, "unmapped", SeasonPack));
    }

    [Fact]
    public void Configuration_schema_explains_category_precedence_and_layout()
    {
        var automation = new ConfigurationAutomation(new AppConfig(), _ => { });
        var schema = JsonSerializer.SerializeToNode(automation.Schema())!;
        var description = schema["schema"]!["properties"]!["downloads"]!["properties"]!["categoryPaths"]!["description"]!.GetValue<string>();
        Assert.Contains("exact raw section key", description);
        Assert.Contains("logical TV/Movies", description);
        Assert.Contains("then localPath", description);
        Assert.Contains("suppresses the extra", description);
    }
}
