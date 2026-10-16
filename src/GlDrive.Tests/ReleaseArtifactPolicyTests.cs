using GlDrive.Downloads;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

public class ReleaseArtifactPolicyTests
{
    // A shared filename fact must not collapse consumer-specific status policies.
    [Theory]
    [InlineData("-MISSING-file.rar", 20, true, true, true)]
    [InlineData("file.rar.missing", 0, true, true, true)]
    [InlineData(".imdbinfoname", 512, true, true, false)]
    [InlineData("release.imdb.nfo", 1024, true, true, false)]
    [InlineData("[payload].mkv", 1024, false, true, false)]
    [InlineData("27%-Complete", 1024, true, false, false)]
    [InlineData("[ COMPLETE ]", 0, true, true, false)]
    [InlineData("-unknown", 0, true, true, true)]
    [InlineData("[unknown", 0, true, true, false)]
    [InlineData("-unknown", 1, false, false, false)]
    [InlineData("release.imdb.txt", 512, false, false, false)]
    [InlineData("Show.S01E01.1080p-GRP.mkv", 1024, false, false, false)]
    [InlineData("", 0, false, false, false)]
    public void Classification_preserves_each_consumers_existing_rules(
        string name, long size, bool downloadMetadata, bool raceArtifact, bool missingStub)
    {
        Assert.Equal(downloadMetadata, ReleaseSourceState.IsSiteMetadata(name, size));
        Assert.Equal(raceArtifact, SpreadJob.IsZipscriptArtifact(name, size));
        Assert.Equal(missingStub, CompletionDetector.IsMissingStub(name, size));
    }
}
