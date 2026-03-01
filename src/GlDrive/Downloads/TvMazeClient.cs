using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace GlDrive.Downloads;

public class TvMazeClient : IDisposable
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TvMazeClient()
    {
        _http = new HttpClient { BaseAddress = new Uri("https://api.tvmaze.com/") };
        _http.DefaultRequestHeaders.Add("User-Agent", "GlDrive/1.0");
    }

    public async Task<TvMazeShow[]> Search(string query, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetStringAsync($"search/shows?q={Uri.EscapeDataString(query)}", ct);
            var results = JsonSerializer.Deserialize<TvMazeSearchResult[]>(response, JsonOptions) ?? [];
            return results.Select(r => r.Show).Where(s => s != null).ToArray()!;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TVMaze search failed for: {Query}", query);
            return [];
        }
    }

    public async Task<TvMazeShow?> GetShow(int id, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetStringAsync($"shows/{id}", ct);
            return JsonSerializer.Deserialize<TvMazeShow>(response, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TVMaze get show failed for id: {Id}", id);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

public class TvMazeSearchResult
{
    public double Score { get; set; }
    public TvMazeShow? Show { get; set; }
}

public class TvMazeShow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Premiered { get; set; }
    public string? Status { get; set; }
    public TvMazeImage? Image { get; set; }

    public int? PremieredYear => Premiered != null && DateTime.TryParse(Premiered, out var dt) ? dt.Year : null;
}

public class TvMazeImage
{
    public string? Medium { get; set; }
    public string? Original { get; set; }
}
