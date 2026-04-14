using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace GlDrive.Downloads;

/// <summary>
/// Movie metadata client. Prefers paid OMDB when an API key is configured,
/// otherwise falls back to free keyless imdbapi.dev (RaceTrade-style).
/// </summary>
public class OmdbClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClient _freeHttp;
    private readonly string _apiKey;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OmdbClient(string apiKey)
    {
        _apiKey = apiKey;
        _http = new HttpClient { BaseAddress = new Uri("https://www.omdbapi.com/") };
        _freeHttp = new HttpClient { BaseAddress = new Uri("https://api.imdbapi.dev/") };
    }

    public bool HasApiKey => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// True when either OMDB (paid) or imdbapi.dev (free) backend is available.
    /// Always true since imdbapi.dev requires no key.
    /// </summary>
    public bool IsAvailable => true;

    public async Task<OmdbMovie[]> Search(string query, CancellationToken ct = default)
    {
        if (HasApiKey)
        {
            try
            {
                var url = $"?apikey={_apiKey}&s={Uri.EscapeDataString(query)}&type=movie";
                var response = await _http.GetStringAsync(url, ct);
                var result = JsonSerializer.Deserialize<OmdbSearchResponse>(response, JsonOptions);
                if (result?.Search is { Length: > 0 }) return result.Search;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "OMDB search failed, falling back to imdbapi.dev: {Query}", query);
            }
        }

        // Fallback: imdbapi.dev /search/titles?query=...
        try
        {
            var url = $"search/titles?query={Uri.EscapeDataString(query)}";
            var response = await _freeHttp.GetStringAsync(url, ct);
            var result = JsonSerializer.Deserialize<ImdbApiDevSearchResponse>(response, JsonOptions);
            if (result?.Titles == null) return [];
            return Array.ConvertAll(result.Titles, ImdbApiDevMapper.ToOmdbMovie);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "imdbapi.dev search failed for: {Query}", query);
            return [];
        }
    }

    public async Task<OmdbMovie?> GetById(string imdbId, CancellationToken ct = default)
    {
        if (HasApiKey)
        {
            try
            {
                var url = $"?apikey={_apiKey}&i={Uri.EscapeDataString(imdbId)}";
                var response = await _http.GetStringAsync(url, ct);
                var movie = JsonSerializer.Deserialize<OmdbMovie>(response, JsonOptions);
                if (movie != null && !string.IsNullOrEmpty(movie.Title)) return movie;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "OMDB get by id failed, falling back to imdbapi.dev: {Id}", imdbId);
            }
        }

        // Fallback: imdbapi.dev /titles/{id}
        try
        {
            var url = $"titles/{Uri.EscapeDataString(imdbId)}";
            var response = await _freeHttp.GetStringAsync(url, ct);
            var title = JsonSerializer.Deserialize<ImdbApiDevTitle>(response, JsonOptions);
            return title != null ? ImdbApiDevMapper.ToOmdbMovie(title) : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "imdbapi.dev get by id failed for: {Id}", imdbId);
            return null;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _freeHttp.Dispose();
    }
}

internal class ImdbApiDevSearchResponse
{
    public ImdbApiDevTitle[]? Titles { get; set; }
}

internal class ImdbApiDevTitle
{
    public string? Id { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("primaryTitle")]
    public string? PrimaryTitle { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("startYear")]
    public int? StartYear { get; set; }
    public ImdbApiDevImage? PrimaryImage { get; set; }
    public string? Plot { get; set; }
    public ImdbApiDevRating? Rating { get; set; }
    public string[]? Genres { get; set; }
}

internal class ImdbApiDevImage
{
    public string? Url { get; set; }
}

internal class ImdbApiDevRating
{
    public double? Aggregate { get; set; }
}

internal static class ImdbApiDevMapper
{
    public static OmdbMovie ToOmdbMovie(ImdbApiDevTitle t) => new()
    {
        Title = t.PrimaryTitle ?? "",
        Year = t.StartYear?.ToString() ?? "",
        ImdbID = t.Id,
        Poster = t.PrimaryImage?.Url,
        Plot = t.Plot,
        imdbRating = t.Rating?.Aggregate is { } r ? Math.Round(r, 1).ToString("F1") : null,
        Genre = t.Genres is { Length: > 0 } ? string.Join(", ", t.Genres) : null,
    };
}

public class OmdbSearchResponse
{
    public OmdbMovie[]? Search { get; set; }
    public string? TotalResults { get; set; }
    public string? Response { get; set; }
}

public class OmdbMovie
{
    public string Title { get; set; } = "";
    public string Year { get; set; } = "";
    public string? ImdbID { get; set; }
    public string? Poster { get; set; }
    public string? Plot { get; set; }
    public string? imdbRating { get; set; }
    public string? Genre { get; set; }
    public string? Rated { get; set; }

    public int? YearParsed => int.TryParse(Year?.Split('–')[0], out var y) ? y : null;
}
