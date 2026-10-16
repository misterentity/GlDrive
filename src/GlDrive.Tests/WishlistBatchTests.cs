using System.IO;
using System.Text.Json;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class WishlistBatchTests
{
    [Fact]
    public void Thousand_unique_imports_write_once_with_fresh_ids_and_preserved_metadata()
    {
        var writes = new List<string>();
        var store = new WishlistStore(() => null, writes.Add);
        var imported = Enumerable.Range(0, 1000).Select(i => new WishlistItem
        {
            Id = $"old-{i}", Title = $"title-{i}", ImdbId = $"imdb-{i}", TvMazeId = i,
            Type = MediaType.TvShow, Year = 2024, Quality = QualityProfile.Q2160p,
            Status = WishlistStatus.Paused, AddedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            GrabbedReleases = ["fixture"], PosterUrl = "poster", Plot = "plot", Rating = "8", Genres = "drama", Dead = true
        }).ToArray();
        Assert.Equal(1000, store.AddImported(imported));
        using var saved = JsonDocument.Parse(Assert.Single(writes));
        Assert.Equal(1000, saved.RootElement.GetArrayLength());
        Assert.Equal(1000, store.Items.Select(i => i.Id).Distinct().Count());
        var actual = store.Items[0];
        Assert.True(Guid.TryParseExact(actual.Id, "N", out _));
        Assert.Equal("old-0", imported[0].Id);
        Assert.Equal(imported[0].Title, actual.Title);
        Assert.Equal(imported[0].ImdbId, actual.ImdbId);
        Assert.Equal(imported[0].TvMazeId, actual.TvMazeId);
        Assert.Equal(imported[0].Type, actual.Type);
        Assert.Equal(imported[0].Year, actual.Year);
        Assert.Equal(imported[0].Quality, actual.Quality);
        Assert.Equal(imported[0].Status, actual.Status);
        Assert.Equal(imported[0].AddedAt, actual.AddedAt);
        Assert.Equal(imported[0].GrabbedReleases, actual.GrabbedReleases);
        Assert.NotSame(imported[0].GrabbedReleases, actual.GrabbedReleases);
        Assert.Equal(imported[0].PosterUrl, actual.PosterUrl);
        Assert.Equal(imported[0].Plot, actual.Plot);
        Assert.Equal(imported[0].Rating, actual.Rating);
        Assert.Equal(imported[0].Genres, actual.Genres);
        Assert.Equal(imported[0].Dead, actual.Dead);
    }

    [Fact]
    public void Dedup_matches_original_imdb_tvmaze_or_title_and_type_semantics_across_batch()
    {
        var writes = new List<string>();
        var existing = new WishlistItem { Id = "keep", Title = "Title", ImdbId = "tt1", TvMazeId = 1 };
        var store = new WishlistStore(() => JsonSerializer.Serialize(new[] { existing },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), writes.Add);
        var imported = new[]
        {
            new WishlistItem { Title = "duplicate imdb", ImdbId = "tt1", TvMazeId = 99 },
            new WishlistItem { Title = "duplicate tv", TvMazeId = 1 },
            new WishlistItem { Title = "Title" },
            new WishlistItem { Title = "Title", Type = MediaType.TvShow }, // different type
            new WishlistItem { Title = "title", ImdbId = "TT1", TvMazeId = 99 }, // case sensitive; skipped row didn't reserve 99
            new WishlistItem { Title = "different title", ImdbId = "TT1" },
            new WishlistItem { Title = "fresh", ImdbId = "" },
            new WishlistItem { Title = "other fresh", ImdbId = "" }, // empty IDs don't match
            new WishlistItem { Title = "fresh" }
        };
        Assert.Equal(4, store.AddImported(imported));
        Assert.Equal(new[] { "Title", "Title", "title", "fresh", "other fresh" }, store.Items.Select(i => i.Title));
        Assert.Equal("keep", store.Items[0].Id);
        Assert.Single(writes);
        Assert.Equal(0, store.AddImported(imported));
        Assert.Single(writes);
    }

    [Fact]
    public void Empty_import_does_not_write()
    {
        var writes = 0;
        var store = new WishlistStore(() => null, _ => writes++);
        Assert.Equal(0, store.AddImported([]));
        Assert.Equal(0, writes);
    }

    [Fact]
    public void Legacy_nullable_fields_round_trip_and_null_rows_fail_without_partial_publication()
    {
        var writes = new List<string>();
        var store = new WishlistStore(() => null, writes.Add);
        var nullable = JsonSerializer.Deserialize<WishlistItem>(
            """{"title":null,"imdbId":null,"tvMazeId":null,"grabbedReleases":null}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        Assert.Equal(1, store.AddImported([nullable]));
        Assert.Null(Assert.Single(store.Items).Title);
        Assert.Null(store.Items[0].GrabbedReleases);
        using var saved = JsonDocument.Parse(Assert.Single(writes));
        Assert.Equal(JsonValueKind.Null, saved.RootElement[0].GetProperty("grabbedReleases").ValueKind);
        Assert.Equal(0, store.AddImported([nullable]));
        Assert.Throws<InvalidDataException>(() => store.AddImported([new WishlistItem { Title = "not published" }, null!]));
        Assert.Single(store.Items);
        Assert.Single(writes);
    }

    [Fact]
    public void Failed_batch_does_not_publish_or_change_input_and_can_be_retried()
    {
        var fail = true;
        var writes = new List<string>();
        var store = new WishlistStore(() => null, json =>
        {
            if (fail) throw new IOException("fixture failure");
            writes.Add(json);
        });
        var item = new WishlistItem { Id = "imported-id", Title = "fixture" };
        Assert.Throws<IOException>(() => store.AddImported([item]));
        Assert.Empty(store.Items);
        Assert.Equal("imported-id", item.Id);
        fail = false;
        Assert.Equal(1, store.AddImported([item]));
        Assert.Single(store.Items);
        Assert.Single(writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_mutation_waits_for_batch_write_and_cannot_overwrite_newer_state(bool failFirst)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<string>();
        var attempts = 0;
        var store = new WishlistStore(() => null, json =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture writer wasn't released");
                if (failFirst) throw new IOException("first write rejected");
            }
            writes.Add(json);
        });
        var first = Task.Run(() => store.AddImported([new WishlistItem { Title = "batch" }]));
        Task? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = Task.Run(() => store.Add(new WishlistItem { Title = "later" }));
        }
        finally { release.Set(); }
        if (failFirst) await Assert.ThrowsAsync<IOException>(() => first);
        else Assert.Equal(1, await first);
        await second!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(failFirst ? new[] { "later" } : new[] { "batch", "later" }, store.Items.Select(i => i.Title));
        using var final = JsonDocument.Parse(writes[^1]);
        Assert.Equal(store.Items.Select(i => i.Title), final.RootElement.EnumerateArray().Select(i => i.GetProperty("title").GetString()));
        Assert.Equal(failFirst ? 1 : 2, writes.Count);
    }
}
