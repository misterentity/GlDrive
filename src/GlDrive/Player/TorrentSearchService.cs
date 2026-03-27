using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace GlDrive.Player;

public record TorrentSearchResult(
    string Title,
    string DetailUrl,
    int Seeds,
    int Leeches,
    string Size,
    string Uploader);

public class TorrentSearchService : IDisposable
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // apibay state
    private static readonly string[] ApiBayHosts = ["https://apibay.org"];
    private string _apibayHost = "";
    private bool _apibayChecked;

    // SolidTorrents state (domain migrates frequently)
    private static readonly string[] SolidTorrentsHosts = [
        "https://solidtorrents.to/api/v1/search",
        "https://solidtorrents.net/api/v1/search",
        "https://bitsearch.to/api/v1/search"
    ];
    private string _solidHost = "";
    private bool _solidChecked;

    public TorrentSearchService()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<List<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        // Search both sources in parallel, merge and deduplicate
        var apibayTask = SearchApiBay(query, ct);
        var solidTask = SearchSolidTorrents(query, ct);

        await Task.WhenAll(apibayTask, solidTask);

        var combined = new List<TorrentSearchResult>();
        combined.AddRange(apibayTask.Result);
        combined.AddRange(solidTask.Result);

        if (combined.Count == 0)
        {
            Log.Debug("No torrent results from any source for \"{Query}\"", query);
            return combined;
        }

        // Deduplicate by info_hash (embedded in the magnet link)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<TorrentSearchResult>();
        foreach (var r in combined.OrderByDescending(r => r.Seeds))
        {
            var hash = ExtractInfoHash(r.DetailUrl);
            if (hash != null && !seen.Add(hash)) continue;
            deduped.Add(r);
        }

        var results = deduped.Take(30).ToList();
        Log.Information("Torrent search for \"{Query}\": {Count} results ({ApiBay} apibay + {Solid} solid, {Dupes} dupes removed)",
            query, results.Count, apibayTask.Result.Count, solidTask.Result.Count,
            combined.Count - deduped.Count);

        return results;
    }

    // ── apibay.org (TPB API) ──

    private async Task<List<TorrentSearchResult>> SearchApiBay(string query, CancellationToken ct)
    {
        var results = new List<TorrentSearchResult>();
        try
        {
            if (!_apibayChecked)
            {
                _apibayChecked = true;
                foreach (var host in ApiBayHosts)
                {
                    try
                    {
                        var probe = await _http.GetAsync($"{host}/q.php?q=test&cat=0", ct);
                        if (probe.IsSuccessStatusCode)
                        {
                            _apibayHost = host;
                            Log.Information("apibay available: {Host}", host);
                            break;
                        }
                    }
                    catch (Exception ex) { Log.Debug(ex, "apibay probe failed for {Host}", host); }
                }
            }

            if (string.IsNullOrEmpty(_apibayHost)) return results;

            var url = $"{_apibayHost}/q.php?q={Uri.EscapeDataString(query)}&cat=0";
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable)
                {
                    _apibayHost = "";
                    _apibayChecked = false;
                }
                return results;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var items = JsonSerializer.Deserialize<List<ApiBayResult>>(json, JsonOptions);
            if (items == null || items.Count == 0) return results;
            if (items.Count == 1 && items[0].Id == "0") return results;

            foreach (var item in items.Where(i => i.Seeders > 0).OrderByDescending(i => i.Seeders).Take(30))
            {
                results.Add(new TorrentSearchResult(
                    WebUtility.HtmlDecode(item.Name),
                    BuildMagnetLink(item.InfoHash, item.Name),
                    item.Seeders,
                    item.Leechers,
                    FormatBytes(long.TryParse(item.Size, out var b) ? b : 0),
                    item.Username));
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception ex) { Log.Warning(ex, "apibay search failed"); }
        return results;
    }

    // ── SolidTorrents ──

    private async Task<List<TorrentSearchResult>> SearchSolidTorrents(string query, CancellationToken ct)
    {
        var results = new List<TorrentSearchResult>();
        try
        {
            if (!_solidChecked)
            {
                _solidChecked = true;
                foreach (var host in SolidTorrentsHosts)
                {
                    try
                    {
                        var probe = await _http.GetAsync($"{host}?q=test&sort=seeders", ct);
                        if (probe.IsSuccessStatusCode)
                        {
                            _solidHost = host;
                            Log.Information("SolidTorrents available: {Host}", host);
                            break;
                        }
                    }
                    catch (Exception ex) { Log.Debug(ex, "SolidTorrents probe failed for {Host}", host); }
                }
            }

            if (string.IsNullOrEmpty(_solidHost)) return results;

            var url = $"{_solidHost}?q={Uri.EscapeDataString(query)}&category=video&sort=seeders";
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable)
                {
                    _solidHost = "";
                    _solidChecked = false;
                }
                return results;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var data = JsonSerializer.Deserialize<SolidResponse>(json, JsonOptions);
            if (data?.Results == null || data.Results.Count == 0) return results;

            foreach (var item in data.Results.Where(i => i.Seeders > 0).Take(30))
            {
                results.Add(new TorrentSearchResult(
                    WebUtility.HtmlDecode(item.Title),
                    BuildMagnetLink(item.InfoHash, item.Title),
                    item.Seeders,
                    item.Leechers,
                    FormatBytes(item.Size),
                    ""));
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception ex) { Log.Warning(ex, "SolidTorrents search failed"); }
        return results;
    }

    /// <summary>
    /// Returns the magnet link. With API backends, the magnet is stored directly in DetailUrl.
    /// </summary>
    public Task<string?> GetMagnetLinkAsync(string detailPath, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(detailPath) && detailPath.StartsWith("magnet:"))
            return Task.FromResult<string?>(detailPath);

        return Task.FromResult<string?>(null);
    }

    private static string? ExtractInfoHash(string magnetUrl)
    {
        if (string.IsNullOrEmpty(magnetUrl)) return null;
        var idx = magnetUrl.IndexOf("btih:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = idx + 5;
        var end = magnetUrl.IndexOf('&', start);
        return end > start ? magnetUrl[start..end] : magnetUrl[start..];
    }

    private static string BuildMagnetLink(string infoHash, string name)
    {
        var encodedName = Uri.EscapeDataString(name);
        return $"magnet:?xt=urn:btih:{infoHash}&dn={encodedName}" +
               "&tr=udp://tracker.opentrackr.org:1337/announce" +
               "&tr=udp://open.stealth.si:80/announce" +
               "&tr=udp://tracker.torrent.eu.org:451/announce" +
               "&tr=udp://tracker.bittor.pw:1337/announce" +
               "&tr=udp://tracker.openbittorrent.com:6969/announce";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F0} KB";
        return $"{bytes} B";
    }

    public void Dispose()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── DTOs ──

    private class ApiBayResult
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("info_hash")]
        public string InfoHash { get; set; } = "";

        [JsonPropertyName("seeders")]
        public int Seeders { get; set; }

        [JsonPropertyName("leechers")]
        public int Leechers { get; set; }

        [JsonPropertyName("size")]
        public string Size { get; set; } = "0";

        [JsonPropertyName("username")]
        public string Username { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "";
    }

    private class SolidResponse
    {
        [JsonPropertyName("results")]
        public List<SolidResult> Results { get; set; } = [];
    }

    private class SolidResult
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("infohash")]
        public string InfoHash { get; set; } = "";

        [JsonPropertyName("seeders")]
        public int Seeders { get; set; }

        [JsonPropertyName("leechers")]
        public int Leechers { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
