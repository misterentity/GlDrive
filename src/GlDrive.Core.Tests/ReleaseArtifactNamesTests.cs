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

    [Theory]
    [InlineData("[incomplete]-Scrubs.2026.S02E06.1080p.HDTV.x264-SYNCOPY", true)]
    [InlineData("(no-sfv)-Suur.Seiklus.S01E08.EE.1080p.WEB.H264-EMX", true)]
    [InlineData("[no-nfo]-Taiuslik.Sobranna.S02E06.EE.1080p.WEB.H264-EMX", true)]
    [InlineData("{unseen-tag}-Release.Name-GRP", true)]
    [InlineData(".imdb", true)]
    [InlineData("-MISSING-release", true)]
    [InlineData("", true)]
    [InlineData("Scrubs.2026.S02E06.1080p.HDTV.x264-SYNCOPY", false)]
    [InlineData("1000-lb.Roomies.S02E10.1080p.WEB.h264-EDITH", false)]
    [InlineData("Nicolas_Giordano-The_Unison-(PS025)-SINGLE-WEB-2026-PTC", false)]
    [InlineData("Ärzte-Live-2026-GRP", false)]
    public void Section_entries_that_cannot_be_a_release_start_with_punctuation(string name, bool expected)
        => Assert.Equal(expected, ReleaseArtifactNames.IsSectionIndicatorName(name));
}
