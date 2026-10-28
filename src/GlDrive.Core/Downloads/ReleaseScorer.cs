using System.Text.RegularExpressions;

namespace GlDrive.Downloads;

public sealed record ReleaseScore(bool Accepted, int Score, bool MeetsCutoff, string Reason,
    ReleaseTraits Traits, int QualityScore);

/// <summary>Deterministic quality decisions without network or filesystem access.</summary>
public static class ReleaseScorer
{
    private const int MaximumNameLength = 1024;
    private const int MaximumFormats = 32;
    private const int MaximumPatternLength = 512;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    public static ReleaseScore Score(string releaseName, ScoredQualityProfile profile, long? sizeBytes = null)
    {
        ArgumentNullException.ThrowIfNull(releaseName);
        ArgumentNullException.ThrowIfNull(profile);
        // Bound untrusted names before any parser/regex work.
        var traits = ReleaseTraits.Parse(releaseName.Length <= MaximumNameLength ? releaseName : "");
        ReleaseScore Reject(string reason) => new(false, 0, false, reason, traits, 0);
        if (string.IsNullOrWhiteSpace(releaseName) || releaseName.Length > MaximumNameLength)
            return Reject("invalid release name length");
        var errors = ValidateProfile(profile);
        if (errors.Count != 0) return Reject("invalid profile: " + errors[0]);
        if (traits.Resolution == QualityProfile.Any && !profile.AllowUnknownResolution)
            return Reject("resolution is unknown");
        if (traits.Resolution != QualityProfile.Any && profile.AllowedResolutions.Count != 0 &&
            !profile.AllowedResolutions.Contains(traits.Resolution)) return Reject("resolution is not allowed");
        if (traits.FullDisc && !profile.AllowFullDisc) return Reject("full disc is not allowed");
        if (traits.LowQualitySource && !profile.AllowLowQualitySources) return Reject("CAM/TS/TC source is not allowed");
        if (sizeBytes is <= 0) sizeBytes = null;
        if (!sizeBytes.HasValue && !profile.AllowUnknownSize && (profile.MinimumBytes.HasValue || profile.MaximumBytes.HasValue))
            return Reject("size is unknown");
        if (sizeBytes < profile.MinimumBytes) return Reject("size is below the minimum");
        if (sizeBytes > profile.MaximumBytes) return Reject("size exceeds the maximum");

        var score = SourceScore(traits.Source) + ResolutionScore(traits.Resolution) +
            (traits.Remux ? 60 : traits.Untouched ? 40 : 0) +
            (traits.Hdr == ReleaseHdr.DolbyVision ? 15 : traits.Hdr != ReleaseHdr.Sdr ? 10 : 0) +
            (traits.Edition is "UNRATED" or "EXTENDED" or "DC" ? 30 : traits.Edition is "REMASTERED" or "IMAX" ? 20 : 0);
        foreach (var format in profile.CustomFormats)
        {
            try
            {
                if (CreateRegex(format.Pattern).IsMatch(releaseName)) score += format.Score;
            }
            catch (RegexMatchTimeoutException) { return Reject("custom format timed out"); }
        }
        var qualityScore = score;
        if (profile.PreferProperRepack && (traits.Proper || traits.RepackRevision > 0)) score += 20;
        if (score < profile.MinimumScore) return Reject("score is below the floor");
        return new(true, score, score >= profile.CutoffScore,
            $"{traits.Source}, {traits.Resolution}: score {score}" + (score >= profile.CutoffScore ? " meets cutoff" : " below cutoff"),
            traits, qualityScore);
    }

    public static bool IsImprovement(ReleaseScore candidate, ReleaseScore existing, ScoredQualityProfile profile)
    {
        if (!candidate.Accepted) return false;
        if (!existing.Accepted) return true;
        if (!profile.AllowUpgrades) return false;
        if (existing.MeetsCutoff) return profile.UpgradeProperRepackAfterCutoff &&
            IsCorrection(candidate, existing, profile) && candidate.QualityScore >= existing.QualityScore;
        return IsBetterCopy(candidate, existing, profile);
    }

    /// <summary>Compare copies that already arrived, independently of future acquisition settings.</summary>
    public static bool IsBetterCopy(ReleaseScore candidate, ReleaseScore existing, ScoredQualityProfile profile)
    {
        if (!candidate.Accepted) return false;
        if (!existing.Accepted) return true;
        // A different group's REPACK alone is not evidence of a better copy.
        return candidate.QualityScore > existing.QualityScore ||
            (IsCorrection(candidate, existing, profile) && candidate.QualityScore >= existing.QualityScore);
    }

    private static bool IsCorrection(ReleaseScore candidate, ReleaseScore existing, ScoredQualityProfile profile) =>
        profile.PreferProperRepack && SameTier(candidate.Traits, existing.Traits) &&
            ((candidate.Traits.Proper && !existing.Traits.Proper) ||
             (candidate.Traits.RepackRevision > existing.Traits.RepackRevision &&
              !string.IsNullOrEmpty(candidate.Traits.Group) &&
              candidate.Traits.Group.Equals(existing.Traits.Group, StringComparison.OrdinalIgnoreCase)));

    private static bool SameTier(ReleaseTraits left, ReleaseTraits right) =>
        left.Source == right.Source && left.Resolution == right.Resolution && left.Codec == right.Codec &&
        left.Hdr == right.Hdr && left.Remux == right.Remux && left.FullDisc == right.FullDisc && left.Edition == right.Edition;

    public static IReadOnlyList<string> ValidateProfile(ScoredQualityProfile? profile)
    {
        if (profile == null) return ["profile must not be null"];
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.Id) || profile.Id.Length > 128) errors.Add("id must contain 1–128 characters");
        if (profile.AllowedResolutions == null || profile.AllowedResolutions.Any(q => q == QualityProfile.Any || !Enum.IsDefined(q)))
            errors.Add("allowedResolutions must contain only SD/720p/1080p/2160p (empty permits all)");
        if (profile.MinimumBytes is <= 0 || profile.MaximumBytes is <= 0 || profile.MinimumBytes > profile.MaximumBytes)
            errors.Add("size bounds must be positive and minimum must not exceed maximum");
        if (profile.MinimumScore is < -100000 or > 100000 || profile.CutoffScore is < -100000 or > 100000 ||
            profile.CutoffScore < profile.MinimumScore) errors.Add("score floor/cutoff must be ordered and within ±100000");
        if (profile.CustomFormats == null || profile.CustomFormats.Count > MaximumFormats)
            errors.Add("at most 32 custom formats are allowed");
        else foreach (var format in profile.CustomFormats)
        {
            if (format == null || string.IsNullOrWhiteSpace(format.Pattern) || format.Pattern.Length > MaximumPatternLength ||
                format.Score is < -1000 or > 1000) { errors.Add("custom formats need a pattern of 1–512 characters and score within ±1000"); continue; }
            try { _ = CreateRegex(format.Pattern); }
            catch (ArgumentException) { errors.Add("custom format pattern is invalid"); }
            catch (NotSupportedException) { errors.Add("custom format pattern must support nonbacktracking matching"); }
        }
        return errors;
    }

    private static Regex CreateRegex(string pattern) => new(pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout);

    internal static int ResolutionScore(QualityProfile quality) => quality switch
    {
        QualityProfile.SD => 100, QualityProfile.Q720p => 200, QualityProfile.Q1080p => 300,
        QualityProfile.Q2160p => 400, _ => 0
    };

    private static int SourceScore(ReleaseSource source) => source switch
    {
        ReleaseSource.BluRay => 4000, ReleaseSource.Web => 3000, ReleaseSource.WebRip => 2800,
        ReleaseSource.Broadcast => 2000, ReleaseSource.Dvd => 1000, _ => 0
    };
}
