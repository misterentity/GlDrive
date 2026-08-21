using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlDrive.Player;

/// <summary>
/// The JSON reader settings every public torrent indexer is parsed with.
///
/// Root cause this exists for (observed 2026-08-19 and 2026-08-20): apibay.org returns its
/// numeric fields as JSON STRINGS — <c>"seeders": "42"</c>, not <c>"seeders": 42</c>. The DTO
/// typed <c>seeders</c> and <c>leechers</c> as <see cref="int"/>, so every apibay query threw
/// <c>JsonException: The JSON value could not be converted to System.Int32. Path:
/// $[0].leechers</c> and <c>RunSource</c> benched the source for ten minutes. The source has
/// therefore contributed nothing for as long as the logs go back.
///
/// The near-miss worth naming: the same DTO already declares <c>id</c> and <c>size</c> as
/// <see cref="string"/>. Someone had already met this API's habit and accommodated it one field
/// at a time, which left the two fields that had not yet been observed failing. That is the
/// zipscript-artifact mistake again — enumerating the instances you happened to see instead of
/// keying on the property that defines them. The defining property here is "this family of
/// scraped indexers quotes numbers", so the accommodation belongs in the reader, once, for all
/// of them: eztv, torrents-csv and knaben are the same kind of endpoint and can start quoting a
/// field tomorrow without warning.
///
/// <see cref="JsonNumberHandling.AllowReadingFromString"/> only widens what parses; a genuine
/// number still reads as a number, so no currently-working source changes behaviour.
/// </summary>
internal static class IndexerJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
