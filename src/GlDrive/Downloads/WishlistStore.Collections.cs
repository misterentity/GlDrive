namespace GlDrive.Downloads;

public partial class WishlistStore
{
    // Shared by ordinary adds and atomic collection adds. Callers hold the shared
    // state lock and publish Changed only after releasing it.
    private void AddCore(WishlistItem item)
    {
        NormalizeQualityState(item);
        _state.Items.Add(item);
        Save();
    }

    internal CollectionGapAddResult AddCollectionMovie(TmdbCollectionFilm film, QualityProfile quality)
    {
        if (film.TmdbId <= 0 || string.IsNullOrWhiteSpace(film.Title))
            throw new ArgumentException("A collection movie needs an identity and title.", nameof(film));
        if (!Enum.IsDefined(quality)) throw new ArgumentOutOfRangeException(nameof(quality));
        if (film.Year is not (>= 1 and <= 9999)) return new("year-required", null);
        WishlistItem added;
        lock (_state.Sync)
        {
            EnsureLoaded();
            var existing = _state.Items.FirstOrDefault(item => CollectionGapFinder.MatchesWishlist(film, item));
            if (existing != null) return new("already-wanted", existing.Id);
            added = new WishlistItem
            {
                Type = MediaType.Movie, TmdbId = film.TmdbId, Title = film.Title, Year = film.Year,
                Quality = quality
            };
            // The regular add normalization sets the legacy profile. Source-floor
            // permission remains null so the current configured default still applies.
            AddCore(added);
        }
        _state.NotifyChanged();
        return new("added", added.Id);
    }
}
