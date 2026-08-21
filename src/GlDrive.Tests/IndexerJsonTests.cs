using System.Text.Json;
using GlDrive.Player;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression cover for the 2026-08-19/20 apibay failure: the API quotes its numbers
/// (<c>"seeders": "42"</c>), the DTO typed them as int, and every query threw
/// <c>JsonException … Path: $[0].leechers</c> — benching the source for ten minutes each time,
/// so it has never returned a result.
///
/// The DTO already declared <c>id</c> and <c>size</c> as string, i.e. the habit was known and
/// had been accommodated one observed field at a time. These tests hold the reader-level fix
/// instead, which is what stops the next unobserved field from repeating it.
/// </summary>
public sealed class IndexerJsonTests
{
    /// <summary>The exact shape that threw, reduced to one row.</summary>
    private const string QuotedNumbersPayload = """
    [{"id":"81261882","name":"Some.Release.2026.1080p.WEB.H264-GROUP",
      "info_hash":"A1B2C3D4E5F60718293A4B5C6D7E8F9012345678",
      "leechers":"7","seeders":"42","num_files":"3","size":"1503238553",
      "username":"someuser","added":"1755600000","status":"vip","category":"207","imdb":""}]
    """;

    [Fact]
    public void QuotedNumericFieldsParse()
    {
        var items = JsonSerializer.Deserialize<List<TorrentSearchService.ApiBayResult>>(
            QuotedNumbersPayload, IndexerJson.Options);

        var item = Assert.Single(items!);
        Assert.Equal(42, item.Seeders);
        Assert.Equal(7, item.Leechers);
        Assert.Equal("1503238553", item.Size);
    }

    /// <summary>
    /// Mutation guard: without AllowReadingFromString this payload throws, so the assertion
    /// above is testing the option and not merely the DTO.
    /// </summary>
    [Fact]
    public void TheSamePayloadFailsWithoutTheOption()
    {
        var strict = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<List<TorrentSearchService.ApiBayResult>>(
                QuotedNumbersPayload, strict));
    }

    /// <summary>
    /// Widening only. A source that sends genuine numbers today must keep working — the option
    /// is additive, and the sibling indexers (eztv, torrents-csv, knaben) share this reader.
    /// </summary>
    [Fact]
    public void UnquotedNumbersStillParse()
    {
        const string json = """
        [{"id":"1","name":"n","info_hash":"h","leechers":7,"seeders":42,"size":"1","username":"u","category":"207"}]
        """;

        var items = JsonSerializer.Deserialize<List<TorrentSearchService.ApiBayResult>>(
            json, IndexerJson.Options);

        var item = Assert.Single(items!);
        Assert.Equal(42, item.Seeders);
        Assert.Equal(7, item.Leechers);
    }
}
