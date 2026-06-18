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
public sealed class PlexClient : IDisposable
{
    private const string TvBase = "https://plex.tv";
    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _product;
    private readonly string _version;

    public string? Token { get; set; }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public PlexClient(string clientId, string product, string version, string? token = null)
    {
        _clientId = clientId;
        _product = product;
        _version = version;
        Token = token;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
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
                result.Add(new PlexLibrary
                {
                    Key = d.TryGetProperty("key", out var k) ? (k.GetString() ?? "") : "",
                    Title = d.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "",
                    Type = d.TryGetProperty("type", out var ty) ? (ty.GetString() ?? "") : "",
                });
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

    /// <summary>Pick the best reachable connection: prefer remote + https + non-relay,
    /// fall back through relay, then any local. Mirrors PlexAPI's connection ordering.</summary>
    public static string? PickConnectionUri(PlexResource server)
    {
        if (server.Connections.Count == 0) return null;
        return server.Connections
            .OrderBy(c => c.Relay ? 1 : 0)
            .ThenBy(c => c.Local ? 1 : 0)
            .ThenBy(c => c.Protocol.Equals("https", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(c => c.Uri)
            .FirstOrDefault(u => !string.IsNullOrEmpty(u));
    }

    public void Dispose() => _http.Dispose();
}
