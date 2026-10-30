using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the prose-as-announce false positive (v3.10.118).
///
/// Production 2026-09-14 14:01:17: a FiSH-decrypted #SYN-SPAM line
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
        => Assert.False(IrcAnnounceListener.IsPlausibleReleaseName(candidate));

    [Theory]
    [InlineData("Unrailed-RUNE")]                 // no dot / digit; uppercase group tag
    [InlineData("BLOODLETTER-TENOKE")]
    [InlineData("Eyewitness.2026.S01E03.1080p.WEB.H264-AFO")]
    [InlineData("Rockoon_One_More_Time-bADkARMA")]
    [InlineData("VA-Underground_Raw_Techno_Vol._4-16BIT-WEB-FLAC-2026-ROSiN")]
    [InlineData("Sangokushi_14_with_Power_Up_Kit_Complete_Edition_CHT_NSW-HR")]
    public void Real_scene_names_are_accepted(string candidate)
        => Assert.True(IrcAnnounceListener.IsPlausibleReleaseName(candidate));

    [Theory]
    [InlineData("brings")]
    [InlineData("short")]
    [InlineData("")]
    public void Existing_rejections_still_hold(string candidate)
        => Assert.False(IrcAnnounceListener.IsPlausibleReleaseName(candidate));

    // v3.19.2: zephyr #ent "AUTONUKE: TV/Nocturne...-CAKES created by altruist is still empty"
    // matched the user's "(?<section>\w[\w-]*)\s*:\s*(?<release>\S+)" rule as release
    // "TV/Nocturne...", and #SYN-SPAM "Plot: Dr. Victor Frankenstein, a brilliant..." as
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
        => Assert.False(IrcAnnounceListener.IsPlausibleReleaseName(candidate));

    [Theory]
    [InlineData("Human_Rebellion-Terror_Machine-(HRRDN)-WEB-2026-TR")]
    [InlineData("(HED)P.E.--Detox-CD-FLAC-2023-MEOWZiK")]
    [InlineData("Artist-Album-(CAT001)")]
    [InlineData("Some.Show.S01E01.1080p.WEB-DL[rartv]")]
    [InlineData("1000-lb.Sisters.S09E09.1080p.WEB.h264-EDITH")]
    public void Real_names_with_brackets_and_leading_digits_are_accepted(string candidate)
        => Assert.True(IrcAnnounceListener.IsPlausibleReleaseName(candidate));
}
