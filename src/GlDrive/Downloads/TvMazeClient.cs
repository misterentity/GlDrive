using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    public async Task<TvMazeScheduleEpisode[]> GetSchedule(DateOnly date, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetStringAsync($"schedule?country=US&date={date:yyyy-MM-dd}", ct);
            return JsonSerializer.Deserialize<TvMazeScheduleEpisode[]>(response, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TVMaze schedule failed for date: {Date}", date);
            return [];
        }
    }

    // Major English-language streaming services by TvMaze webChannel ID
    private static readonly HashSet<int> StreamingChannelIds = [
        1,    // Netflix
        2,    // Hulu
        3,    // Prime Video
        107,  // Paramount+
        287,  // Disney+
        310,  // Apple TV+
        329,  // HBO Max
        347,  // Peacock
    ];

    public async Task<TvMazeScheduleEpisode[]> GetWebSchedule(DateOnly date, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetStringAsync($"schedule/web?date={date:yyyy-MM-dd}", ct);
            var episodes = JsonSerializer.Deserialize<TvMazeWebScheduleEpisode[]>(response, JsonOptions) ?? [];

            // Convert to standard episodes, filtering to major English streaming services
            return episodes
                .Where(e => e.Embedded?.Show != null &&
                            e.Embedded.Show.WebChannel != null &&
                            StreamingChannelIds.Contains(e.Embedded.Show.WebChannel.Id))
                .Select(e =>
                {
                    var ep = new TvMazeScheduleEpisode
                    {
                        Id = e.Id,
                        Name = e.Name,
                        Season = e.Season,
                        Number = e.Number,
                        Airdate = e.Airdate,
                        Airtime = e.Airtime,
                        Runtime = e.Runtime,
                        Show = e.Embedded!.Show
                    };
                    return ep;
                })
                .ToArray();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TVMaze web schedule failed for date: {Date}", date);
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

    public async Task<TvMazeNextEpisodeResult?> GetShowWithNextEpisode(int id, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetStringAsync($"shows/{id}?embed=nextepisode", ct);
            var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            var show = JsonSerializer.Deserialize<TvMazeShow>(response, JsonOptions);
            if (show == null) return null;

            TvMazeScheduleEpisode? nextEp = null;
            if (root.TryGetProperty("_embedded", out var embedded) &&
                embedded.TryGetProperty("nextepisode", out var nextepisode))
            {
                nextEp = JsonSerializer.Deserialize<TvMazeScheduleEpisode>(nextepisode.GetRawText(), JsonOptions);
            }

            return new TvMazeNextEpisodeResult { Show = show, NextEpisode = nextEp };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TVMaze get show with next episode failed for id: {Id}", id);
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
    public string? Type { get; set; }
    public string? Premiered { get; set; }
    public string? Status { get; set; }
    public TvMazeImage? Image { get; set; }
    public string? Summary { get; set; }
    public string[]? Genres { get; set; }
    public TvMazeRating? Rating { get; set; }
    public TvMazeNetwork? Network { get; set; }
    public TvMazeNetwork? WebChannel { get; set; }

    public string NetworkName => Network?.Name ?? WebChannel?.Name ?? "";
    public int? PremieredYear => Premiered != null && DateTime.TryParse(Premiered, out var dt) ? dt.Year : null;
}

public class TvMazeRating
{
    public double? Average { get; set; }
}

public class TvMazeImage
{
    public string? Medium { get; set; }
    public string? Original { get; set; }
}

public class TvMazeScheduleEpisode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Season { get; set; }
    public int? Number { get; set; }
    public string? Airdate { get; set; }
    public string? Airtime { get; set; }
    public int? Runtime { get; set; }
    public TvMazeShow? Show { get; set; }
}

// Web schedule episodes have the show inside _embedded instead of a direct property
public class TvMazeWebScheduleEpisode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Season { get; set; }
    public int? Number { get; set; }
    public string? Airdate { get; set; }
    public string? Airtime { get; set; }
    public int? Runtime { get; set; }

    [JsonPropertyName("_embedded")]
    public TvMazeWebEmbedded? Embedded { get; set; }
}

public class TvMazeWebEmbedded
{
    public TvMazeShow? Show { get; set; }
}

public class TvMazeNetwork
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class TvMazeNextEpisodeResult
{
    public TvMazeShow Show { get; set; } = null!;
    public TvMazeScheduleEpisode? NextEpisode { get; set; }
}
