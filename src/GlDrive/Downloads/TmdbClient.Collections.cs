using System.Globalization;
using System.Text.Json;
using Serilog;

namespace GlDrive.Downloads;

public sealed record TmdbCollectionFilm(int TmdbId, string Title, int? Year);
public sealed record TmdbCollectionData(int Id, string Name, IReadOnlyList<TmdbCollectionFilm> Films);
public sealed record TmdbCollectionResult(string Status, TmdbCollectionData? Collection);

public partial class TmdbClient
{
    internal TimeSpan CollectionRequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
    internal TimeSpan CollectionLookupTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>One explicit refresh, at most two bounded requests. Never polls or discovers other collections.</summary>
    public async Task<TmdbCollectionResult> GetMovieCollection(int movieTmdbId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!HasApiKey || movieTmdbId <= 0) return new("unknown", null);
        using var whole = CancellationTokenSource.CreateLinkedTokenSource(ct);
        whole.CancelAfter(CollectionLookupTimeout);
        try
        {
            var json = await ReadCalendarJson($"3/movie/{movieTmdbId}?api_key={Uri.EscapeDataString(_apiKey)}",
                whole.Token, CollectionRequestTimeout).ConfigureAwait(false);
            var (status, collectionId) = TmdbCollectionParser.MovieCollection(json, movieTmdbId);
            if (status != "available") return new(status, null);
            return await ReadCollection(collectionId!.Value, whole.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { Log.Warning("TMDB collection lookup unavailable; collection evidence remains unknown"); return new("unknown", null); }
    }

    /// <summary>Read one explicitly requested collection with a bounded response and deadline.</summary>
    public async Task<TmdbCollectionResult> GetCollection(int collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!HasApiKey || collectionId <= 0) return new("unknown", null);
        try { return await ReadCollection(collectionId, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { Log.Warning("TMDB collection lookup unavailable; collection evidence remains unknown"); return new("unknown", null); }
    }

    private async Task<TmdbCollectionResult> ReadCollection(int collectionId, CancellationToken ct)
    {
        var json = await ReadCalendarJson($"3/collection/{collectionId}?api_key={Uri.EscapeDataString(_apiKey)}",
            ct, CollectionRequestTimeout).ConfigureAwait(false);
        return TmdbCollectionParser.Collection(json, collectionId);
    }
}

internal static class TmdbCollectionParser
{
    internal const int MaximumFilms = 200;
    internal static int? CollectionId(JsonElement? value) => value is { ValueKind: JsonValueKind.Object } root &&
        root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number) && number > 0 ? number : null;

    internal static (string Status, int? CollectionId) MovieCollection(string json, int movieTmdbId)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (CollectionId(root) != movieTmdbId || !root.TryGetProperty("belongs_to_collection", out var collection)) return ("unknown", null);
        if (collection.ValueKind == JsonValueKind.Null) return ("no-collection", null);
        var id = CollectionId(collection);
        return id.HasValue ? ("available", id) : ("unknown", null);
    }

    internal static TmdbCollectionResult Collection(string json, int collectionId)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (CollectionId(root) != collectionId || !root.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array) return new("unknown", null);
        var films = new List<TmdbCollectionFilm>();
        var partial = parts.GetArrayLength() > MaximumFilms;
        foreach (var part in parts.EnumerateArray().Take(MaximumFilms))
        {
            var id = CollectionId(part);
            if (part.ValueKind != JsonValueKind.Object) { partial = true; continue; }
            var title = Text(part, "title") ?? Text(part, "name") ?? Text(part, "original_title") ?? Text(part, "original_name");
            if (id == null || title == null) { partial = true; continue; }
            // Essential identities cannot be guessed. Optional dates/metadata can be unknown.
            var date = Text(part, "release_date");
            int? year = DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed.Year : null;
            if (part.TryGetProperty("media_type", out var media) && media.ValueKind == JsonValueKind.String && media.GetString() is { } kind && kind != "movie")
            { partial = true; continue; }
            films.Add(new(id.Value, title, year));
        }
        return new(partial ? "partial" : "available", new(collectionId, Text(root, "name") ?? $"Collection {collectionId}",
            films.DistinctBy(film => film.TmdbId).ToArray()));
    }

    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var text) &&
        text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 and <= 512 } content &&
        !string.IsNullOrWhiteSpace(content) ? content.Trim() : null;
}
