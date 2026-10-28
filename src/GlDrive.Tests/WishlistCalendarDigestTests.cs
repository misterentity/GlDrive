using System.IO;
using System.Text.Json;
using GlDrive.AiAgent;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class WishlistCalendarDigestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static WishlistAttemptEvent Attempt(string id, bool matched = false, DateTimeOffset? at = null) => new()
    { WishlistItemId = id, Matched = matched, Ts = (at ?? Now).ToString("O"), MissReason = matched ? null : "quality" };

    [Fact]
    public void No_observed_match_never_invents_sixty_days_of_age()
    {
        var item = new WishlistItem { Id = "movie", Title = "Example", Year = 2026 };
        var digest = new WishlistDigester().Build([Attempt(item.Id)], [item], Now);
        Assert.Null(Assert.Single(digest.DeadItems).DaysSinceLastMatch);
        Assert.Null(Assert.Single(digest.MovieAvailabilityItems).DaysSinceLastMatch);
        Assert.Contains("not established", digest.DeadItems[0].Evidence);
    }

    [Fact]
    public void Calendar_evidence_uses_only_real_observed_match_time()
    {
        var item = new WishlistItem { Id = "movie", ReleaseDates = new(Digital: new(2026, 11, 1), Status: "known") };
        var digest = new WishlistDigester().Build([Attempt(item.Id, true, Now.AddDays(-3).AddHours(-2)),
            Attempt(item.Id, true, Now.AddDays(9)), Attempt(item.Id, true) with { Ts = "unknown" }], [item], Now);
        Assert.Empty(digest.DeadItems);
        var evidence = Assert.Single(digest.MovieAvailabilityItems);
        Assert.Equal(3, evidence.DaysSinceLastMatch);
        Assert.Equal("2026-11-01", evidence.DigitalDate);
    }

    [Theory]
    [InlineData("known", false)]
    [InlineData("unknown", true)]
    public void Known_future_digital_date_explains_no_matches_without_changing_wishlist(string status, bool noMatchEvidence)
    {
        var item = new WishlistItem { Id = "movie", ReleaseDates = new(Digital: new(2026, 11, 1), Status: status) };
        var digest = new WishlistDigester().Build([Attempt(item.Id)], [item], Now);
        Assert.Equal(noMatchEvidence, digest.DeadItems.Count == 1);
        Assert.Equal(WishlistStatus.Watching, item.Status);
        Assert.False(item.Dead);
    }

    [Fact]
    public void All_new_prompt_evidence_has_row_and_text_bounds()
    {
        var items = Enumerable.Range(0, 200).Select(i => new WishlistItem { Id = i + new string('x', 500), AddedAt = Now.AddMinutes(i).UtcDateTime }).ToArray();
        var attempts = items.Select(i => Attempt(i.Id) with { MissReason = new string('z', 8000) });
        var digest = new WishlistDigester().Build(attempts, items, Now);
        Assert.Equal(50, digest.DeadItems.Count);
        Assert.Equal(50, digest.MovieAvailabilityItems.Count);
        Assert.Equal(25, digest.NearMissPatterns.Count);
        Assert.All(digest.DeadItems, i => Assert.True(i.ItemId.Length <= 128));
        Assert.All(digest.MovieAvailabilityItems, i => Assert.True(i.ItemId.Length <= 128));
        Assert.All(digest.NearMissPatterns, i => Assert.True(i.ItemId.Length <= 128 && i.MissReason.Length <= 160));
        Assert.True(JsonSerializer.Serialize(digest).Length < 40000);
    }

    [Fact]
    public void Floor_blocked_title_match_is_a_sighting_even_when_near_misses_are_crowded_out()
    {
        var item = new WishlistItem { Id = "movie" };
        var noise = Enumerable.Range(0, 30).SelectMany(i => Enumerable.Range(0, 3)
            .Select(_ => Attempt("noise-" + i) with { MissReason = "title" }));
        var floor = Attempt(item.Id) with { MissReason = "wishlist-CAM-TS-TC-floor" };
        var digest = new WishlistDigester().Build(noise.Append(floor), [item], Now);
        Assert.DoesNotContain(digest.DeadItems, row => row.ItemId == item.Id);
        Assert.DoesNotContain(digest.NearMissPatterns, row => row.ItemId == item.Id);
        Assert.Equal(1, Assert.Single(digest.MovieAvailabilityItems).LowSourceSightings);
        Assert.Null(Assert.Single(digest.MovieAvailabilityItems).DaysSinceLastMatch);
    }

    [Fact]
    public void Known_theatrical_movie_without_digital_date_is_not_dead()
    {
        var item = new WishlistItem { Id = "movie", ReleaseDates = new(Theatrical: new(2026, 10, 1), Status: "known") };
        var digest = new WishlistDigester().Build([Attempt(item.Id)], [item], Now);
        Assert.Empty(digest.DeadItems);
        Assert.Equal("2026-10-01", Assert.Single(digest.MovieAvailabilityItems).TheatricalDate);
    }

    [Fact]
    public void Malformed_optional_identity_and_duplicate_snapshot_rows_do_not_void_digest()
    {
        var item = new WishlistItem { Id = "movie" };
        var digest = new WishlistDigester().Build([Attempt(null!), Attempt(""), Attempt(item.Id)],
            [item, item, new WishlistItem { Id = null! }], Now);
        Assert.Single(digest.DeadItems);
        Assert.Single(digest.MovieAvailabilityItems);
    }

    [Fact]
    public void Missing_optional_calendar_snapshot_does_not_stop_other_digest_work()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gldrive-digest-calendar-" + Guid.NewGuid().ToString("N"));
        var digest = new LogDigester(directory, () => throw new IOException("synthetic unavailable snapshot"))
            .Build(1, Now.UtcDateTime);
        Assert.Empty(digest.Wishlist.MovieAvailabilityItems);
        Assert.NotNull(digest.Races);
    }
}
