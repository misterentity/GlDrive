using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

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
