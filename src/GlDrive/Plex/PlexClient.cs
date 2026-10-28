using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Serilog;

namespace GlDrive.Plex;

/// <summary>
/// Low-level Plex.tv + Plex Media Server HTTP client. Holds the stable client
/// identifier and (optionally) the auth token, attaches the standard X-Plex-*
/// headers, and exposes one method per raw endpoint. Response parsing is split into
/// static pure functions (ParseResources / ParseLibraries / ParseSharedUsers) so the
/// fiddly bits are unit-testable without hitting the network.
///
/// Endpoints:
///   - OAuth PIN flow + resource discovery use the documented JSON v2 API.
///   - Library sections come from the server itself (JSON).
///   - Sharing (list/invite/update/revoke) uses the long-stable classic XML
///     /api/servers/{machineId}/shared_servers API that PlexAPI's inviteFriend/
///     removeFriend wrap — the same operations helpr's plex_service.py performed.
/// </summary>
public sealed partial class PlexClient : IDisposable, IPlexWatchlistReader
{
    private const string TvBase = "https://plex.tv";
    private readonly HttpClient _http;
    private readonly HttpClient _libraryHttp;
    private readonly string _clientId;
    private readonly string _product;
    private readonly string _version;

    public string? Token { get; set; }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public PlexClient(string clientId, string product, string version, string? token = null)
        : this(clientId, product, version, token, new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    internal PlexClient(string clientId, string product, string version, string? token,
        HttpMessageHandler libraryHandler)
    {
        _clientId = clientId;
        _product = product;
        _version = version;
        Token = token;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // Catalog reads and targeted scans never follow a redirect with the Plex token.
        // Keep this independent of the existing Plex.tv authentication/sharing transport.
        _libraryHttp = new HttpClient(libraryHandler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private HttpRequestMessage Build(HttpMethod method, string url, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Add("X-Plex-Client-Identifier", _clientId);
        req.Headers.Add("X-Plex-Product", _product);
        req.Headers.Add("X-Plex-Version", _version);
        req.Headers.Add("X-Plex-Device", "Windows");
        req.Headers.Add("X-Plex-Platform", "Windows");
        if (!string.IsNullOrEmpty(Token)) req.Headers.Add("X-Plex-Token", Token);
        return req;
    }

    // ---- OAuth PIN flow ----

    public async Task<PlexPin> CreatePinAsync(CancellationToken ct)
    {
        using var resp = await _http.SendAsync(Build(HttpMethod.Post, $"{TvBase}/api/v2/pins?strong=true"), ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<PlexPin>(body, Json)
               ?? throw new PlexException("Plex returned an empty PIN response.");
    }

    public async Task<PlexPin?> PollPinAsync(long pinId, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(Build(HttpMethod.Get, $"{TvBase}/api/v2/pins/{pinId}"), ct);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<PlexPin>(body, Json);
    }

    /// <summary>Browser URL the user authorizes; on success Plex marks the PIN authorized.</summary>
    public string BuildAuthUrl(string code) =>
        "https://app.plex.tv/auth#?" +
        $"clientID={Uri.EscapeDataString(_clientId)}" +
        $"&code={Uri.EscapeDataString(code)}" +
        $"&context%5Bdevice%5D%5Bproduct%5D={Uri.EscapeDataString(_product)}";

    // ---- discovery ----

    public async Task<List<PlexResource>> GetResourcesAsync(CancellationToken ct)
    {
        var url = $"{TvBase}/api/v2/resources?includeHttps=1&includeRelay=1";
        using var resp = await _http.SendAsync(Build(HttpMethod.Get, url), ct);
        resp.EnsureSuccessStatusCode();
        return ParseResources(await resp.Content.ReadAsStringAsync(ct));
    }

    public async Task<List<PlexLibrary>> GetLibrariesAsync(string serverUri, CancellationToken ct)
    {
        var url = $"{serverUri.TrimEnd('/')}/library/sections";
        using var resp = await _http.SendAsync(Build(HttpMethod.Get, url), ct);
        resp.EnsureSuccessStatusCode();
        return ParseLibraries(await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Read catalog roots without discovery, connection self-healing or config saves.
    /// Malformed responses are errors, so callers cannot mistake an unknown catalog for an empty one.</summary>
    public async Task<List<PlexLibrary>> GetLibrarySectionsAsync(string serverUri, CancellationToken ct)
    {
        using var request = Build(HttpMethod.Get, LibraryUrl(serverUri, "library/sections"));
        using var response = await _libraryHttp.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var container = RequiredObject(doc.RootElement, "MediaContainer");
        if (!container.TryGetProperty("Directory", out var directories))
        {
            if (OptionalInt(container, "size") == 0) return [];
            throw new PlexException("Plex returned no library sections.");
        }
        if (directories.ValueKind != JsonValueKind.Array)
            throw new PlexException("Plex returned malformed library sections.");
        var sections = directories.EnumerateArray().Select(ParseLibrary).ToList();
        if (OptionalInt(container, "size") is { } size && size != sections.Count)
            throw new PlexException("Plex returned incomplete library sections.");
        if (sections.Any(s => !ValidSectionId(s.Key)) ||
            sections.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != sections.Count)
            throw new PlexException("Plex returned ambiguous library sections.");
        return sections;
    }

    /// <summary>Enumerate movie or episode metadata, retaining media paths as observations only.
    /// Pagination has no catalog-size cap; missing or inconsistent pages fail the read.</summary>
    public async Task<IReadOnlyList<PlexLibraryItem>> GetLibraryItemsAsync(string serverUri,
        PlexLibrary section, CancellationToken ct)
    {
        if (!ValidSectionId(section.Key)) throw new PlexException("A numeric Plex section is required.");
        var type = section.Type switch
        {
            "movie" => 1,
            "show" => 4,
            _ => throw new PlexException("Only movie and television sections are supported by the library index.")
        };
        const int pageSize = 250;
        var result = new List<PlexLibraryItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? expectedTotal = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var start = result.Count;
            var url = LibraryUrl(serverUri, $"library/sections/{section.Key}/all?type={type}&includeGuids=1" +
                $"&X-Plex-Container-Start={start}&X-Plex-Container-Size={pageSize}");
            using var request = Build(HttpMethod.Get, url);
            using var response = await _libraryHttp.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var container = RequiredObject(doc.RootElement, "MediaContainer");
            var size = OptionalInt(container, "size") ?? throw new PlexException("Plex returned no page size.");
            var offset = OptionalInt(container, "offset");
            var total = OptionalInt(container, "totalSize");
            if (size < 0 || size > pageSize || offset is not null && offset != start ||
                total < 0 || expectedTotal is not null && total != expectedTotal)
                throw new PlexException("Plex catalog changed or returned inconsistent pagination; retry the read.");
            expectedTotal ??= total;
            var rows = new List<PlexLibraryItem>();
            if (container.TryGetProperty("Metadata", out var metadata))
            {
                if (metadata.ValueKind != JsonValueKind.Array)
                    throw new PlexException("Plex returned malformed catalog items.");
                foreach (var item in metadata.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    rows.Add(ParseLibraryItem(item, type == 1 ? "movie" : "episode"));
                }
            }
            if (rows.Count != size || rows.Any(row => !seen.Add(row.RatingKey)))
                throw new PlexException("Plex returned an incomplete or repeated catalog page.");
            result.AddRange(rows);
            if (expectedTotal is not null)
            {
                if (result.Count > expectedTotal || size == 0 && result.Count < expectedTotal)
                    throw new PlexException("Plex returned an incomplete catalog.");
                if (result.Count == expectedTotal) return result.AsReadOnly();
            }
            // PMS may enforce a smaller page than requested without sending totalSize.
            // A short nonempty page is not evidence that the catalog has ended.
            else if (size == 0) return result.AsReadOnly();
        }
    }

    /// <summary>Request a scan of one verified imported folder. The supplied paths must be in
    /// Plex's filesystem namespace; no root scan, fallback scan, or library-item deletion exists.</summary>
    public async Task ScanImportedFolderAsync(string serverUri, string sectionId, string plexRoot,
        string importedFolder, string mediaKind, CancellationToken ct)
    {
        var expectedType = ImportSectionType(mediaKind);
        if (expectedType == null || !ValidSectionId(sectionId) || !IsWithinPlexRoot(plexRoot, importedFolder))
            throw new PlexException("A specific imported folder beneath a Plex root is required.");
        var sections = await GetLibrarySectionsAsync(serverUri, ct).ConfigureAwait(false);
        var section = sections.SingleOrDefault(s => s.Key == sectionId);
        if (section is null || section.Type != expectedType ||
            section.Roots.Count(root => SamePlexPath(root, plexRoot)) != 1)
            throw new PlexException("The selected Plex root is not uniquely advertised by that library section.");
        ct.ThrowIfCancellationRequested();
        var url = LibraryUrl(serverUri, $"library/sections/{sectionId}/refresh?path={Uri.EscapeDataString(importedFolder)}");
        using var request = Build(HttpMethod.Post, url);
        using var response = await _libraryHttp.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public Task ScanImportedFolderAsync(string serverUri, int sectionId, string plexRoot,
        string importedFolder, string mediaKind, CancellationToken ct) => ScanImportedFolderAsync(serverUri,
            sectionId.ToString(System.Globalization.CultureInfo.InvariantCulture), plexRoot, importedFolder, mediaKind, ct);

    /// <summary>Resolve only an unambiguous advertised root. Remote path mappings are never guessed.</summary>
    public static (int SectionId, string Root)? FindImportedFolderTarget(
        IEnumerable<PlexLibrary> sections, string importedFolder, string mediaKind)
    {
        var expectedType = ImportSectionType(mediaKind);
        if (expectedType == null) return null;
        var candidates = new List<(int SectionId, string Root)>();
        foreach (var section in sections.Where(s => s.Type == expectedType))
            if (int.TryParse(section.Key, out var sectionId) && sectionId > 0)
                candidates.AddRange(section.Roots.Where(root => IsWithinPlexRoot(root, importedFolder))
                    .Select(root => (sectionId, root)));
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static string? ImportSectionType(string mediaKind) => mediaKind switch
    {
        "movie" => "movie", "episode" => "show", _ => null
    };

    /// <summary>Pure Plex-path containment, supporting Windows and POSIX server roots.
    /// Equality, traversal and ambiguous Windows path spellings are not imported subfolders.</summary>
    public static bool IsWithinPlexRoot(string root, string folder)
    {
        if (!TryNormalizePlexPath(root, out var parent, out var windows) ||
            !TryNormalizePlexPath(folder, out var child, out var childWindows) || windows != childWindows)
            return false;
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var separator = windows ? '\\' : '/';
        var prefix = parent.EndsWith(separator) ? parent : parent + separator;
        return child.Length > parent.Length && child.StartsWith(prefix, comparison);
    }

    private static bool SamePlexPath(string first, string second) =>
        TryNormalizePlexPath(first, out var a, out var windows) &&
        TryNormalizePlexPath(second, out var b, out var otherWindows) && windows == otherWindows &&
        a.Equals(b, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool TryNormalizePlexPath(string path, out string normalized, out bool windows)
    {
        normalized = "";
        windows = false;
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl)) return false;
        var drive = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';
        var unc = path.StartsWith("\\\\", StringComparison.Ordinal);
        windows = drive || unc;
        if (!windows && (!path.StartsWith('/') || path.Contains('\\'))) return false;
        var value = windows ? path.Replace('/', '\\') : path;
        var rest = drive ? value[3..] : unc ? value[2..] : value[1..];
        var parts = rest.TrimEnd(windows ? '\\' : '/').Split(windows ? '\\' : '/');
        var isWindows = windows;
        if (parts.Any(p => p is "." or ".." || p.Length == 0 && rest.Length != 0 ||
            isWindows && (p.EndsWith(' ') || p.EndsWith('.') || p.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0)))
            return false;
        if (unc && parts.Length < 2) return false;
        normalized = value.TrimEnd(windows ? '\\' : '/');
        if (normalized.Length == 0) normalized = "/";
        if (drive && normalized.Length == 2) normalized += "\\";
        return true;
    }

    private static bool ValidSectionId(string value) => value.Length > 0 && value.All(char.IsAsciiDigit) &&
        long.TryParse(value, out var number) && number > 0;

    private static string LibraryUrl(string serverUri, string relative)
    {
        if (!Uri.TryCreate(serverUri, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new PlexException("A configured HTTP or HTTPS Plex server address is required.");
        return serverUri.TrimEnd('/') + "/" + relative;
    }

    private static JsonElement RequiredObject(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object ? value : throw new PlexException("Plex returned a malformed catalog response.");

    private static string? OptionalString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static int? OptionalInt(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetInt32() : null;

    private static PlexLibrary ParseLibrary(JsonElement item)
    {
        var roots = new List<string>();
        if (item.TryGetProperty("Location", out var locations))
        {
            if (locations.ValueKind != JsonValueKind.Array) throw new PlexException("Plex returned malformed library roots.");
            foreach (var location in locations.EnumerateArray())
            {
                var path = OptionalString(location, "path");
                if (!string.IsNullOrWhiteSpace(path)) roots.Add(path);
            }
        }
        return new PlexLibrary
        {
            Key = OptionalString(item, "key") ?? "", Title = OptionalString(item, "title") ?? "",
            Type = OptionalString(item, "type") ?? "", Roots = roots
        };
    }

    private static PlexLibraryItem ParseLibraryItem(JsonElement item, string expectedType)
    {
        var key = OptionalString(item, "ratingKey");
        var title = OptionalString(item, "title");
        var type = OptionalString(item, "type");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(title) || type != expectedType)
            throw new PlexException("Plex returned an unidentified catalog item.");
        var guids = new HashSet<string>(StringComparer.Ordinal);
        if (OptionalString(item, "guid") is { Length: > 0 } guid) guids.Add(guid);
        if (item.TryGetProperty("Guid", out var extraGuids))
            foreach (var extra in extraGuids.EnumerateArray())
                if (OptionalString(extra, "id") is { Length: > 0 } id) guids.Add(id);
        var files = new List<PlexMediaFile>();
        if (item.TryGetProperty("Media", out var media))
            foreach (var version in media.EnumerateArray())
            {
                var resolution = OptionalString(version, "videoResolution") ?? "";
                if (version.TryGetProperty("Part", out var parts))
                    foreach (var part in parts.EnumerateArray())
                        if (OptionalString(part, "file") is { Length: > 0 } path)
                            files.Add(new PlexMediaFile(path, resolution));
            }
        return new PlexLibraryItem(key, title, type, OptionalInt(item, "year"),
            OptionalString(item, "grandparentTitle"), OptionalInt(item, "parentIndex"), OptionalInt(item, "index"),
            guids.ToList().AsReadOnly(), files.AsReadOnly());
    }

    // ---- sharing ----

    public async Task<List<PlexSharedUser>> GetSharedUsersAsync(string machineId, CancellationToken ct)
    {
        var url = $"{TvBase}/api/servers/{machineId}/shared_servers";
        using var resp = await _http.SendAsync(Build(HttpMethod.Get, url), ct);
        resp.EnsureSuccessStatusCode();
        return ParseSharedUsers(await resp.Content.ReadAsStringAsync(ct));
    }

    public async Task InviteAsync(string machineId, string invitedEmailOrUser,
        IReadOnlyCollection<int> librarySectionIds, bool allowSync, CancellationToken ct)
    {
        var payload = new
        {
            server_id = machineId,
            shared_server = new
            {
                library_section_ids = librarySectionIds,
                invited_email = invitedEmailOrUser,
            },
            sharing_settings = new
            {
                allowSync = allowSync ? "1" : "0",
                allowCameraUpload = "0",
                allowChannels = "0",
            },
        };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var url = $"{TvBase}/api/servers/{machineId}/shared_servers";
        using var resp = await _http.SendAsync(Build(HttpMethod.Post, url, content), ct);
        await ThrowIfInviteFailed(resp, invitedEmailOrUser, ct);
    }

    public async Task RevokeAsync(string machineId, string sharedServerId, CancellationToken ct)
    {
        var url = $"{TvBase}/api/servers/{machineId}/shared_servers/{sharedServerId}";
        using var resp = await _http.SendAsync(Build(HttpMethod.Delete, url), ct);
        // A vanished share is effectively revoked (matches helpr's NotFound→success).
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new PlexException($"Revoke failed ({(int)resp.StatusCode}): {Trim(body)}");
        }
    }

    private static async Task ThrowIfInviteFailed(HttpResponseMessage resp, string user, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        var lower = body.ToLowerInvariant();
        if (resp.StatusCode == HttpStatusCode.UnprocessableEntity ||
            lower.Contains("already") || lower.Contains("sharing"))
            throw new PlexException($"'{user}' already has access or has already been invited.");
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new PlexException($"Plex user '{user}' not found. Check the username or email.");
        throw new PlexException($"Invite failed ({(int)resp.StatusCode}): {Trim(body)}");
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    // ---- pure parsers (unit-tested) ----

    public static List<PlexResource> ParseResources(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<PlexResource>>(json, Json) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Plex: failed to parse resources");
            return new();
        }
    }

    public static List<PlexLibrary> ParseLibraries(string json)
    {
        var result = new List<PlexLibrary>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc)) return result;
            if (!mc.TryGetProperty("Directory", out var dirs) || dirs.ValueKind != JsonValueKind.Array) return result;
            foreach (var d in dirs.EnumerateArray())
            {
                result.Add(ParseLibrary(d));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Plex: failed to parse libraries");
        }
        return result;
    }

    public static List<PlexSharedUser> ParseSharedUsers(string xml)
    {
        var result = new List<PlexSharedUser>();
        if (string.IsNullOrWhiteSpace(xml)) return result;
        try
        {
            var root = XDocument.Parse(xml).Root;
            if (root == null) return result;
            foreach (var ss in root.Elements("SharedServer"))
            {
                var user = new PlexSharedUser
                {
                    SharedServerId = (string?)ss.Attribute("id") ?? "",
                    Username = (string?)ss.Attribute("username") ?? "",
                    Email = (string?)ss.Attribute("email") ?? "",
                    UserId = (string?)ss.Attribute("userID") ?? "",
                    Accepted = ((string?)ss.Attribute("accepted") ?? "0") is "1" or "true",
                    AllowSync = ((string?)ss.Attribute("allowSync") ?? "0") is "1" or "true",
                };
                foreach (var sec in ss.Elements("Section"))
                {
                    user.Sections.Add(new PlexSharedSection
                    {
                        Id = (string?)sec.Attribute("id") ?? "",
                        Key = (string?)sec.Attribute("key") ?? "",
                        Title = (string?)sec.Attribute("title") ?? "",
                        Type = (string?)sec.Attribute("type") ?? "",
                        Shared = ((string?)sec.Attribute("shared") ?? "0") is "1" or "true",
                    });
                }
                result.Add(user);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Plex: failed to parse shared users");
        }
        return result;
    }

    /// <summary>Best-guess connection ordering (no probing): direct before relay,
    /// local before remote, https first. Used as a last-resort fallback and in tests;
    /// real selection goes through FindWorkingConnectionAsync which actually probes.</summary>
    public static string? PickConnectionUri(PlexResource server)
    {
        if (server.Connections.Count == 0) return null;
        return OrderConnections(server)
            .Select(c => c.Uri)
            .FirstOrDefault(u => !string.IsNullOrEmpty(u));
    }

    private static IEnumerable<PlexConnection> OrderConnections(PlexResource server) =>
        server.Connections
            .Where(c => !string.IsNullOrEmpty(c.Uri))
            .OrderBy(c => c.Relay ? 1 : 0)       // direct before relay (relay is bandwidth-capped)
            .ThenBy(c => c.Local ? 0 : 1)         // local before remote (fast on same LAN)
            .ThenBy(c => c.Protocol.Equals("https", StringComparison.OrdinalIgnoreCase) ? 0 : 1);

    /// <summary>Lightweight reachability check — hits /identity with a short timeout.</summary>
    public async Task<bool> ProbeAsync(string uri, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var resp = await _http.SendAsync(Build(HttpMethod.Get, $"{uri.TrimEnd('/')}/identity"), cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Probe a server's connections and return the first that actually responds.
    /// A server typically advertises local, remote-direct, and relay connections;
    /// any single one may be refused (no port forward, wrong LAN, offline), so we
    /// must test rather than guess. Probes concurrently and takes the first success.
    /// </summary>
    public async Task<string?> FindWorkingConnectionAsync(PlexResource server, CancellationToken ct)
    {
        var uris = OrderConnections(server).Select(c => c.Uri).Distinct().ToList();
        if (uris.Count == 0) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var probes = uris.ToDictionary(u => ProbeAsync(u, cts.Token));
        var pending = new List<Task<bool>>(probes.Keys);
        string? winner = null;
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            if (done.Result) { winner = probes[done]; break; }
        }
        cts.Cancel(); // stop the losers
        return winner;
    }

    public void Dispose()
    {
        _http.Dispose();
        _libraryHttp.Dispose();
    }
}
