using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public class ReleaseScorerTests
{
    [Theory]
    [InlineData("Film.2026.2160p.UHD.BluRay.REMUX.DV.HEVC-GRP", QualityProfile.Q2160p, ReleaseSource.BluRay, ReleaseCodec.H265, ReleaseHdr.DolbyVision, true, false)]
    [InlineData("Film.2026.1080p.WEB-DL.H264-GRP", QualityProfile.Q1080p, ReleaseSource.Web, ReleaseCodec.H264, ReleaseHdr.Sdr, false, false)]
    [InlineData("Film.2026.720p.WEBRip.x264-GRP", QualityProfile.Q720p, ReleaseSource.WebRip, ReleaseCodec.X264, ReleaseHdr.Sdr, false, false)]
    [InlineData("Film.2026.COMPLETE.BLURAY-GRP", QualityProfile.Any, ReleaseSource.BluRay, ReleaseCodec.Unknown, ReleaseHdr.Sdr, false, true)]
    [InlineData("Film.2026.1080p.HDTV.x264-GRP", QualityProfile.Q1080p, ReleaseSource.Broadcast, ReleaseCodec.X264, ReleaseHdr.Sdr, false, false)]
    [InlineData("Film.2026.BDRip.x264-GRP", QualityProfile.Any, ReleaseSource.Dvd, ReleaseCodec.X264, ReleaseHdr.Sdr, false, false)]
    [InlineData("Film.2026.2160p.HDR10+.WEB.H265-GRP", QualityProfile.Q2160p, ReleaseSource.Web, ReleaseCodec.H265, ReleaseHdr.Hdr10Plus, false, false)]
    [InlineData("Film.2026.2160p.HLG.WEB.x265-GRP", QualityProfile.Q2160p, ReleaseSource.Web, ReleaseCodec.X265, ReleaseHdr.Hlg, false, false)]
    public void Parses_quality_traits(string name, QualityProfile resolution, ReleaseSource source, ReleaseCodec codec,
        ReleaseHdr hdr, bool remux, bool disc)
    {
        var traits = ReleaseTraits.Parse(name);
        Assert.Equal(resolution, traits.Resolution);
        Assert.Equal(source, traits.Source);
        Assert.Equal(codec, traits.Codec);
        Assert.Equal(hdr, traits.Hdr);
        Assert.Equal(remux, traits.Remux);
        Assert.Equal(disc, traits.FullDisc);
    }

    [Theory]
    [InlineData("Film.2026.1080p.HDCAM.WEB.H264-GRP", ReleaseSource.Cam)]
    [InlineData("Film.2026.1080p.HDTS.BluRay.x264-GRP", ReleaseSource.Telesync)]
    [InlineData("Film.2026.1080p.TELECINE.H264-GRP", ReleaseSource.Telecine)]
    public void Rejects_bad_sources_even_with_attractive_tokens(string name, ReleaseSource source)
    {
        var score = ReleaseScorer.Score(name, ScoredQualityProfile.Recommended(QualityProfile.Q1080p));
        Assert.Equal(source, score.Traits.Source);
        Assert.False(score.Accepted);
        Assert.Contains("CAM/TS/TC", score.Reason);
    }

    [Fact]
    public void Tokens_are_bounded_and_group_does_not_change_quality()
    {
        var traits = ReleaseTraits.Parse("Scampi.Webster.2026.1080p.WEB.H264-CAM");
        Assert.Equal(ReleaseSource.Web, traits.Source);
        Assert.Equal("CAM", traits.Group);
    }

    [Theory]
    [InlineData("Cam.2026.1080p.WEB.H264-GRP")]
    [InlineData("Complete.Strangers.2026.1080p.WEB.H264-GRP")]
    [InlineData("Unrated.S01E02.1080p.WEB.H264-GRP")]
    [InlineData("Complete.S01.COMPLETE.1080p.WEB.H264-GRP")]
    [InlineData("Complete.S01.COMPLETE.UHD.WEB.H265-GRP")]
    public void Title_words_and_complete_web_seasons_are_not_format_tokens(string name)
    {
        var traits = ReleaseTraits.Parse(name);
        Assert.Equal(ReleaseSource.Web, traits.Source);
        Assert.False(traits.FullDisc);
        Assert.Null(traits.Edition);
    }

    [Theory]
    [InlineData("Film.2026.COMPLETE.BLURAY-GRP", "resolution is unknown")]
    [InlineData("Film.2026.BDRip.x264-GRP", "resolution is unknown")]
    [InlineData("Film.2026.1080p.COMPLETE.BLURAY-GRP", "full disc is not allowed")]
    [InlineData("Film.2026.720p.BluRay.x264-GRP", "resolution is not allowed")]
    public void Recommended_profile_has_explicit_floors(string name, string reason)
    {
        var score = ReleaseScorer.Score(name, ScoredQualityProfile.Recommended(QualityProfile.Q1080p));
        Assert.False(score.Accepted);
        Assert.Equal(reason, score.Reason);
    }

    [Theory]
    [InlineData("Film.2026.COMPLETE.BLURAY-GRP", true)]
    [InlineData("Film.2026.BDRip.x264-GRP", true)]
    [InlineData("Film.2026.1080p.HDCAM-GRP", true)]
    [InlineData("Film.2026.1080p.WEB.H264-GRP", true)]
    [InlineData("Film.2026.2160p.WEB.H265-GRP", false)]
    public void Legacy_profile_preserves_the_resolution_acceptance_set(string name, bool expected)
    {
        var score = ReleaseScorer.Score(name, ScoredQualityProfile.Legacy(QualityProfile.Q1080p));
        Assert.Equal(expected, score.Accepted);
        Assert.Equal(expected, score.MeetsCutoff);
    }

    [Theory]
    [InlineData("1080p.BluRay.x264", "1080p.WEB.x264")]
    [InlineData("1080p.BluRay.x264", "720p.BluRay.x264")]
    [InlineData("1080p.BluRay.H264", "1080p.BluRay.x264")]
    [InlineData("1080p.BluRay.REMUX.H264", "1080p.BluRay.H264")]
    [InlineData("1080p.BluRay.UNRATED.x264", "1080p.BluRay.THEATRICAL.x264")]
    [InlineData("2160p.BluRay.HDR.x265", "2160p.BluRay.x265")]
    public void Seed_ranking_is_explainable_and_ordered(string better, string worse)
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Any);
        profile.CutoffScore = 10000;
        var left = ReleaseScorer.Score($"Film.2026.{better}-GRP", profile);
        var right = ReleaseScorer.Score($"Film.2026.{worse}-GRP", profile);
        Assert.True(left.Score > right.Score);
        Assert.True(ReleaseScorer.IsImprovement(left, right, profile));
        Assert.False(ReleaseScorer.IsImprovement(right, left, profile));
        Assert.Contains($"score {left.Score}", left.Reason);
    }

    [Fact]
    public void Cutoff_stops_later_upgrades_and_equal_variants()
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        var web = ReleaseScorer.Score("Film.2026.1080p.WEB.H264-ONE", profile);
        Assert.True(web.MeetsCutoff);
        Assert.False(ReleaseScorer.IsImprovement(ReleaseScorer.Score("Film.2026.1080p.WEB.H264-TWO", profile), web, profile));
        Assert.False(ReleaseScorer.IsImprovement(ReleaseScorer.Score("Film.2026.1080p.BluRay.REMUX.H264-ONE", profile), web, profile));
    }

    [Fact]
    public void Seven_variants_result_in_one_accepted_copy_at_cutoff()
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        string[] variants = ["1080p.WEB.H264-ONE", "1080p.WEB.H264-TWO", "COMPLETE.BLURAY-THREE",
            "MULTI.COMPLETE.BLURAY-FOUR", "BDRip.x264-FIVE", "1080p.PROPER.WEB.H264-SIX", "1080p.BluRay.x264-SEVEN"];
        ReleaseScore? best = null;
        var grabs = 0;
        foreach (var variant in variants)
        {
            var score = ReleaseScorer.Score("Synthetic.Journey.2026." + variant, profile);
            if (!score.Accepted || (best != null && !ReleaseScorer.IsImprovement(score, best, profile))) continue;
            best = score;
            grabs++;
        }
        Assert.Equal(1, grabs);
    }

    [Theory]
    [InlineData("PROPER.WEB.H264-TWO", true)]
    [InlineData("REPACK.WEB.H264-ONE", true)]
    [InlineData("REPACK.WEB.H264-TWO", false)]
    [InlineData("PROPER.WEB.x264-ONE", false)]
    [InlineData("PROPER.HDTV.H264-ONE", false)]
    public void Corrections_require_same_tier_and_repack_requires_same_group(string candidate, bool expected)
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.UpgradeProperRepackAfterCutoff = true;
        Assert.Equal(expected, ReleaseScorer.IsImprovement(
            ReleaseScorer.Score("Film.2026.1080p." + candidate, profile),
            ReleaseScorer.Score("Film.2026.1080p.WEB.H264-ONE", profile), profile));
    }

    [Fact]
    public void Repack_revision_is_monotonic()
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.UpgradeProperRepackAfterCutoff = true;
        var first = ReleaseScorer.Score("Film.2026.1080p.REPACK.WEB.H264-ONE", profile);
        var second = ReleaseScorer.Score("Film.2026.1080p.REPACK2.WEB.H264-ONE", profile);
        Assert.True(ReleaseScorer.IsImprovement(second, first, profile));
        Assert.False(ReleaseScorer.IsImprovement(first, second, profile));
    }

    [Theory]
    [InlineData("REPACK", 1)]
    [InlineData("REPACK1", 1)]
    [InlineData("REPACK2", 2)]
    [InlineData("REPACK10", 10)]
    [InlineData("RERIP3", 3)]
    public void Correction_revisions_parse_without_losing_double_digits(string token, int revision)
    {
        Assert.Equal(revision, ReleaseTraits.Parse($"Film.2026.{token}.1080p.WEB.H264-GRP").RepackRevision);
    }

    [Fact]
    public void After_cutoff_correction_does_not_override_custom_format_penalty()
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.UpgradeProperRepackAfterCutoff = true;
        profile.CustomFormats = [new() { Pattern = "-TWO$", Score = -100 }];
        Assert.False(ReleaseScorer.IsImprovement(
            ReleaseScorer.Score("Film.2026.1080p.PROPER.WEB.H264-TWO", profile),
            ReleaseScorer.Score("Film.2026.1080p.WEB.H264-ONE", profile), profile));
    }

    [Fact]
    public void Owned_copy_comparison_retains_manual_revision_without_reopening_acquisition()
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.AllowUpgrades = false;
        var original = ReleaseScorer.Score("Film.2026.1080p.REPACK.WEB.H264-ONE", profile);
        var revision = ReleaseScorer.Score("Film.2026.1080p.REPACK2.WEB.H264-ONE", profile);
        Assert.Equal(original.Score, revision.Score);
        Assert.False(ReleaseScorer.IsImprovement(revision, original, profile));
        Assert.True(ReleaseScorer.IsBetterCopy(revision, original, profile));
        Assert.False(ReleaseScorer.IsBetterCopy(original, revision, profile));
        Assert.False(ReleaseScorer.IsBetterCopy(
            ReleaseScorer.Score("Film.2026.1080p.REPACK2.WEB.H264-TWO", profile), original, profile));
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData(0L, false, false)]
    [InlineData(-1L, false, false)]
    [InlineData(99L, true, false)]
    [InlineData(100L, true, true)]
    [InlineData(200L, true, true)]
    [InlineData(201L, true, false)]
    public void Size_band_handles_unknown_and_inclusive_bounds(long? size, bool allowUnknown, bool accepted)
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.MinimumBytes = 100;
        profile.MaximumBytes = 200;
        profile.AllowUnknownSize = allowUnknown;
        Assert.Equal(accepted, ReleaseScorer.Score("Film.2026.1080p.WEB.H264-GRP", profile, size).Accepted);
    }

    [Fact]
    public void Signed_custom_formats_can_set_floor_and_cutoff()
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.MinimumScore = 3300;
        profile.CustomFormats = [new() { Pattern = "MULTI", Score = -100 }, new() { Pattern = "UNRATED", Score = 100 }];
        Assert.False(ReleaseScorer.Score("Film.2026.MULTI.1080p.WEB.H264-GRP", profile).Accepted);
        Assert.True(ReleaseScorer.Score("Film.2026.UNRATED.1080p.WEB.H264-GRP", profile).MeetsCutoff);
    }

    [Theory]
    [InlineData("[")]
    [InlineData(@"(?<=foo)bar")]
    [InlineData(@"(foo)\1")]
    public void Unsafe_or_invalid_custom_patterns_fail_closed(string pattern)
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.CustomFormats = [new() { Pattern = pattern }];
        Assert.NotEmpty(ReleaseScorer.ValidateProfile(profile));
        Assert.False(ReleaseScorer.Score("Film.2026.1080p.WEB.H264-GRP", profile).Accepted);
    }

    [Fact]
    public void Malformed_profile_lists_fail_closed()
    {
        Assert.Single(ReleaseScorer.ValidateProfile(null));
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.AllowedResolutions = null!;
        profile.CustomFormats = null!;
        Assert.Equal(2, ReleaseScorer.ValidateProfile(profile).Count);
        Assert.False(ReleaseScorer.Score("Film.2026.1080p.WEB.H264-GRP", profile).Accepted);
        profile.AllowedResolutions = [QualityProfile.Any, (QualityProfile)99];
        profile.CustomFormats = [null!];
        Assert.Equal(2, ReleaseScorer.ValidateProfile(profile).Count);
    }

    [Fact]
    public void Invalid_bounds_and_excessive_formats_are_rejected()
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.Id = "";
        profile.MinimumBytes = 200;
        profile.MaximumBytes = 100;
        profile.MinimumScore = 4000;
        profile.CutoffScore = 3000;
        profile.CustomFormats = Enumerable.Range(0, 33).Select(_ => new QualityCustomFormat { Pattern = "x" }).ToList();
        Assert.Equal(4, ReleaseScorer.ValidateProfile(profile).Count);
    }

    [Fact]
    public void Upgrade_toggle_and_invalid_existing_copy_have_explicit_semantics()
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.AllowUpgrades = false;
        var accepted = ReleaseScorer.Score("Film.2026.1080p.WEB.H264-GRP", profile);
        var lower = ReleaseScorer.Score("Film.2026.1080p.HDTV.x264-GRP", profile);
        var rejected = ReleaseScorer.Score("Film.2026.1080p.HDCAM-GRP", profile);
        Assert.False(ReleaseScorer.IsImprovement(accepted, lower, profile));
        Assert.True(ReleaseScorer.IsImprovement(accepted, rejected, profile));
        Assert.False(ReleaseScorer.IsImprovement(rejected, accepted, profile));
    }

    [Fact]
    public void Untrusted_long_name_is_rejected_before_parsing()
    {
        Assert.False(ReleaseScorer.Score(new string('x', 1025), ScoredQualityProfile.Recommended(QualityProfile.Q1080p)).Accepted);
    }
}
