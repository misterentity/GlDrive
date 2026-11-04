using System.Net.Http;
using GlDrive.Library;
using GlDrive.Plex;
using Xunit;

namespace GlDrive.Tests;

public sealed class LibraryInventoryMultiPlexTests
{
    private static PlexLibraryItem Movie(string key, string title) =>
        new(key, title, "movie", 2024, null, null, null, [], [new PlexMediaFile($"/media/{title}.mkv", "1080")]);

    private static Func<string, CancellationToken, Task<IReadOnlyList<PlexLibraryItem>>> Servers(
        Dictionary<string, IReadOnlyList<PlexLibraryItem>> byUri) =>
        (uri, _) => byUri.TryGetValue(uri, out var items) ? Task.FromResult(items) : throw new HttpRequestException("refused");

    [Fact]
    public async Task Every_owned_server_is_read_and_rating_keys_stay_distinct()
    {
        var items = new List<PlexLibraryItem>();
        var (status, servers) = await LibraryInventoryReader.ReadServersAsync(
            [new("m1", "PLEXHOST", "https://a"), new("m2", "sited", "https://b")], discovered: true,
            Servers(new() { ["https://a"] = [Movie("1", "Alpha")], ["https://b"] = [Movie("1", "Beta")] }), items, default);
        Assert.Equal("complete", status);
        Assert.Equal(["PLEXHOST", "sited"], servers);
        Assert.Equal(["m1/1", "m2/1"], items.Select(i => i.RatingKey).ToArray());

        var index = new LibraryIndex().Refresh([], items);
        Assert.Equal(2, index.Entries.Count);
    }

    [Fact]
    public async Task One_unreachable_server_keeps_the_others_but_is_incomplete()
    {
        var items = new List<PlexLibraryItem>();
        var (status, servers) = await LibraryInventoryReader.ReadServersAsync(
            [new("m1", "PLEXHOST", "https://a"), new("m2", "sited", null), new("m3", "broken", "https://c")], true,
            Servers(new() { ["https://a"] = [Movie("1", "Alpha")] }), items, default);
        Assert.Equal("incomplete", status);
        Assert.Equal(["PLEXHOST"], servers);
        Assert.Single(items);
    }

    [Fact]
    public async Task Selected_server_alone_cannot_claim_the_whole_account()
    {
        var items = new List<PlexLibraryItem>();
        var (status, _) = await LibraryInventoryReader.ReadServersAsync(
            [new("m1", "PLEXHOST", "https://a")], discovered: false,
            Servers(new() { ["https://a"] = [Movie("1", "Alpha")] }), items, default);
        Assert.Equal("incomplete", status);
        Assert.Single(items);
    }

    [Fact]
    public async Task An_account_with_no_servers_is_unconfigured()
    {
        var (status, servers) = await LibraryInventoryReader.ReadServersAsync([], true, Servers(new()), [], default);
        Assert.Equal("unconfigured", status);
        Assert.Empty(servers);
    }
}
