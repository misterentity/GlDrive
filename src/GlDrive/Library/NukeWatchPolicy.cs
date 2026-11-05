using System.Text.RegularExpressions;
using GlDrive.Downloads;

namespace GlDrive.Library;

public static class NukeWatchPolicy
{
    private static readonly Regex DateToken = new(@"(?:^|[._ -])(?:19|20)\d{2}[._ -]\d{2}[._ -]\d{2}(?=$|[._ -])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool IsQualityNuke(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1024) return false;
        var words = Regex.Replace(reason.ToLowerInvariant(), "[^a-z0-9]+", " ",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50)).Trim();
        var padded = " " + words + " ";
        if (new[] { " not ", " no ", " false ", " unnuke", " un nuke" }.Any(padded.Contains)) return false;
        return new[] { "audio out of sync", "audio sync", "out of sync", "bad ivtc", "corrupt", "crc error",
            "bad frames", "bad aspect ratio", "missing audio", "wrong frame rate", "wrong framerate" }
            .Any(term => padded.Contains(" " + term + " ", StringComparison.Ordinal));
    }

    public static bool IsCorrection(string ownedRelease, string candidateRelease, ScoredQualityProfile profile,
        long? candidateBytes = null)
    {
        if (ownedRelease.Length > 1024 || candidateRelease.Length > 1024 ||
            string.Equals(ownedRelease, candidateRelease, StringComparison.OrdinalIgnoreCase) || !profile.AllowUpgrades) return false;
        var owned = Identify(ownedRelease);
        var candidate = Identify(candidateRelease);
        if (owned == null || candidate == null || owned.Item.Type != candidate.Item.Type ||
            TitleKey.Normalize(owned.Item.Title) != TitleKey.Normalize(candidate.Item.Title)) return false;
        if (owned.Item.Type == MediaType.Movie)
        {
            if (!owned.Item.Year.HasValue || owned.Item.Year != candidate.Item.Year) return false;
        }
        else
        {
            if (owned.Item.Year.HasValue && candidate.Item.Year.HasValue && owned.Item.Year != candidate.Item.Year) return false;
            if (!owned.Keys.SequenceEqual(candidate.Keys, StringComparer.Ordinal)) return false;
        }
        // Ownership already exists; a missing historic byte count must not erase its
        // comparable quality and make a penalized correction appear better.
        var comparison = new ScoredQualityProfile
        {
            Id = profile.Id, AllowedResolutions = profile.AllowedResolutions,
            AllowUnknownResolution = profile.AllowUnknownResolution, AllowFullDisc = profile.AllowFullDisc,
            AllowLowQualitySources = profile.AllowLowQualitySources, AllowUnknownSize = true,
            MinimumScore = -100000, CutoffScore = profile.CutoffScore,
            PreferProperRepack = profile.PreferProperRepack, CustomFormats = profile.CustomFormats
        };
        var oldScore = ReleaseScorer.Score(ownedRelease, comparison);
        var newScore = ReleaseScorer.Score(candidateRelease, profile, candidateBytes);
        return newScore.Accepted && oldScore.Accepted && oldScore.Traits.Resolution != QualityProfile.Any &&
            oldScore.Traits.Source != ReleaseSource.Unknown &&
            ReleaseScorer.IsCorrection(newScore, oldScore, profile) && newScore.QualityScore >= oldScore.QualityScore;
    }

    // Correction identity is broader than destructive single-file library import:
    // exact season packs, equal episode sets and dated episodes can be downloaded safely.
    internal static WishlistItem? PolicyItem(string release) => Identify(release)?.Item;

    private sealed record Identity(WishlistItem Item, IReadOnlyList<string> Keys);
    private static Identity? Identify(string release)
    {
        if (!NukeWatchStore.ValidRelease(release)) return null;
        var parsed = SceneNameParser.Parse(release);
        var seasons = EpisodeToken.FindAll(release);
        var dates = DateToken.Matches(release);
        if (seasons.Count > 1 || dates.Count > 1 || seasons.Count != 0 && dates.Count != 0) return null;
        var tv = seasons.Count != 0 || dates.Count != 0;
        var title = parsed.Title;
        var year = parsed.Year;
        if (seasons.Count == 1)
        {
            // Season-pack parsing leaves Sxx in the title; parse just the prefix.
            var prefix = release[..seasons[0].Index].TrimEnd('.', '_', ' ', '-');
            var prefixParsed = SceneNameParser.Parse(prefix);
            title = prefixParsed.Title;
            year = prefixParsed.Year;
        }
        else if (dates.Count == 1)
        {
            if (!SceneNameParser.TryEpisodeDate(release, out _)) return null;
            // The episode's calendar year is part of its episode key, not a remake year.
            var prefix = release[..dates[0].Index].TrimEnd('.', '_', ' ', '-');
            var prefixParsed = SceneNameParser.Parse(prefix);
            title = prefixParsed.Title;
            year = prefixParsed.Year;
        }
        if (TitleKey.Normalize(title).Length == 0 || !tv && !year.HasValue) return null;
        var item = new WishlistItem
        {
            Type = tv ? MediaType.TvShow : MediaType.Movie, Title = title, Year = year, Quality = parsed.Quality,
            ProfileId = ScoredQualityProfile.Legacy(parsed.Quality).Id
        };
        if (!tv) return new(item, []);
        // The shared wishlist parser expands ranges into exact episode keys. Use a
        // neutral series identity so punctuation differences cannot change the set.
        var keyItem = new WishlistItem { Type = MediaType.TvShow, Title = "correction" };
        IReadOnlyList<string> keys = seasons.Count == 1 && seasons[0].IsSeasonPack
            ? ["season:" + seasons[0].Season] : WishlistAcquisition.Keys(keyItem, release);
        return keys.Count == 0 ? null : new(item, keys.Order(StringComparer.Ordinal).ToArray());
    }
}
