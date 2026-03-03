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
        _http = new HttpClient { BaseAddress = new Uri("https://predb.ovh/api/v1/") };
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
            return resp?.Data?.Rows ?? [];
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
            return resp?.Data?.Rows ?? [];
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
    public PreDbData? Data { get; set; }
}

public class PreDbData
{
    [JsonPropertyName("rowCount")]
    public int Total { get; set; }
    public PreDbRelease[] Rows { get; set; } = [];
}

public class PreDbRelease
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Team { get; set; } = "";
    public string Cat { get; set; } = "";
    public string Genre { get; set; } = "";
    public double Size { get; set; }
    public int Files { get; set; }
    public long PreAt { get; set; }
    public PreDbNuke? Nuke { get; set; }

    public DateTime PreTime => DateTimeOffset.FromUnixTimeSeconds(PreAt).LocalDateTime;

    public string SizeFormatted
    {
        get
        {
            if (Size <= 0) return "";
            double kb = Size;
            if (kb < 1024) return $"{kb:F0} KB";
            double mb = kb / 1024;
            if (mb < 1024) return $"{mb:F1} MB";
            double gb = mb / 1024;
            return $"{gb:F2} GB";
        }
    }
}

public class PreDbNuke
{
    public string Reason { get; set; } = "";
}
