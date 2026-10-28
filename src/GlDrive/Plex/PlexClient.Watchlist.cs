using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GlDrive.Downloads;

namespace GlDrive.Plex;

public sealed record PlexWatchlistEntry(string Id, string Title, MediaType Type, int? Year,
    string? ImdbId, int? TmdbId, int? TvdbId);
public sealed record PlexWatchlistPage(int Offset, int RawCount, int? TotalSize,
    IReadOnlyList<PlexWatchlistEntry> Items, int Skipped);
public interface IPlexWatchlistReader : IDisposable
{
    IAsyncEnumerable<PlexWatchlistPage> ReadWatchlistAsync(CancellationToken cancellationToken);
}

public sealed partial class PlexClient
{
    public async IAsyncEnumerable<PlexWatchlistPage> ReadWatchlistAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Token)) throw new PlexException("Plex sign-in is required.");
        var offset = 0;
        var seenPages = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The discover provider is undocumented. Keep this isolated, GET-only and paged;
            // do not follow redirects carrying the account token to another endpoint.
            using var request = Build(HttpMethod.Get, "https://discover.provider.plex.tv/library/sections/watchlist/all" +
                $"?includeGuids=1&X-Plex-Container-Start={offset}&X-Plex-Container-Size=10");
            using var response = await _libraryHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (body.Length + count > 1024 * 1024) throw new PlexException("Plex watchlist page exceeded the response budget.");
                body.Write(buffer, 0, count);
            }
            var text = System.Text.Encoding.UTF8.GetString(body.ToArray());
            var page = ParseWatchlistPage(text, offset);
            using var pageDocument = JsonDocument.Parse(text);
            var signature = page.Items.Count > 0 ? string.Join('\n', page.Items.Select(i => i.Id)) :
                pageDocument.RootElement.GetProperty("MediaContainer").TryGetProperty("Metadata", out var raw) ? raw.GetRawText() : "";
            signature = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(signature)));
            if (page.RawCount > 0 && !seenPages.Add(signature)) throw new PlexException("Plex repeated a watchlist page.");
            yield return page;
            offset = checked(offset + page.RawCount);
            if (page.RawCount == 0 || page.TotalSize is { } total && offset >= total) yield break;
        }
    }

    internal static PlexWatchlistPage ParseWatchlistPage(string json, int expectedOffset = 0)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("MediaContainer", out var container) || container.ValueKind != JsonValueKind.Object)
            throw new PlexException("Plex returned no watchlist container.");
        var offset = WatchInt(container, "offset") ?? expectedOffset;
        if (offset != expectedOffset) throw new PlexException("Plex returned an unexpected watchlist offset.");
        var total = WatchInt(container, "totalSize");
        if (total < 0) total = null;
        if (!container.TryGetProperty("Metadata", out var metadata))
        {
            if (WatchInt(container, "size") == 0) return new(offset, 0, total, [], 0);
            throw new PlexException("Plex returned no watchlist items.");
        }
        if (metadata.ValueKind != JsonValueKind.Array) throw new PlexException("Plex returned malformed watchlist items.");
        var rows = new List<PlexWatchlistEntry>();
        var skipped = 0;
        foreach (var row in metadata.EnumerateArray())
        {
            var type = WatchString(row, "type");
            var title = WatchString(row, "title");
            var id = WatchString(row, "guid") ?? WatchString(row, "ratingKey");
            if (type is not ("movie" or "show") || string.IsNullOrWhiteSpace(title) || title.Length > 512 ||
                string.IsNullOrWhiteSpace(id) || id.Length > 512) { skipped++; continue; }
            string? imdb = null; int? tmdb = null, tvdb = null;
            var guids = new List<string>();
            if (row.TryGetProperty("Guid", out var values) && values.ValueKind == JsonValueKind.Array)
                foreach (var value in values.EnumerateArray())
                    if (WatchString(value, "id") is { } guid) guids.Add(guid);
            var imdbIds = guids.Where(g => g.StartsWith("imdb://", StringComparison.OrdinalIgnoreCase) && ValidImdb(g[7..]))
                .Select(g => g[7..].ToLowerInvariant()).Distinct().ToArray();
            if (imdbIds.Length == 1) imdb = imdbIds[0];
            int? NumericId(string scheme)
            {
                var ids = guids.Where(g => g.StartsWith(scheme + "://", StringComparison.OrdinalIgnoreCase))
                    .Select(g => int.TryParse(g[(scheme.Length + 3)..], out var value) && value > 0 ? value : 0)
                    .Where(v => v > 0).Distinct().ToArray();
                return ids.Length == 1 ? ids[0] : null;
            }
            tmdb = NumericId("tmdb"); tvdb = NumericId("tvdb");
            var year = WatchInt(row, "year");
            if (year is < 1800 or > 2500) year = null;
            rows.Add(new(id, title.Trim(), type == "movie" ? MediaType.Movie : MediaType.TvShow, year, imdb, tmdb, tvdb));
        }
        return new(offset, metadata.GetArrayLength(), total, rows.AsReadOnly(), skipped);
    }

    internal static bool ValidImdb(string? value) => value is { Length: >= 3 and <= 20 } &&
        value.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && value[2..].All(char.IsAsciiDigit);
    private static string? WatchString(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object &&
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? WatchInt(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object &&
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}
