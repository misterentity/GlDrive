using System.Globalization;
using Serilog;

namespace GlDrive.Downloads;

/// <summary>Daily metadata refresh. It cannot perform FTP work or replace acquisition evidence.</summary>
public sealed class SeriesInventoryRefresher(WishlistStore wishlist,
    Func<int, CancellationToken, Task<TvMazeScheduleEpisode[]?>> fetchEpisodes,
    Func<DateTimeOffset>? utcNow = null)
{
    internal const int MaximumEpisodes = 20000;
    private int _refreshing;
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public async Task<int> RefreshDueAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return 0;
        try
        {
            var refreshed = 0;
            foreach (var item in wishlist.Items)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!wishlist.TryBeginSeriesRefresh(item.Id, _utcNow(), out var showId)) continue;
                    var response = await fetchEpisodes(showId, ct).ConfigureAwait(false);
                    if (response == null) continue;
                    var inventory = ConvertEpisodes(response);
                    if (inventory != null && wishlist.ApplySeriesInventory(item.Id, showId, inventory, _utcNow())) refreshed++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Log.Warning(ex, "Series episode inventory refresh failed"); }
            }
            return refreshed;
        }
        finally { Volatile.Write(ref _refreshing, 0); }
    }

    internal static bool IsDue(DateTimeOffset? lastAttempt, DateTimeOffset now) =>
        !lastAttempt.HasValue || lastAttempt > now.AddDays(1) || now - lastAttempt >= TimeSpan.FromDays(1);

    internal static IReadOnlyList<SeriesEpisode>? ConvertEpisodes(IReadOnlyList<TvMazeScheduleEpisode> response)
    {
        if (response.Count == 0 || response.Count > MaximumEpisodes) return null;
        var episodes = new List<SeriesEpisode>();
        var identities = new HashSet<(int, int)>();
        foreach (var episode in response)
        {
            // TVMaze omits specials by default; unnumbered specials must not be
            // guessed into the regular season's numbering.
            if (episode == null) return null;
            if (episode.Season <= 0 || episode.Number is not > 0) continue;
            if (episode.Season > 999 || episode.Number > 999 || !identities.Add((episode.Season, episode.Number.Value))) return null;
            DateOnly? date = DateOnly.TryParseExact(episode.Airdate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var airDate) ? airDate : null;
            DateTimeOffset? stamp = DateTimeOffset.TryParse(episode.Airstamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var airStamp) ? airStamp.ToUniversalTime() : null;
            episodes.Add(new SeriesEpisode
            {
                TvMazeEpisodeId = episode.Id, Season = episode.Season, Number = episode.Number.Value,
                Name = episode.Name?.Length > 512 ? episode.Name[..512] : episode.Name ?? "",
                AirDate = date, AirAtUtc = stamp
            });
        }
        return episodes.Count == 0 ? null : episodes.OrderBy(e => e.Season).ThenBy(e => e.Number).ToArray();
    }

    internal static SeriesEpisode Clone(SeriesEpisode episode) => new()
    {
        TvMazeEpisodeId = episode.TvMazeEpisodeId, Season = episode.Season, Number = episode.Number,
        Name = episode.Name, AirDate = episode.AirDate, AirAtUtc = episode.AirAtUtc
    };
}
