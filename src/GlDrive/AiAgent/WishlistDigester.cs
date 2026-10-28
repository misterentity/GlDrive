using System.Globalization;
using GlDrive.Downloads;

namespace GlDrive.AiAgent;

public sealed class WishlistDigester
{
    internal const int MaximumItemRows = 50;

    public WishlistDigest Build(IEnumerable<WishlistAttemptEvent> events,
        IEnumerable<WishlistItem>? wishlist = null, DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var list = events.Where(e => e != null && !string.IsNullOrWhiteSpace(e.WishlistItemId)).ToList();
        var d = new WishlistDigest();
        var items = (wishlist ?? []).Where(i => i != null && !string.IsNullOrWhiteSpace(i.Id))
            .GroupBy(i => i.Id).Select(g => g.First()).ToDictionary(i => i.Id);
        var byItem = list.GroupBy(e => e.WishlistItemId).ToDictionary(g => g.Key, g => g.ToArray());
        DateTimeOffset? At(WishlistAttemptEvent row) => DateTimeOffset.TryParse(row.Ts, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var at) && at <= now ? at : null;
        foreach (var g in byItem.OrderByDescending(g => g.Value.Select(At).Max()))
        {
            // Known cinema availability explains a lack of home-release matches. This is
            // advisory evidence only; availability dates are never acquisition gates.
            var cinemaWindow = items.TryGetValue(g.Key, out var item) && item.Type == MediaType.Movie &&
                item.ReleaseDates is { Status: "known" } dates &&
                (dates.Digital is { } digital && digital > DateOnly.FromDateTime(now.UtcDateTime) ||
                 dates.Theatrical.HasValue && !dates.Digital.HasValue);
            if (!g.Value.Any(IsSighting) && !cinemaWindow && d.DeadItems.Count < MaximumItemRows)
                d.DeadItems.Add(new WishlistDigest.DeadItem
                {
                    ItemId = Clip(g.Key, 128),
                    AttemptsInWindow = g.Value.Length,
                    DaysSinceLastMatch = null
                });
        }
        foreach (var g in list.Where(e => !e.Matched && !string.IsNullOrEmpty(e.MissReason))
                              .GroupBy(e => (e.WishlistItemId, e.MissReason!))
                              .OrderByDescending(g => g.Count()).Take(25))
            d.NearMissPatterns.Add(new WishlistDigest.NearMiss
            {
                ItemId = Clip(g.Key.WishlistItemId, 128),
                MissReason = Clip(g.Key.Item2, 160),
                Count = g.Count()
            });
        foreach (var item in items.Values.Where(i => i.Type == MediaType.Movie && !i.Dead && i.Status == WishlistStatus.Watching)
                     .OrderByDescending(i => i.AddedAt).Take(MaximumItemRows))
        {
            var last = byItem.TryGetValue(item.Id, out var attempts)
                ? attempts.Where(e => e.Matched).Select(At).Max() : null;
            d.MovieAvailabilityItems.Add(new()
            {
                ItemId = Clip(item.Id, 128),
                TheatricalDate = Date(item.ReleaseDates?.Theatrical), DigitalDate = Date(item.ReleaseDates?.Digital),
                PhysicalDate = Date(item.ReleaseDates?.Physical), CalendarStatus = item.ReleaseDates?.Status == "known" ? "known" : "unknown",
                LowSourceSightings = attempts?.Count(IsFloorSighting) ?? 0,
                DaysSinceLastMatch = last.HasValue ? (int)Math.Floor((now - last.Value).TotalDays) : null
            });
        }
        return d;
    }

    private static string Clip(string text, int maximum) => text.Length <= maximum ? text : text[..maximum];
    private static bool IsFloorSighting(WishlistAttemptEvent attempt) => attempt.MissReason == "wishlist-CAM-TS-TC-floor";
    private static bool IsSighting(WishlistAttemptEvent attempt) => attempt.Matched || IsFloorSighting(attempt);
    private static string? Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
