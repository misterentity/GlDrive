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

    private static readonly Dictionary<int, string> TvGenreNames = new()
    {
        [10759] = "Action & Adventure", [16] = "Animation", [35] = "Comedy",
        [80] = "Crime", [99] = "Documentary", [18] = "Drama",
        [10751] = "Family", [10762] = "Kids", [9648] = "Mystery",
        [10763] = "News", [10764] = "Reality", [10765] = "Sci-Fi & Fantasy",
        [10766] = "Soap", [10767] = "Talk", [10768] = "War & Politics",
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
            var baseUrl = $"3/discover/movie?api_key={_apiKey}" +
                          $"&with_release_type=2|3" +
                          $"&release_date.gte={from:yyyy-MM-dd}" +
                          $"&release_date.lte={to:yyyy-MM-dd}" +
                          "&sort_by=popularity.desc&region=US";
            var movies = new List<TmdbMovie>();
            for (var page = 1; page <= 2; page++)
            {
                var response = await _http.GetStringAsync($"{baseUrl}&page={page}", ct);
                var result = JsonSerializer.Deserialize<TmdbDiscoverResponse>(response, JsonOptions);
                if (result?.Results != null) movies.AddRange(result.Results);
                if (result == null || result.TotalResults <= page * 20) break;
            }
            foreach (var movie in movies) movie.GenreText = ResolveGenres(movie.GenreIds);
            return movies.ToArray();
        }
        catch (Exception ex) { Log.Warning(ex, "TMDb discover failed"); return []; }
    }

    public async Task<TmdbMovie[]> GetTrendingMovies(CancellationToken ct = default)
    {
        if (!HasApiKey) return [];
        try
        {
            var movies = new List<TmdbMovie>();
            for (var page = 1; page <= 2; page++)
            {
                var url = $"3/trending/movie/week?api_key={_apiKey}&page={page}";
                var response = await _http.GetStringAsync(url, ct);
                var result = JsonSerializer.Deserialize<TmdbDiscoverResponse>(response, JsonOptions);
                if (result?.Results != null) movies.AddRange(result.Results);
                if (result == null || result.TotalResults <= page * 20) break;
            }
            foreach (var movie in movies) movie.GenreText = ResolveGenres(movie.GenreIds);
            return movies.ToArray();
        }
        catch (Exception ex) { Log.Warning(ex, "TMDb trending movies failed"); return []; }
    }

    public async Task<TmdbTvShow[]> GetTrendingTvShows(CancellationToken ct = default)
    {
        if (!HasApiKey) return [];
        try
        {
            var shows = new List<TmdbTvShow>();
            for (var page = 1; page <= 2; page++)
            {
                var url = $"3/trending/tv/week?api_key={_apiKey}&page={page}";
                var response = await _http.GetStringAsync(url, ct);
                var result = JsonSerializer.Deserialize<TmdbTvResponse>(response, JsonOptions);
                if (result?.Results != null) shows.AddRange(result.Results);
                if (result == null || result.TotalResults <= page * 20) break;
            }
            foreach (var show in shows) show.GenreText = ResolveTvGenres(show.GenreIds);
            return shows.ToArray();
        }
        catch (Exception ex) { Log.Warning(ex, "TMDb trending TV failed"); return []; }
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
        catch (Exception ex) { Log.Warning(ex, "TMDb movie detail failed for id: {Id}", tmdbId); return null; }
    }

    private static string ResolveGenres(int[]? ids)
    {
        if (ids == null || ids.Length == 0) return "";
        return string.Join(", ", ids.Select(id => GenreNames.GetValueOrDefault(id, "Unknown")).Take(3));
    }

    private static string ResolveTvGenres(int[]? ids)
    {
        if (ids == null || ids.Length == 0) return "";
        return string.Join(", ", ids.Select(id => TvGenreNames.GetValueOrDefault(id, GenreNames.GetValueOrDefault(id, "Unknown"))).Take(3));
    }


    public async Task<TmdbSearchResult[]> SearchMulti(string query, CancellationToken ct = default)
    {
        if (!HasApiKey || string.IsNullOrWhiteSpace(query)) return [];
        try
        {
            var url = $"3/search/multi?api_key={_apiKey}&query={Uri.EscapeDataString(query)}&page=1";
            var response = await _http.GetStringAsync(url, ct);
            var result = JsonSerializer.Deserialize<TmdbSearchMultiResponse>(response, JsonOptions);
            return result?.Results?.Where(r => r.MediaType is "movie" or "tv").ToArray() ?? [];
        }
        catch (Exception ex) { Log.Warning(ex, "TMDb search failed"); return []; }
    }

    public async Task<TmdbTvDetail?> GetTvDetail(int tvId, CancellationToken ct = default)
    {
        if (!HasApiKey) return null;
        try
        {
            var url = $"3/tv/{tvId}?api_key={_apiKey}";
            var response = await _http.GetStringAsync(url, ct);
            return JsonSerializer.Deserialize<TmdbTvDetail>(response, JsonOptions);
        }
        catch (Exception ex) { Log.Warning(ex, "TMDb TV detail failed for id: {Id}", tvId); return null; }
    }

    public async Task<TmdbSeason?> GetTvSeason(int tvId, int seasonNumber, CancellationToken ct = default)
    {
        if (!HasApiKey) return null;
        try
        {
            var url = $"3/tv/{tvId}/season/{seasonNumber}?api_key={_apiKey}";
            var response = await _http.GetStringAsync(url, ct);
            return JsonSerializer.Deserialize<TmdbSeason>(response, JsonOptions);
        }
        catch (Exception ex) { Log.Warning(ex, "TMDb season failed for tv:{TvId} s:{Season}", tvId, seasonNumber); return null; }
    }

    public void Dispose() => _http.Dispose();
}

public class TmdbDiscoverResponse
{
    public TmdbMovie[]? Results { get; set; }
    [JsonPropertyName("total_results")]
    public int TotalResults { get; set; }
}

public class TmdbTvResponse
{
    public TmdbTvShow[]? Results { get; set; }
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

    [JsonIgnore] public string GenreText { get; set; } = "";
    [JsonIgnore] public string? PosterUrl => PosterPath != null ? $"https://image.tmdb.org/t/p/w342{PosterPath}" : null;
    [JsonIgnore] public int? YearParsed => ReleaseDate is { Length: >= 4 } ? int.TryParse(ReleaseDate[..4], out var y) ? y : null : null;
}

public class TmdbTvShow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }
    public string? Overview { get; set; }
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }
    [JsonPropertyName("genre_ids")]
    public int[]? GenreIds { get; set; }

    [JsonIgnore] public string GenreText { get; set; } = "";
    [JsonIgnore] public string? PosterUrl => PosterPath != null ? $"https://image.tmdb.org/t/p/w342{PosterPath}" : null;
    [JsonIgnore] public int? YearParsed => FirstAirDate is { Length: >= 4 } ? int.TryParse(FirstAirDate[..4], out var y) ? y : null : null;
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

    [JsonIgnore] public string? PosterUrl => PosterPath != null ? $"https://image.tmdb.org/t/p/w185{PosterPath}" : null;
    [JsonIgnore] public string GenreText => Genres != null ? string.Join(", ", Genres.Select(g => g.Name)) : "";
}

public class TmdbGenre
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class TmdbSearchMultiResponse
{
    public TmdbSearchResult[]? Results { get; set; }
    [JsonPropertyName("total_results")]
    public int TotalResults { get; set; }
}

public class TmdbSearchResult
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Name { get; set; } = "";
    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = "";
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }
    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }
    public string? Overview { get; set; }
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }
    [JsonPropertyName("genre_ids")]
    public int[]? GenreIds { get; set; }

    [JsonIgnore] public string DisplayTitle => !string.IsNullOrEmpty(Title) ? Title : Name;
    [JsonIgnore] public string? PosterUrl => PosterPath != null ? $"https://image.tmdb.org/t/p/w342{PosterPath}" : null;
    [JsonIgnore] public string? DateStr => ReleaseDate ?? FirstAirDate;
    [JsonIgnore] public int? YearParsed => DateStr is { Length: >= 4 } ? int.TryParse(DateStr[..4], out var y) ? y : null : null;
}

public class TmdbTvDetail
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    [JsonPropertyName("number_of_seasons")]
    public int NumberOfSeasons { get; set; }
    public TmdbSeasonSummary[]? Seasons { get; set; }
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }
    public string? Overview { get; set; }
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }
}

public class TmdbSeasonSummary
{
    public int Id { get; set; }
    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }
    public string Name { get; set; } = "";
    [JsonPropertyName("episode_count")]
    public int EpisodeCount { get; set; }
    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }
}

public class TmdbSeason
{
    public int Id { get; set; }
    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }
    public string Name { get; set; } = "";
    public TmdbEpisode[]? Episodes { get; set; }
}

public class TmdbEpisode
{
    public int Id { get; set; }
    [JsonPropertyName("episode_number")]
    public int EpisodeNumber { get; set; }
    public string Name { get; set; } = "";
    public string? Overview { get; set; }
    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }
    [JsonPropertyName("still_path")]
    public string? StillPath { get; set; }
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }
    public int? Runtime { get; set; }

    [JsonIgnore] public string DisplayName => $"E{EpisodeNumber:D2} - {Name}";
    public string SearchQuery(string showName, int season) =>
        $"{showName} S{season:D2}E{EpisodeNumber:D2}";
}

