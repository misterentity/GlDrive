using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class ZipscriptArtifactTests
{
    [Theory]
    [InlineData("-MISSING-foo.rar", 0)]
    [InlineData("-missing-foo.rar", 0)]
    [InlineData("foo.rar.missing", 0)]
    [InlineData("release.r08-missing", 0)]                    // dash-suffix form (v2.6.0)
    [InlineData("[###:::::::::::] - 27% Complete - [zephyr]", 0)]  // progress bar
    [InlineData("[ NUKED ] reason here", 0)]
    [InlineData("-somezerobyte", 0)]
    [InlineData("Ryan.Hamilton.This.Just.Hit.Me.2026.2160p.WEB.h265-EDITH.imdb.html", 8192)]  // site imdb sidecar
    [InlineData("release.imdb.nfo", 4096)]
    public void Detects_zipscript_artifacts(string name, long size)
        => Assert.True(SpreadJob.IsZipscriptArtifact(name, size));

    [Theory]
    [InlineData("release.r08", 15_000_000)]
    [InlineData("release.sfv", 1_200)]
    [InlineData("release.nfo", 4_096)]
    [InlineData("movie.mkv", 8_000_000_000)]
    [InlineData("normal-file-with-dashes.rar", 50_000_000)]   // leading word, not a marker
    public void Allows_real_release_files(string name, long size)
        => Assert.False(SpreadJob.IsZipscriptArtifact(name, size));

    [Fact]
    public void Empty_name_is_not_artifact()
        => Assert.False(SpreadJob.IsZipscriptArtifact("", 0));
}
