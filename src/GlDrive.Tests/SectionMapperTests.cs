using GlDrive.Config;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class SectionMapperTests
{
    private static SiteSpreadConfig Site(params string[] sectionKeys)
    {
        var s = new SiteSpreadConfig();
        foreach (var k in sectionKeys) s.Sections[k] = "/" + k.ToLowerInvariant();
        return s;
    }

    [Fact]
    public void HasSectionFor_exact_key_match_case_insensitive()
    {
        var site = Site("MP3", "FLAC", "X265");
        Assert.True(SectionMapper.HasSectionFor(site, "mp3"));
        Assert.True(SectionMapper.HasSectionFor(site, "MP3"));
        Assert.True(SectionMapper.HasSectionFor(site, "x265"));
    }

    [Fact]
    public void HasSectionFor_fuzzy_dash_underscore()
    {
        var site = Site("X264_HD", "TV_HD");
        Assert.True(SectionMapper.HasSectionFor(site, "x264-hd"));
        Assert.True(SectionMapper.HasSectionFor(site, "tv-hd"));
    }

    [Fact]
    public void HasSectionFor_via_explicit_mapping()
    {
        var site = new SiteSpreadConfig();
        site.SectionMappings.Add(new SectionMapping { IrcSection = "tv-1080p", RemoteSection = "X264-HD", Enabled = true });
        Assert.True(SectionMapper.HasSectionFor(site, "tv-1080p"));
    }

    [Fact]
    public void HasSectionFor_false_when_absent()
    {
        var site = Site("MP3", "FLAC");
        Assert.False(SectionMapper.HasSectionFor(site, "x265"));
        Assert.False(SectionMapper.HasSectionFor(site, "xxx-paysite"));
        Assert.False(SectionMapper.HasSectionFor(site, ""));
    }
}
