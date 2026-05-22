using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public class PathSanitizerTests
{
    [Theory]
    [InlineData("normal.name", "normal.name")]
    [InlineData("bad:name?", "badname")]
    [InlineData("a/b\\c", "abc")]
    [InlineData("  trimmed  ", "trimmed")]
    public void Sanitizes_illegal_chars(string input, string expected)
        => Assert.Equal(expected, PathSanitizer.Sanitize(input));

    [Fact]
    public void Reserved_device_names_are_escaped()
    {
        var r = PathSanitizer.Sanitize("CON");
        Assert.NotEqual("CON", r);   // must not produce a raw reserved name
    }

    [Fact]
    public void Empty_becomes_placeholder()
        => Assert.Equal("_", PathSanitizer.Sanitize("   "));
}

public class SceneNameParserTests
{
    [Fact]
    public void Parses_tv_episode()
    {
        var p = SceneNameParser.Parse("MasterChef.US.S16E05.1080p.WEB.h264-EDITH");
        Assert.Equal(16, p.Season);
        Assert.Equal(5, p.Episode);
        Assert.Equal(QualityProfile.Q1080p, p.Quality);
    }

    [Fact]
    public void Parses_2160p_movie_quality()
    {
        var p = SceneNameParser.Parse("Sinners.2026.2160p.UHD.BluRay.x265-LIGHTBRiNGER");
        Assert.Equal(QualityProfile.Q2160p, p.Quality);
        Assert.Equal(2026, p.Year);
    }

    [Fact]
    public void Extracts_group()
    {
        var p = SceneNameParser.Parse("Some.Release.720p.WEB.H264-GRACE");
        Assert.Equal("GRACE", p.Group);
        Assert.Equal(QualityProfile.Q720p, p.Quality);
    }
}
