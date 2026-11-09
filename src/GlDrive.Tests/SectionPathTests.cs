using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// A dated section is a property of the SITE: sitea files FLAC under /FLAC/MMDD while
/// siteb keeps /incoming/mp3 flat. The {MMDD} token lets a path say so instead of
/// relying on the leaf-name guess (mp3/0day), which routed sitea FLAC into the read-only
/// section root.
/// </summary>
public sealed class SectionPathTests
{
    private const string Release = "Artist-Album-16BIT-WEB-FLAC-2026-GRP";
    private static readonly DateTime Nov9 = new(2026, 11, 9, 19, 30, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("/FLAC/{MMDD}", "/FLAC/1109")]
    [InlineData("/FLAC/{mmdd}/", "/FLAC/1109/")]
    [InlineData("/x264", "/x264")]
    public void Expand_substitutes_todays_utc_folder(string path, string expected) =>
        Assert.Equal(expected, SectionPath.Expand(path, Nov9));

    [Fact]
    public void Token_path_is_expanded_not_suffixed_again() =>
        Assert.Equal("/FLAC/1109", SpreadJob.DatedBase("/FLAC/{MMDD}/", Nov9));

    [Theory]
    [InlineData("/mp3", "/mp3/1109")]
    [InlineData("/incoming/0DAY/", "/incoming/0DAY/1109")]
    [InlineData("/flac.today", "/flac.today")]
    [InlineData("/FLAC", "/FLAC")]
    public void Legacy_leaf_rule_is_unchanged_for_untokened_paths(string path, string expected) =>
        Assert.Equal(expected, SpreadJob.DatedBase(path, Nov9));

    [Fact]
    public void Tokened_source_root_requires_an_explicit_discovered_path()
    {
        var source = new ServerConfig { Id = "source" };
        source.SpreadSite.Sections = new() { ["FLAC"] = "/FLAC/{MMDD}" };
        Assert.Null(RaceAnnounceMerge.ResolveSourcePath(source, new("source", "flac"), Release));
    }

    [Fact]
    public void Source_probe_bases_never_contain_the_literal_token()
    {
        var site = new ServerConfig();
        site.SpreadSite.Sections["FLAC"] = "/FLAC/{MMDD}";
        var bases = SpreadJob.CandidateBasePaths(site, "flac");
        Assert.DoesNotContain(bases, b => b.Contains('{'));
        Assert.Contains(bases, b => b.StartsWith("/FLAC/") && b.Length == "/FLAC/1109".Length);
    }

    [Fact]
    public void Wishlist_announce_resolves_into_todays_folder()
    {
        var server = new ServerConfig();
        server.SpreadSite.Sections["FLAC"] = "/FLAC/{MMDD}";
        var path = WishlistIrcRelease.ResolvePath(server, "flac", Release);
        Assert.NotNull(path);
        Assert.DoesNotContain("{", path);
        Assert.EndsWith("/" + Release, path);
    }
}
