using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Core.Tests;

public class ReleaseArtifactNamesTests
{
    [Theory]
    [InlineData("-MISSING-release.rar", true)]
    [InlineData("release.rar.missing", true)]
    [InlineData("release.r01-MISSING", true)]
    [InlineData("release.missing.rar", false)]
    [InlineData("missing-release.rar", false)]
    [InlineData("", false)]
    public void Missing_names_match_only_site_placeholder_forms(string name, bool expected)
        => Assert.Equal(expected, ReleaseArtifactNames.IsMissingPlaceholderName(name));

    [Theory]
    [InlineData(".imdbinfoname", true)]
    [InlineData(".message", true)]
    [InlineData("release.IMDB.HTML", true)]
    [InlineData("release.imdb.nfo", true)]
    [InlineData("release.imdb.txt", false)]
    [InlineData("Some.Show.1080p-GRP.mkv", false)]
    [InlineData("[ COMPLETE ]", false)]
    [InlineData("", false)]
    public void Site_metadata_names_do_not_classify_payload_or_status_markers(string name, bool expected)
        => Assert.Equal(expected, ReleaseArtifactNames.IsSiteMetadataName(name));

    [Theory]
    [InlineData("-stub", 0, true)]
    [InlineData("[status]", 0, true)]
    [InlineData("-stub", 1, false)]
    [InlineData("[status]", 1, false)]
    [InlineData("release.rar", 0, false)]
    [InlineData("", 0, false)]
    public void Zero_byte_marker_requires_both_marker_prefix_and_empty_file(string name, long size, bool expected)
        => Assert.Equal(expected, ReleaseArtifactNames.IsZeroByteMarker(name, size));
}
