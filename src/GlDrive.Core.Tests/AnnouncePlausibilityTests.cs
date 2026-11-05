using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the prose-as-announce false positive (v3.10.118).
///
/// Production 2026-09-14 14:01:17: a FiSH-decrypted #SITEC-SPAM line
/// "Plot: A father-to-be tries to figure out what is happening with all this AI insanity."
/// matched the user's catch-all rule (?&lt;section&gt;\w[\w-]*)\s+(?&lt;release&gt;\S+) as
/// section "A" / release "father-to-be". IsPlausibleReleaseName accepted it because a hyphen
/// counted as "structure", so an auto-race launched, probed 23 base paths on two servers,
/// and failed with "Release not found on any server".
///
/// The defining property of a scene release name is a structural token: a dot or underscore
/// separator, a digit (year, resolution, episode), or an uppercase letter (the group tag).
/// Hyphenated lowercase prose ("father-to-be", "state-of-the-art") carries none.
/// Validated against 24,881 matched announces in ai-data: exactly one rejected, the prose line.
/// </summary>
public class AnnouncePlausibilityTests
{
    [Theory]
    [InlineData("father-to-be")]
    [InlineData("state-of-the-art")]
    [InlineData("well-known-fact")]
    public void Hyphenated_lowercase_prose_is_rejected(string candidate)
        => Assert.False(ReleaseArtifactNames.IsPlausibleReleaseName(candidate));

    [Theory]
    [InlineData("Unrailed-RUNE")]                 // no dot / digit; uppercase group tag
    [InlineData("BLOODLETTER-TENOKE")]
    [InlineData("Eyewitness.2026.S01E03.1080p.WEB.H264-AFO")]
    [InlineData("Rockoon_One_More_Time-bADkARMA")]
    [InlineData("VA-Underground_Raw_Techno_Vol._4-16BIT-WEB-FLAC-2026-ROSiN")]
    [InlineData("Sangokushi_14_with_Power_Up_Kit_Complete_Edition_CHT_NSW-HR")]
    public void Real_scene_names_are_accepted(string candidate)
        => Assert.True(ReleaseArtifactNames.IsPlausibleReleaseName(candidate));

    [Theory]
    [InlineData("brings")]
    [InlineData("short")]
    [InlineData("")]
    public void Existing_rejections_still_hold(string candidate)
        => Assert.False(ReleaseArtifactNames.IsPlausibleReleaseName(candidate));

    // v3.19.2: sited #chan1 "AUTONUKE: TV/Nocturne...-CAKES created by altruist is still empty"
    // matched the user's "(?<section>\w[\w-]*)\s*:\s*(?<release>\S+)" rule as release
    // "TV/Nocturne...", and #SITEC-SPAM "Plot: Dr. Victor Frankenstein, a brilliant..." as
    // "Frankenstein,". A release is one directory name that ends in its group tag, so a path
    // separator / filename-illegal character or a non-alphanumeric tail is a capture of
    // something else. Checked against 2,350 distinct real names from 3 days of logs and race
    // history: only these two were rejected.
    [Theory]
    [InlineData("TV/Nocturne.2026.S01E01.2160p.WEB.H265-CAKES")]
    [InlineData(@"TV\Nocturne.2026.S01E01.2160p.WEB.H265-CAKES")]
    [InlineData("Nocturne.2026.S01E01.2160p.WEB.H265-CAKES/")]
    [InlineData("Show.2026.S01E01.1080p.WEB:H264-GRP")]
    [InlineData("Show.2026.S01E01.1080p.WEB.H264-GRP?")]
    [InlineData("Frankenstein,")]
    [InlineData("Eyewitness.2026.S01E03.1080p.WEB.H264-AFO.")]
    [InlineData("Eyewitness.2026.S01E03.1080p.WEB.H264-AFO;")]
    public void Paths_and_prose_tails_are_rejected(string candidate)
        => Assert.False(ReleaseArtifactNames.IsPlausibleReleaseName(candidate));

    [Theory]
    [InlineData("Human_Rebellion-Terror_Machine-(HRRDN)-WEB-2026-TR")]
    [InlineData("(HED)P.E.--Detox-CD-FLAC-2023-MEOWZiK")]
    [InlineData("Artist-Album-(CAT001)")]
    [InlineData("Some.Show.S01E01.1080p.WEB-DL[rartv]")]
    [InlineData("1000-lb.Sisters.S09E09.1080p.WEB.h264-EDITH")]
    public void Real_names_with_brackets_and_leading_digits_are_accepted(string candidate)
        => Assert.True(ReleaseArtifactNames.IsPlausibleReleaseName(candidate));

    // The invalid set is Windows' file-name set on every OS, so the Linux CI job judges
    // "TV\Name" (a Windows path) the same way the app does.
    [Fact]
    public void Invalid_characters_are_the_windows_file_name_set_on_every_os()
    {
        var invalid = new HashSet<char>("\"<>|:*?\\/");
        for (var c = (char)0; c < 32; c++) invalid.Add(c);
        if (OperatingSystem.IsWindows()) Assert.True(invalid.SetEquals(Path.GetInvalidFileNameChars()));
        for (var c = (char)0; c < 128; c++)
            Assert.Equal(!invalid.Contains(c), ReleaseArtifactNames.IsPlausibleReleaseName($"Some.Show.S01E01{c}1080p-GRP"));
    }
}
