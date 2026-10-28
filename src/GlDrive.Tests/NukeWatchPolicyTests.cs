using GlDrive.Downloads;
using GlDrive.Library;
using Xunit;

namespace GlDrive.Tests;

public sealed class NukeWatchPolicyTests
{
    private const string Owned = "Cinema.Sample.2026.1080p.WEB.H264-ALPHA";
    private const string Proper = "Cinema.Sample.2026.1080p.PROPER.WEB.H264-BETA";

    [Theory]
    [InlineData("audio out of sync")]
    [InlineData("AUDIO.OUT.OF.SYNC")]
    [InlineData("audio_sync")]
    [InlineData("bad-ivtc")]
    [InlineData("corrupt")]
    [InlineData("crc_error")]
    [InlineData("bad.frames")]
    [InlineData("bad_aspect_ratio")]
    [InlineData("missing.audio")]
    [InlineData("wrong_frame_rate")]
    public void Positive_quality_evidence_is_normalized(string reason) =>
        Assert.True(NukeWatchPolicy.IsQualityNuke(reason));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("incomplete")]
    [InlineData("missing_files")]
    [InlineData("bad_tag")]
    [InlineData("wrong_section")]
    [InlineData("dupe")]
    [InlineData("not corrupt")]
    [InlineData("NOT_CORRUPT")]
    [InlineData("no audio sync issue")]
    [InlineData("corruptionist")]
    public void Administrative_unknown_and_negated_reasons_are_not_quality_evidence(string? reason) =>
        Assert.False(NukeWatchPolicy.IsQualityNuke(reason));

    [Theory]
    [InlineData("Cinema.Sample.2026.1080p.PROPER.WEB.H264-BETA", true)]
    [InlineData("cinema_sample.2026.1080p.PROPER.WEB.H264-BETA", true)]
    [InlineData("Cinema.Sample.2026.1080p.REPACK.WEB.H264-ALPHA", true)]
    [InlineData("Cinema.Sample.2026.1080p.REPACK.WEB.H264-BETA", false)]
    [InlineData("Cinema.Sample.2026.1080p.WEB.H264-BETA", false)]
    [InlineData("Cinema.Sample.2026.1080p.PROPER.WEB.x264-BETA", false)]
    [InlineData("Cinema.Sample.2026.1080p.PROPER.HDTV.H264-BETA", false)]
    [InlineData("Cinema.Sample.2026.1080p.PROPER.WEB.DV.H264-BETA", false)]
    [InlineData("Cinema.Sample.2026.2160p.PROPER.WEB.H264-BETA", false)]
    [InlineData("Cinema.Sample.2026.1080p.PROPER.WEB.REMUX.H264-BETA", false)]
    [InlineData("Cinema.Sample.2026.1080p.PROPER.UNRATED.WEB.H264-BETA", false)]
    public void Correction_requires_same_quality_tier_and_correction_evidence(string candidate, bool expected) =>
        Assert.Equal(expected, NukeWatchPolicy.IsCorrection(Owned, candidate, ScoredQualityProfile.Recommended(QualityProfile.Any)));

    [Theory]
    [InlineData("Other.Sample.2026.1080p.PROPER.WEB.H264-BETA")]
    [InlineData("Cinema.Sample.2025.1080p.PROPER.WEB.H264-BETA")]
    [InlineData("Cinema.Sample.1080p.PROPER.WEB.H264-BETA")]
    [InlineData("Cinema.Sample.2026.S01E01.1080p.PROPER.WEB.H264-BETA")]
    [InlineData("")]
    public void Movie_identity_never_uses_title_only_or_cross_media_fallback(string candidate) =>
        Assert.False(NukeWatchPolicy.IsCorrection(Owned, candidate, ScoredQualityProfile.Recommended(QualityProfile.Q1080p)));

    [Fact]
    public void Unknown_owned_movie_year_cannot_authorize_a_correction() =>
        Assert.False(NukeWatchPolicy.IsCorrection("Cinema.Sample.1080p.WEB.H264-ALPHA", Proper,
            ScoredQualityProfile.Recommended(QualityProfile.Q1080p)));

    [Theory]
    [InlineData("Serial.Sample.S01E02.1080p.WEB.H264-ALPHA", "Serial.Sample.S01E02.1080p.PROPER.WEB.H264-BETA", true)]
    [InlineData("Serial.Sample.2026.S01E02.1080p.WEB.H264-ALPHA", "Serial.Sample.S01E02.1080p.PROPER.WEB.H264-BETA", true)]
    [InlineData("Serial.Sample.S01E02.1080p.WEB.H264-ALPHA", "Serial.Sample.2026.S01E02.1080p.PROPER.WEB.H264-BETA", true)]
    [InlineData("Serial.Sample.2024.S01E02.1080p.WEB.H264-ALPHA", "Serial.Sample.2026.S01E02.1080p.PROPER.WEB.H264-BETA", false)]
    [InlineData("Serial.Sample.S01E02.1080p.WEB.H264-ALPHA", "Serial.Sample.S01E03.1080p.PROPER.WEB.H264-BETA", false)]
    [InlineData("Serial.Sample.S01E02.1080p.WEB.H264-ALPHA", "Serial.Sample.S02E02.1080p.PROPER.WEB.H264-BETA", false)]
    [InlineData("Serial.Sample.S01.COMPLETE.1080p.WEB.H264-ALPHA", "Serial.Sample.S01.COMPLETE.1080p.PROPER.WEB.H264-BETA", true)]
    [InlineData("Serial.Sample.S01E02E03.1080p.WEB.H264-ALPHA", "Serial.Sample.S01E02E03.1080p.PROPER.WEB.H264-BETA", true)]
    [InlineData("Serial.Sample.2026.01.02.1080p.WEB.H264-ALPHA", "Serial.Sample.2026.01.02.1080p.PROPER.WEB.H264-BETA", true)]
    [InlineData("Serial.Sample.S01.COMPLETE.1080p.WEB.H264-ALPHA", "Serial.Sample.S02.COMPLETE.1080p.PROPER.WEB.H264-BETA", false)]
    [InlineData("Serial.Sample.S01E02E03.1080p.WEB.H264-ALPHA", "Serial.Sample.S01E02E04.1080p.PROPER.WEB.H264-BETA", false)]
    [InlineData("Serial.Sample.S01E02-E04.1080p.WEB.H264-ALPHA", "Serial.Sample.S01E02E03E04.1080p.PROPER.WEB.H264-BETA", true)]
    [InlineData("Serial.Sample.S01E02E03E04.1080p.WEB.H264-ALPHA", "Serial.Sample.S01E02-E04.1080p.PROPER.WEB.H264-BETA", true)]
    [InlineData("Serial.Sample.S01E02-E04.1080p.WEB.H264-ALPHA", "Serial.Sample.S01E02E04.1080p.PROPER.WEB.H264-BETA", false)]
    [InlineData("Serial.Sample.2026.01.02.1080p.WEB.H264-ALPHA", "Serial.Sample.2026.01.03.1080p.PROPER.WEB.H264-BETA", false)]
    [InlineData("Serial.Sample.2026.02.30.1080p.WEB.H264-ALPHA", "Serial.Sample.2026.02.30.1080p.PROPER.WEB.H264-BETA", false)]
    [InlineData("Serial.Sample.2024.S01.COMPLETE.1080p.WEB.H264-ALPHA", "Serial.Sample.2026.S01.COMPLETE.1080p.PROPER.WEB.H264-BETA", false)]
    [InlineData("Serial.Sample.S01.COMPLETE.1080p.WEB.H264-ALPHA", "Serial.Sample.2026.S01.COMPLETE.1080p.PROPER.WEB.H264-BETA", true)]
    [InlineData("Serial.Sample.2024.S01E02E03.1080p.WEB.H264-ALPHA", "Serial.Sample.2026.S01E02E03.1080p.PROPER.WEB.H264-BETA", false)]
    [InlineData("Serial.Sample.2020.2026.01.02.1080p.WEB.H264-ALPHA", "Serial.Sample.2021.2026.01.02.1080p.PROPER.WEB.H264-BETA", false)]
    public void Tv_corrections_require_matching_episode_or_pack_identity_without_a_known_remake_conflict(string owned, string candidate, bool expected) =>
        Assert.Equal(expected, NukeWatchPolicy.IsCorrection(owned, candidate, ScoredQualityProfile.Recommended(QualityProfile.Q1080p)));

    [Theory]
    [InlineData("REPACK", "REPACK2", "ALPHA", true)]
    [InlineData("REPACK2", "REPACK10", "alpha", true)]
    [InlineData("REPACK2", "REPACK2", "ALPHA", false)]
    [InlineData("REPACK2", "REPACK", "ALPHA", false)]
    [InlineData("REPACK", "REPACK2", "BETA", false)]
    [InlineData("PROPER", "PROPER", "BETA", false)]
    public void Existing_corrections_require_positive_new_revision_evidence(string ownedToken, string candidateToken, string candidateGroup, bool expected) =>
        Assert.Equal(expected, NukeWatchPolicy.IsCorrection(
            $"Cinema.Sample.2026.1080p.{ownedToken}.WEB.H264-ALPHA",
            $"Cinema.Sample.2026.1080p.{candidateToken}.WEB.H264-{candidateGroup}",
            ScoredQualityProfile.Recommended(QualityProfile.Q1080p)));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void Explicit_quality_preferences_remain_authoritative(bool allowUpgrades, bool preferCorrections, bool expected)
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.AllowUpgrades = allowUpgrades;
        profile.PreferProperRepack = preferCorrections;
        profile.UpgradeProperRepackAfterCutoff = false;
        Assert.True(ReleaseScorer.Score(Owned, profile).MeetsCutoff);
        Assert.Equal(expected, NukeWatchPolicy.IsCorrection(Owned, Proper, profile));
    }

    [Theory]
    [InlineData("Cinema.Sample.2026.WEB.H264-ALPHA", "Cinema.Sample.2026.PROPER.WEB.H264-BETA")]
    [InlineData("Cinema.Sample.2026.1080p.H264-ALPHA", "Cinema.Sample.2026.1080p.PROPER.H264-BETA")]
    public void Unknown_resolution_or_source_cannot_authorize_an_automatic_correction(string owned, string candidate) =>
        Assert.False(NukeWatchPolicy.IsCorrection(owned, candidate, ScoredQualityProfile.Legacy(QualityProfile.Any)));

    [Theory]
    [InlineData(null, false)]
    [InlineData(99L, false)]
    [InlineData(100L, true)]
    [InlineData(200L, true)]
    [InlineData(201L, false)]
    public void Candidate_size_acceptance_is_preserved(long? bytes, bool expected)
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.AllowUnknownSize = false;
        profile.MinimumBytes = 100;
        profile.MaximumBytes = 200;
        Assert.Equal(expected, NukeWatchPolicy.IsCorrection(Owned, Proper, profile, bytes));
    }

    [Fact]
    public void Rejected_candidate_score_and_invalid_profile_cannot_authorize_a_correction()
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.MinimumScore = 3300;
        profile.CustomFormats = [new() { Pattern = "-BETA$", Score = -100 }];
        Assert.False(NukeWatchPolicy.IsCorrection(Owned, Proper, profile));
        profile.CustomFormats = [new() { Pattern = "[", Score = 0 }];
        Assert.False(NukeWatchPolicy.IsCorrection(Owned, Proper, profile));
    }

    [Fact]
    public void Correction_label_does_not_override_an_accepted_custom_quality_penalty()
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.CustomFormats = [new() { Pattern = "-BETA$", Score = -100 }];
        Assert.True(ReleaseScorer.Score(Proper, profile).Accepted);
        Assert.False(NukeWatchPolicy.IsCorrection(Owned, Proper, profile));
    }

    [Fact]
    public void Unknown_owned_size_does_not_erase_comparable_quality_when_candidate_has_a_penalty()
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        profile.AllowUnknownSize = false;
        profile.MinimumBytes = 100;
        profile.MaximumBytes = 200;
        profile.CustomFormats = [new() { Pattern = "-BETA$", Score = -100 }];
        Assert.False(ReleaseScorer.Score(Owned, profile).Accepted);
        Assert.True(ReleaseScorer.Score(Proper, profile, 150).Accepted);
        Assert.False(NukeWatchPolicy.IsCorrection(Owned, Proper, profile, 150));
        Assert.False(profile.AllowUnknownSize);
        Assert.Equal(100L, profile.MinimumBytes);
        Assert.Equal(200L, profile.MaximumBytes);
    }

    [Fact]
    public void Oversized_untrusted_evidence_cannot_authorize_work()
    {
        var profile = ScoredQualityProfile.Recommended(QualityProfile.Q1080p);
        Assert.False(NukeWatchPolicy.IsQualityNuke(new string('x', 1025) + " corrupt"));
        Assert.False(NukeWatchPolicy.IsCorrection(new string('x', 1025), Proper, profile));
        Assert.False(NukeWatchPolicy.IsCorrection(Owned, new string('x', 1025), profile));
    }
}
