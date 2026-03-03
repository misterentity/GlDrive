using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace GlDrive.Downloads;

public class PreDbClient : IDisposable
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public PreDbClient()
    {
        _http = new HttpClient { BaseAddress = new Uri("https://api.predb.net/") };
        var version = typeof(PreDbClient).Assembly.GetName().Version;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"GlDrive/{version}");
    }

    public async Task<PreDbRelease[]> SearchAsync(string query, int count = 30, int page = 0, CancellationToken ct = default)
    {
        try
        {
            var url = $"?q={Uri.EscapeDataString(query)}&count={count}&page={page}";
            var json = await _http.GetStringAsync(url, ct);
            var resp = JsonSerializer.Deserialize<PreDbResponse>(json, JsonOptions);
            return resp?.Data ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PreDB search failed for: {Query}", query);
            return [];
        }
    }

    public async Task<PreDbRelease[]> GetLatestAsync(int count = 30, CancellationToken ct = default)
    {
        try
        {
            var url = $"?count={count}";
            var json = await _http.GetStringAsync(url, ct);
            var resp = JsonSerializer.Deserialize<PreDbResponse>(json, JsonOptions);
            return resp?.Data ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PreDB latest fetch failed");
            return [];
        }
    }

    public void Dispose() => _http.Dispose();
}

public class PreDbResponse
{
    public string Status { get; set; } = "";
    public int Results { get; set; }
    public PreDbRelease[] Data { get; set; } = [];
}

public class PreDbRelease
{
    public int Id { get; set; }
    public string Release { get; set; } = "";
    public string Group { get; set; } = "";
    public string Section { get; set; } = "";
    public string Genre { get; set; } = "";
    public double Size { get; set; }
    public int Files { get; set; }
    [JsonPropertyName("pretime")]
    public long PreAt { get; set; }
    public int Status { get; set; }
    public string Reason { get; set; } = "";

    [JsonIgnore]
    public DateTime PreTime => DateTimeOffset.FromUnixTimeSeconds(PreAt).LocalDateTime;
    [JsonIgnore]
    public bool IsNuked => Status == 3;

    public string SizeFormatted
    {
        get
        {
            if (Size <= 0) return "";
            double mb = Size;
            if (mb < 1024) return $"{mb:F1} MB";
            double gb = mb / 1024;
            return $"{gb:F2} GB";
        }
    }
}
