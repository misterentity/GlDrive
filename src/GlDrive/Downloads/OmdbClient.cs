using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace GlDrive.Downloads;

public class OmdbClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OmdbClient(string apiKey)
    {
        _apiKey = apiKey;
        _http = new HttpClient { BaseAddress = new Uri("https://www.omdbapi.com/") };
    }

    public bool HasApiKey => !string.IsNullOrEmpty(_apiKey);

    public async Task<OmdbMovie[]> Search(string query, CancellationToken ct = default)
    {
        if (!HasApiKey) return [];

        try
        {
            var url = $"?apikey={_apiKey}&s={Uri.EscapeDataString(query)}&type=movie";
            var response = await _http.GetStringAsync(url, ct);
            var result = JsonSerializer.Deserialize<OmdbSearchResponse>(response, JsonOptions);
            return result?.Search ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OMDB search failed for: {Query}", query);
            return [];
        }
    }

    public async Task<OmdbMovie?> GetById(string imdbId, CancellationToken ct = default)
    {
        if (!HasApiKey) return null;

        try
        {
            var url = $"?apikey={_apiKey}&i={Uri.EscapeDataString(imdbId)}";
            var response = await _http.GetStringAsync(url, ct);
            return JsonSerializer.Deserialize<OmdbMovie>(response, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OMDB get by id failed for: {Id}", imdbId);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
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

    public int? YearParsed => int.TryParse(Year?.Split('–')[0], out var y) ? y : null;
}
