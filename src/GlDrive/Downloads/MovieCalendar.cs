using GlDrive.Config;

namespace GlDrive.Downloads;

public sealed record MovieCalendarResult(string Status, string ItemId, MovieReleaseDates? Dates);

/// <summary>Explicit single-movie refresh. No timer, FTP traffic, or acquisition gates.</summary>
public sealed class MovieCalendar(DownloadConfig config, WishlistStore wishlist, Func<TmdbClient>? clientFactory = null)
{
    public async Task<MovieCalendarResult> RefreshAsync(string itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var item = wishlist.GetById(itemId) ?? throw new KeyNotFoundException("Unknown wishlist item");
        if (item.Type != MediaType.Movie) throw new ArgumentException("Release dates apply to movies");
        var identity = new MovieCalendarIdentity(item.TmdbId, item.ImdbId, item.Title, item.Year);
        using var client = clientFactory?.Invoke() ?? new TmdbClient(config.ResolveTmdbKey());
        var id = identity.TmdbId ?? await client.FindMovieByImdbId(identity.ImdbId, ct).ConfigureAwait(false);
        var dates = await client.GetReleaseDates(id ?? 0, config.MovieCalendarRegion, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (dates.Status == "unavailable") return new("unavailable", itemId, item.ReleaseDates);
        return wishlist.ApplyReleaseDates(itemId, identity, id, dates, ct)
            ? new(dates.Status, itemId, dates) : new("item-changed", itemId, null);
    }
}

internal sealed record MovieCalendarIdentity(int? TmdbId, string? ImdbId, string Title, int? Year);

public partial class WishlistStore
{
    internal bool ApplyReleaseDates(string itemId, MovieCalendarIdentity expected, int? tmdbId, MovieReleaseDates dates, CancellationToken ct)
    {
        if (dates.Status == "unavailable") return false;
        lock (_state.Sync)
        {
            ct.ThrowIfCancellationRequested();
            EnsureLoaded();
            var item = _state.Items.FirstOrDefault(i => i.Id == itemId);
            if (item == null || item.Type != MediaType.Movie ||
                new MovieCalendarIdentity(item.TmdbId, item.ImdbId, item.Title, item.Year) != expected) return false;
            item.TmdbId ??= tmdbId;
            item.ReleaseDates = dates;
            Save();
        }
        _state.NotifyChanged();
        return true;
    }
}
