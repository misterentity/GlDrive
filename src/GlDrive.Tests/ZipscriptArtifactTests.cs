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
    // Unix-hidden PSXC-IMDB state, regenerated per-site (v3.10.44). Observed live
    // 2026-07-29: 86 attempts/day, source RETR 550 "No such file or directory" and
    // dest STOR 553 "path-filter denied permission. (Filename deny)".
    [InlineData(".imdbinfoname", 0)]
    [InlineData(".imdbinfoname", 512)]        // non-zero size must not exempt it
    [InlineData(".imdb", 0)]
    [InlineData(".imdb", 4096)]
    [InlineData(".imdbinfo", 128)]
    [InlineData(".message", 0)]               // glftpd per-dir banner
    [InlineData(".htaccess", 64)]
    public void Detects_zipscript_artifacts(string name, long size)
        => Assert.True(SpreadJob.IsZipscriptArtifact(name, size));

    [Theory]
    [InlineData("release.r08", 15_000_000)]
    [InlineData("release.sfv", 1_200)]
    [InlineData("release.nfo", 4_096)]
    [InlineData("movie.mkv", 8_000_000_000)]
    [InlineData("normal-file-with-dashes.rar", 50_000_000)]   // leading word, not a marker
    // Dotted scene names are the norm — only a LEADING dot marks site-local state.
    [InlineData("Some.Show.S01E01.1080p.WEB.h264-GRP.mkv", 2_000_000_000)]
    [InlineData("some.show.s01e01.1080p.web.h264-grp.sfv", 900)]
    [InlineData("release.imdb.txt", 2_048)]                   // not one of the sidecar forms
    public void Allows_real_release_files(string name, long size)
        => Assert.False(SpreadJob.IsZipscriptArtifact(name, size));

    [Fact]
    public void Empty_name_is_not_artifact()
        => Assert.False(SpreadJob.IsZipscriptArtifact("", 0));
}
