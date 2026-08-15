using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Serilog;

namespace GlDrive.Player;

public record TorrentSearchResult(
    string Title,
    string DetailUrl,
    int Seeds,
    int Leeches,
    string Size,
    string Uploader);

/// <summary>
/// Multi-source torrent metadata search.
///
/// Source list overhauled 2026-08-15 after probing every configured endpoint live:
///   * apibay.org  — DNS resolves and TCP 443 connects, but TLS returns zero bytes and times
///                   out. That is SNI-level filtering on the local network, not a dead site,
///                   so it stays in the list (it works elsewhere) behind the retry policy.
///   * SolidTorrents — solidtorrents.to, .net and bitsearch.to ALL redirect to one
///                   bitsearch.eu backend returning HTTP 500. The three-host "fallback list"
///                   was one backend wearing three names and provided zero redundancy, which
///                   is why all three died together. Removed.
///   * Knaben      — added, and the most valuable of the set: a meta-indexer federating The
///                   Pirate Bay, RuTracker, Nyaa and others, returning a ready-made magnetUrl.
///                   It restores TPB coverage WITHOUT touching the SNI-blocked apibay host.
///   * EZTV, Nyaa  — added; both verified returning live data. Narrow (TV and anime) but
///                   reliable, and they cover what Knaben's ranking sometimes buries.
///
/// Availability is owned by <see cref="SourceAvailabilityPolicy"/>. It used to be a per-source
/// one-shot latch that disabled a backend for the whole process lifetime after one failed
/// probe — see that class for the full root cause.
/// </summary>
public class TorrentSearchService : IDisposable
{
    private readonly HttpClient _http;
    private readonly SourceAvailabilityPolicy _availability = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string KnabenApi = "https://api.knaben.org/v1";
    private const string ApiBayApi = "https://apibay.org/q.php";
    private const string TorrentsCsvApi = "https://torrents-csv.com/service/search";
    private const string EztvApi = "https://eztvx.to/api/get-torrents";
    private const string NyaaRss = "https://nyaa.si/";

    // yts.mx does not resolve from some networks (including this one) and yts.rs returns 500;
    // the .lt mirror answers. Movies only, but quality-labelled and generally well seeded.
    private const string YtsApi = "https://yts.lt/api/v2/list_movies.json";

    private const string LimeTorrentsRss = "https://www.limetorrents.lol/searchrss/";

    /// <summary>
    /// Optional proxy for search traffic only. Public indexers are blocked at DNS or SNI level
    /// on some networks — this machine cannot reach apibay.org or resolve yts.mx — and a proxy
    /// is the only thing that routes around SNI filtering (DNS-over-HTTPS would not: the block
    /// is on the TLS ClientHello, not the lookup). Null means direct.
    /// </summary>
    public TorrentSearchService(string? proxyUrl = null)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            try
            {
                handler.Proxy = new WebProxy(proxyUrl);
                handler.UseProxy = true;
                Log.Information("TorrentSearchService: routing search traffic via proxy {Proxy}", proxyUrl);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "TorrentSearchService: invalid proxy {Proxy} — searching direct", proxyUrl);
            }
        }

        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<List<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var sources = new (string Name, Task<List<TorrentSearchResult>> Task)[]
        {
            ("knaben", SearchKnaben(query, ct)),
            ("apibay", SearchApiBay(query, ct)),
            ("csv",    SearchTorrentsCsv(query, ct)),
            ("eztv",   SearchEztv(query, ct)),
            ("nyaa",   SearchNyaa(query, ct)),
            ("yts",    SearchYts(query, ct)),
            ("lime",   SearchLimeTorrents(query, ct)),
        };

        await Task.WhenAll(sources.Select(s => s.Task));

        var combined = new List<TorrentSearchResult>();
        foreach (var s in sources) combined.AddRange(s.Task.Result);

        if (combined.Count == 0)
        {
            // Every source contributing zero is worth a warning, not a debug line: it is the
            // shape of "search is broken" that went unnoticed for weeks because two dead
            // backends failed silently and the survivor returned a handful of results.
            Log.Warning("Torrent search for \"{Query}\": NO results from any source ({States})",
                query, string.Join(", ", _availability.Snapshot().Select(kv => $"{kv.Key}={kv.Value}")));
            return combined;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<TorrentSearchResult>();
        foreach (var r in combined.OrderByDescending(r => r.Seeds))
        {
            var hash = ExtractInfoHash(r.DetailUrl);
            if (hash != null && !seen.Add(hash)) continue;
            deduped.Add(r);
        }

        var results = deduped.Take(50).ToList();
        Log.Information("Torrent search for \"{Query}\": {Count} results ({Breakdown}, {Dupes} dupes removed)",
            query, results.Count,
            string.Join(" + ", sources.Select(s => $"{s.Task.Result.Count} {s.Name}")),
            combined.Count - deduped.Count);

        return results;
    }

    /// <summary>
    /// Run a source only if the policy says it is worth trying, and report the outcome back so
    /// a failure serves a cooldown instead of a life sentence.
    /// </summary>
    private async Task<List<TorrentSearchResult>> RunSource(
        string name,
        Func<Task<List<TorrentSearchResult>>> search)
    {
        var now = DateTime.UtcNow;
        if (!_availability.IsUsable(name, now) && !_availability.ShouldProbe(name, now))
            return [];

        try
        {
            var results = await search();
            _availability.MarkAvailable(name, DateTime.UtcNow);
            return results;
        }
        catch (TaskCanceledException)
        {
            // The caller's cancellation, or our own 15s timeout. Neither is evidence about the
            // source, so it keeps its standing — the same distinction ConnectFailureClassifier
            // draws for FTP borrows.
            return [];
        }
        catch (Exception ex)
        {
            _availability.MarkUnavailable(name, DateTime.UtcNow);
            Log.Warning(ex, "Torrent source {Source} failed — sitting out {Cooldown}",
                name, SourceAvailabilityPolicy.RetryAfter);
            return [];
        }
    }

    // ── Knaben (meta-indexer: TPB, RuTracker, Nyaa, …) ──

    private Task<List<TorrentSearchResult>> SearchKnaben(string query, CancellationToken ct) =>
        RunSource("knaben", async () =>
        {
            var payload = JsonSerializer.Serialize(new
            {
                search_field = "title",
                query,
                order_by = "seeders",
                order_direction = "desc",
                from = 0,
                size = 50,
                hide_unsafe = true,
                hide_xxx = true,
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(KnabenApi, content, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var data = JsonSerializer.Deserialize<KnabenResponse>(json, JsonOptions);

            var results = new List<TorrentSearchResult>();
            if (data?.Hits == null) return results;

            foreach (var hit in data.Hits.Where(h => h.Seeders > 0))
            {
                // magnetUrl is supplied ready-made; fall back to building one from the hash
                // for the occasional hit that carries only metadata.
                var magnet = !string.IsNullOrEmpty(hit.MagnetUrl)
                    ? hit.MagnetUrl
                    : !string.IsNullOrEmpty(hit.Hash)
                        ? BuildMagnetLink(hit.Hash, hit.Title)
                        : null;
                if (magnet == null) continue;

                results.Add(new TorrentSearchResult(
                    WebUtility.HtmlDecode(hit.Title),
                    magnet,
                    hit.Seeders,
                    hit.Peers,
                    FormatBytes(hit.Bytes),
                    hit.Tracker ?? ""));
            }

            return results;
        });

    // ── apibay.org (TPB API) ──

    private Task<List<TorrentSearchResult>> SearchApiBay(string query, CancellationToken ct) =>
        RunSource("apibay", async () =>
        {
            var url = $"{ApiBayApi}?q={Uri.EscapeDataString(query)}&cat=0";
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var items = JsonSerializer.Deserialize<List<ApiBayResult>>(json, JsonOptions);

            var results = new List<TorrentSearchResult>();
            if (items == null || items.Count == 0) return results;
            // apibay signals "no matches" with a single dummy row rather than an empty array.
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

            return results;
        });

    // ── Torrents-CSV (open source DHT aggregator) ──

    private Task<List<TorrentSearchResult>> SearchTorrentsCsv(string query, CancellationToken ct) =>
        RunSource("csv", async () =>
        {
            var url = $"{TorrentsCsvApi}?q={Uri.EscapeDataString(query)}&size=30";
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var data = JsonSerializer.Deserialize<CsvResponse>(json, JsonOptions);

            var results = new List<TorrentSearchResult>();
            if (data?.Torrents == null) return results;

            foreach (var item in data.Torrents.Where(i => i.Seeders > 0).OrderByDescending(i => i.Seeders))
            {
                results.Add(new TorrentSearchResult(
                    item.Name,
                    BuildMagnetLink(item.InfoHash, item.Name),
                    item.Seeders,
                    item.Leechers,
                    FormatBytes(item.SizeBytes),
                    ""));
            }

            return results;
        });

    // ── EZTV (TV) ──

    private Task<List<TorrentSearchResult>> SearchEztv(string query, CancellationToken ct) =>
        RunSource("eztv", async () =>
        {
            var url = $"{EztvApi}?limit=100";
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var data = JsonSerializer.Deserialize<EztvResponse>(json, JsonOptions);

            var results = new List<TorrentSearchResult>();
            if (data?.Torrents == null) return results;

            // EZTV's endpoint filters by imdb_id, not free text, so match client-side on the
            // recent-releases feed. Cheap, and it surfaces new episodes the meta-indexers lag on.
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var item in data.Torrents.Where(t => t.Seeds > 0))
            {
                var title = item.Title ?? "";
                if (!terms.All(t => title.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;

                var magnet = !string.IsNullOrEmpty(item.MagnetUrl)
                    ? item.MagnetUrl
                    : !string.IsNullOrEmpty(item.Hash)
                        ? BuildMagnetLink(item.Hash, title)
                        : null;
                if (magnet == null) continue;

                results.Add(new TorrentSearchResult(
                    WebUtility.HtmlDecode(title),
                    magnet,
                    item.Seeds,
                    item.Peers,
                    FormatBytes(long.TryParse(item.SizeBytes, out var b) ? b : 0),
                    "EZTV"));
            }

            return results;
        });

    // ── Nyaa (anime, RSS) ──

    private Task<List<TorrentSearchResult>> SearchNyaa(string query, CancellationToken ct) =>
        RunSource("nyaa", async () =>
        {
            var url = $"{NyaaRss}?page=rss&q={Uri.EscapeDataString(query)}&s=seeders&o=desc";
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var xml = await response.Content.ReadAsStringAsync(ct);
            var doc = XDocument.Parse(xml);
            XNamespace ns = "https://nyaa.si/xmlns/nyaa";

            var results = new List<TorrentSearchResult>();
            foreach (var item in doc.Descendants("item").Take(30))
            {
                var hash = item.Element(ns + "infoHash")?.Value;
                var title = item.Element("title")?.Value ?? "";
                if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(title)) continue;

                int.TryParse(item.Element(ns + "seeders")?.Value, out var seeds);
                int.TryParse(item.Element(ns + "leechers")?.Value, out var leeches);
                if (seeds <= 0) continue;

                results.Add(new TorrentSearchResult(
                    WebUtility.HtmlDecode(title),
                    BuildMagnetLink(hash, title),
                    seeds,
                    leeches,
                    item.Element(ns + "size")?.Value ?? "",
                    "Nyaa"));
            }

            return results;
        });

    // ── YTS (movies) ──

    private Task<List<TorrentSearchResult>> SearchYts(string query, CancellationToken ct) =>
        RunSource("yts", async () =>
        {
            var url = $"{YtsApi}?query_term={Uri.EscapeDataString(query)}&limit=20&sort_by=seeds";
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var data = JsonSerializer.Deserialize<YtsResponse>(json, JsonOptions);

            var results = new List<TorrentSearchResult>();
            if (data?.Data?.Movies == null) return results;

            foreach (var movie in data.Data.Movies)
            {
                if (movie.Torrents == null) continue;

                foreach (var t in movie.Torrents)
                {
                    if (string.IsNullOrEmpty(t.Hash)) continue;

                    // Deliberately NOT filtered on seeds > 0 like the other sources: the
                    // list_movies endpoint reports 0 for every torrent on this mirror even
                    // though the swarms are alive. Dropping them would mean YTS never
                    // contributed a single result. They sort last by seed count anyway.
                    var title = $"{movie.TitleLong} [{t.Quality}]";
                    results.Add(new TorrentSearchResult(
                        WebUtility.HtmlDecode(title),
                        BuildMagnetLink(t.Hash, title),
                        t.Seeds,
                        t.Peers,
                        string.IsNullOrEmpty(t.Size) ? FormatBytes(t.SizeBytes) : t.Size,
                        "YTS"));
                }
            }

            return results;
        });

    // ── LimeTorrents (general, RSS) ──

    // The feed is Cloudflare-wrapped: a <script> block is appended AFTER </rss>, and titles
    // carry unescaped ampersands. Both make a strict XML parse throw, so items are extracted
    // with regex instead — deliberate, not laziness. XDocument.Parse fails on this feed.
    private static readonly Regex LimeItemRegex = new(
        @"<item>(?<body>.*?)</item>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LimeTitleRegex = new(
        @"<title>(?<v>.*?)</title>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LimeSizeRegex = new(
        @"<size>(?<v>\d+)</size>", RegexOptions.Compiled);
    private static readonly Regex LimeSeedsRegex = new(
        @"Seeds:\s*(?<s>\d+)\s*,\s*Leechers\s*(?<l>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // The info hash is only available as the itorrents.net filename in the enclosure URL.
    private static readonly Regex LimeHashRegex = new(
        @"itorrents\.net/torrent/(?<h>[A-Fa-f0-9]{40})", RegexOptions.Compiled);

    private Task<List<TorrentSearchResult>> SearchLimeTorrents(string query, CancellationToken ct) =>
        RunSource("lime", async () =>
        {
            var url = $"{LimeTorrentsRss}{Uri.EscapeDataString(query)}/";
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);

            var results = new List<TorrentSearchResult>();
            foreach (Match item in LimeItemRegex.Matches(body))
            {
                var chunk = item.Groups["body"].Value;

                var hash = LimeHashRegex.Match(chunk);
                if (!hash.Success) continue;

                var title = LimeTitleRegex.Match(chunk).Groups["v"].Value.Trim();
                if (string.IsNullOrEmpty(title)) continue;

                var seedsMatch = LimeSeedsRegex.Match(chunk);
                int.TryParse(seedsMatch.Groups["s"].Value, out var seeds);
                int.TryParse(seedsMatch.Groups["l"].Value, out var leeches);
                if (seeds <= 0) continue;

                long.TryParse(LimeSizeRegex.Match(chunk).Groups["v"].Value, out var size);

                title = WebUtility.HtmlDecode(title);
                results.Add(new TorrentSearchResult(
                    title,
                    BuildMagnetLink(hash.Groups["h"].Value, title),
                    seeds,
                    leeches,
                    FormatBytes(size),
                    "LimeTorrents"));
            }

            return results;
        });

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

    private class KnabenResponse
    {
        [JsonPropertyName("hits")]
        public List<KnabenHit> Hits { get; set; } = [];
    }

    private class KnabenHit
    {
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("hash")] public string Hash { get; set; } = "";
        [JsonPropertyName("magnetUrl")] public string? MagnetUrl { get; set; }
        [JsonPropertyName("seeders")] public int Seeders { get; set; }
        [JsonPropertyName("peers")] public int Peers { get; set; }
        [JsonPropertyName("bytes")] public long Bytes { get; set; }
        [JsonPropertyName("tracker")] public string? Tracker { get; set; }
    }

    private class ApiBayResult
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("info_hash")] public string InfoHash { get; set; } = "";
        [JsonPropertyName("seeders")] public int Seeders { get; set; }
        [JsonPropertyName("leechers")] public int Leechers { get; set; }
        [JsonPropertyName("size")] public string Size { get; set; } = "0";
        [JsonPropertyName("username")] public string Username { get; set; } = "";
        [JsonPropertyName("category")] public string Category { get; set; } = "";
    }

    private class CsvResponse
    {
        [JsonPropertyName("torrents")]
        public List<CsvResult> Torrents { get; set; } = [];
    }

    private class CsvResult
    {
        [JsonPropertyName("infohash")] public string InfoHash { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
        [JsonPropertyName("seeders")] public int Seeders { get; set; }
        [JsonPropertyName("leechers")] public int Leechers { get; set; }
    }

    private class EztvResponse
    {
        [JsonPropertyName("torrents")]
        public List<EztvResult> Torrents { get; set; } = [];
    }

    private class EztvResult
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("hash")] public string? Hash { get; set; }
        [JsonPropertyName("magnet_url")] public string? MagnetUrl { get; set; }
        [JsonPropertyName("seeds")] public int Seeds { get; set; }
        [JsonPropertyName("peers")] public int Peers { get; set; }
        [JsonPropertyName("size_bytes")] public string SizeBytes { get; set; } = "0";
    }

    private class YtsResponse
    {
        [JsonPropertyName("data")] public YtsData? Data { get; set; }
    }

    private class YtsData
    {
        [JsonPropertyName("movies")] public List<YtsMovie>? Movies { get; set; }
    }

    private class YtsMovie
    {
        [JsonPropertyName("title_long")] public string TitleLong { get; set; } = "";
        [JsonPropertyName("torrents")] public List<YtsTorrent>? Torrents { get; set; }
    }

    private class YtsTorrent
    {
        [JsonPropertyName("hash")] public string Hash { get; set; } = "";
        [JsonPropertyName("quality")] public string Quality { get; set; } = "";
        [JsonPropertyName("size")] public string Size { get; set; } = "";
        [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
        [JsonPropertyName("seeds")] public int Seeds { get; set; }
        [JsonPropertyName("peers")] public int Peers { get; set; }
    }
}
