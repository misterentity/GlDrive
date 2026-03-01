using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace GlDrive.Downloads;

public class TmdbClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Common genre IDs so discover results can show names without extra API call
    private static readonly Dictionary<int, string> GenreNames = new()
    {
        [28] = "Action", [12] = "Adventure", [16] = "Animation",
        [35] = "Comedy", [80] = "Crime", [99] = "Documentary",
        [18] = "Drama", [10751] = "Family", [14] = "Fantasy",
        [36] = "History", [27] = "Horror", [10402] = "Music",
        [9648] = "Mystery", [10749] = "Romance", [878] = "Sci-Fi",
        [10770] = "TV Movie", [53] = "Thriller", [10752] = "War",
        [37] = "Western"
    };

    public TmdbClient(string apiKey)
    {
        _apiKey = apiKey;
        _http = new HttpClient { BaseAddress = new Uri("https://api.themoviedb.org/") };
        _http.DefaultRequestHeaders.Add("User-Agent", "GlDrive/1.0");
    }

    public bool HasApiKey => !string.IsNullOrEmpty(_apiKey);

    public async Task<TmdbMovie[]> GetUpcomingReleases(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (!HasApiKey) return [];

        try
        {
            var url = $"3/discover/movie?api_key={_apiKey}" +
                      $"&with_release_type=4|5" +
                      $"&release_date.gte={from:yyyy-MM-dd}" +
                      $"&release_date.lte={to:yyyy-MM-dd}" +
                      $"&sort_by=release_date.asc&region=US";
            var response = await _http.GetStringAsync(url, ct);
            var result = JsonSerializer.Deserialize<TmdbDiscoverResponse>(response, JsonOptions);
            if (result?.Results == null) return [];

            foreach (var movie in result.Results)
                movie.GenreText = ResolveGenres(movie.GenreIds);

            return result.Results;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TMDb discover failed");
            return [];
        }
    }

    public async Task<TmdbMovieDetail?> GetMovieDetail(int tmdbId, CancellationToken ct = default)
    {
        if (!HasApiKey) return null;

        try
        {
            var url = $"3/movie/{tmdbId}?api_key={_apiKey}";
            var response = await _http.GetStringAsync(url, ct);
            return JsonSerializer.Deserialize<TmdbMovieDetail>(response, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TMDb movie detail failed for id: {Id}", tmdbId);
            return null;
        }
    }

    private static string ResolveGenres(int[]? ids)
    {
        if (ids == null || ids.Length == 0) return "";
        return string.Join(", ", ids.Select(id => GenreNames.GetValueOrDefault(id, "Unknown")).Take(3));
    }

    public void Dispose() => _http.Dispose();
}

public class TmdbDiscoverResponse
{
    public TmdbMovie[]? Results { get; set; }
    [JsonPropertyName("total_results")]
    public int TotalResults { get; set; }
}

public class TmdbMovie
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }
    public string? Overview { get; set; }
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }
    [JsonPropertyName("genre_ids")]
    public int[]? GenreIds { get; set; }

    // Resolved after deserialization
    [JsonIgnore]
    public string GenreText { get; set; } = "";

    [JsonIgnore]
    public string? PosterUrl => PosterPath != null ? $"https://image.tmdb.org/t/p/w185{PosterPath}" : null;

    [JsonIgnore]
    public int? YearParsed => ReleaseDate is { Length: >= 4 } ? int.TryParse(ReleaseDate[..4], out var y) ? y : null : null;
}

public class TmdbMovieDetail
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }
    public string? Overview { get; set; }
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }
    public TmdbGenre[]? Genres { get; set; }
    public int? Runtime { get; set; }

    [JsonIgnore]
    public string? PosterUrl => PosterPath != null ? $"https://image.tmdb.org/t/p/w185{PosterPath}" : null;

    [JsonIgnore]
    public string GenreText => Genres != null ? string.Join(", ", Genres.Select(g => g.Name)) : "";
}

public class TmdbGenre
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}
