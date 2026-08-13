using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GlDrive.Config;
using GlDrive.Spread;
using Serilog;

namespace GlDrive.Services;

/// <summary>
/// Loopback-only HTTP control surface for GlDrive.
///
/// Motivation: the only way to start a race or read a race's internals was the WPF
/// Dashboard, and driving the tray icon through UI Automation is unreliable — it breaks
/// whenever the notification-area flyout changes state. This gives scripts a stable way in.
///
/// Security, deliberately narrow and non-negotiable:
///   * binds ONLY to http://127.0.0.1:{port}/ — never a wildcard or LAN address;
///   * every request must carry `Authorization: Bearer &lt;token&gt;`, compared in fixed time;
///   * any request whose RemoteEndPoint is not a loopback address is refused even with a
///     valid token (defence in depth against a reverse proxy or a rebinding attack);
///   * disabled by default; the token is generated on first enable and never logged.
///
/// Endpoints
///   GET  /status                 — version, servers, connection state, active race count
///   GET  /sections               — section keys usable as the `section` field of POST /race
///   GET  /races                  — active races (summary)
///   GET  /races/{id}             — one race, full detail (files, dests, failures)
///   GET  /history?limit=N        — recent finished races
///   POST /races  {section,release} — start a race; returns the new race's id
///   POST /races/{id}/stop        — stop a race
/// </summary>
public sealed class ControlApi : IDisposable
{
    private readonly AppConfig _config;
    private readonly Func<SpreadManager?> _getSpread;
    private readonly Func<IReadOnlyList<string>> _getConnectedServerIds;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public ControlApi(AppConfig config, Func<SpreadManager?> getSpread,
        Func<IReadOnlyList<string>> getConnectedServerIds)
    {
        _config = config;
        _getSpread = getSpread;
        _getConnectedServerIds = getConnectedServerIds;
    }

    /// <summary>Generates and persists a token the first time the API is switched on.</summary>
    public static bool EnsureToken(AppConfig config)
    {
        if (!config.ControlApi.Enabled || !string.IsNullOrWhiteSpace(config.ControlApi.Token))
            return false;
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        config.ControlApi.Token = Convert.ToHexString(bytes).ToLowerInvariant();
        return true; // caller persists — never logged
    }

    public void Start()
    {
        if (!_config.ControlApi.Enabled) return;
        if (string.IsNullOrWhiteSpace(_config.ControlApi.Token))
        {
            Log.Warning("Control API enabled but no token configured — refusing to listen");
            return;
        }

        var prefix = $"http://127.0.0.1:{_config.ControlApi.Port}/";
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            _listener.Start();
        }
        catch (Exception ex)
        {
            // Most often HTTP.SYS refusing the namespace reservation, or the port is taken.
            Log.Error(ex, "Control API failed to bind {Prefix} — control surface unavailable", prefix);
            _listener = null;
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoop(_cts.Token));
        Log.Information("Control API listening on {Prefix} (loopback only, token required)", prefix);
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { Log.Debug(ex, "Control API accept failed"); continue; }

            _ = Task.Run(() => Handle(ctx), ct);
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            // Defence in depth: the prefix is already loopback-only, but never serve a
            // request that somehow arrived from off-box.
            var remote = ctx.Request.RemoteEndPoint?.Address;
            if (remote == null || !IPAddress.IsLoopback(remote))
            {
                await Respond(ctx, 403, new { error = "loopback only" });
                return;
            }

            var auth = ctx.Request.Headers["Authorization"] ?? "";
            const string scheme = "Bearer ";
            var presented = auth.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
                ? auth[scheme.Length..].Trim() : "";
            if (!FixedTimeEquals(presented, _config.ControlApi.Token))
            {
                await Respond(ctx, 401, new { error = "unauthorized" });
                return;
            }

            var path = (ctx.Request.Url?.AbsolutePath ?? "/").TrimEnd('/');
            if (path.Length == 0) path = "/";
            var method = ctx.Request.HttpMethod.ToUpperInvariant();

            switch (method, path)
            {
                case ("GET", "/status"):   await Respond(ctx, 200, Status()); return;
                case ("GET", "/sections"): await Respond(ctx, 200, Sections()); return;
                case ("GET", "/races"):    await Respond(ctx, 200, Races()); return;
                case ("POST", "/races"):   await StartRace(ctx); return;
                case ("GET", "/history"):  await Respond(ctx, 200, HistoryList(ctx)); return;
            }

            if (method == "GET" && path.StartsWith("/races/", StringComparison.Ordinal))
            {
                var id = path["/races/".Length..];
                var job = _getSpread()?.ActiveJobs.FirstOrDefault(j => j.Id == id);
                if (job == null) { await Respond(ctx, 404, new { error = "no such active race", id }); return; }
                await Respond(ctx, 200, job.GetDetail());
                return;
            }

            if (method == "POST" && path.StartsWith("/races/", StringComparison.Ordinal)
                && path.EndsWith("/stop", StringComparison.Ordinal))
            {
                var id = path["/races/".Length..^"/stop".Length];
                var spread = _getSpread();
                if (spread == null) { await Respond(ctx, 503, new { error = "spread engine unavailable" }); return; }
                spread.StopJob(id);
                await Respond(ctx, 200, new { stopped = id });
                return;
            }

            await Respond(ctx, 404, new { error = "not found", path });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Control API request failed");
            try { await Respond(ctx, 500, new { error = ex.Message }); } catch { }
        }
    }

    private object Status()
    {
        var spread = _getSpread();
        var connected = _getConnectedServerIds().ToHashSet(StringComparer.Ordinal);
        return new
        {
            version = typeof(ControlApi).Assembly.GetName().Version?.ToString(),
            activeRaces = spread?.ActiveJobs.Count ?? 0,
            maxConcurrentRaces = _config.Spread.MaxConcurrentRaces,
            servers = _config.Servers.Select(s => new
            {
                id = s.Id,
                name = s.Name,
                connected = connected.Contains(s.Id),
                loginCap = s.Pool.LoginCap,
                loginHeadroom = s.Pool.LoginHeadroom,
                uploadSlots = s.SpreadSite.MaxUploadSlots,
                downloadSlots = s.SpreadSite.MaxDownloadSlots,
                sections = s.SpreadSite.Sections.Count
            })
        };
    }

    private object Sections()
    {
        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _config.Servers)
            foreach (var k in s.SpreadSite.Sections.Keys) keys.Add(k);
        // Section KEYS only. The values of SpreadSite.Sections are the real remote paths on
        // each site (/incoming/x265, request dirs, staff-only trees); the documented purpose
        // of this endpoint is to tell a caller what it may put in POST /races {"section"},
        // and that needs the keys alone. Returning the map handed out each site's directory
        // layout to anything holding the token.
        return new
        {
            sections = keys,
            perServer = _config.Servers.ToDictionary(
                s => s.Name,
                s => (IEnumerable<string>)s.SpreadSite.Sections.Keys
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList())
        };
    }

    private object Races()
    {
        var spread = _getSpread();
        if (spread == null) return new { races = Array.Empty<object>() };
        return new
        {
            races = spread.ActiveJobs.Select(j => new
            {
                id = j.Id,
                release = j.ReleaseName,
                section = j.Section,
                state = j.State.ToString(),
                score = j.Score,
                startedAt = j.StartedAt,
                isAutoRace = j.IsAutoRace,
                sites = j.Sites.Values.Select(s => new { s.ServerName, s.FilesOwned, s.FilesTotal, s.IsSource })
            })
        };
    }

    private object HistoryList(HttpListenerContext ctx)
    {
        var limitRaw = ctx.Request.QueryString["limit"];
        var limit = int.TryParse(limitRaw, out var n) ? Math.Clamp(n, 1, 500) : 25;
        var spread = _getSpread();
        if (spread == null) return new { history = Array.Empty<object>() };
        return new { history = spread.History.Items.Take(limit) };
    }

    private async Task StartRace(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        string? section, release;
        try
        {
            var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            section = doc.RootElement.TryGetProperty("section", out var s) ? s.GetString() : null;
            release = doc.RootElement.TryGetProperty("release", out var r) ? r.GetString() : null;
        }
        catch (JsonException ex)
        {
            await Respond(ctx, 400, new { error = "invalid JSON body", detail = ex.Message });
            return;
        }

        if (string.IsNullOrWhiteSpace(section) || string.IsNullOrWhiteSpace(release))
        {
            await Respond(ctx, 400, new { error = "both 'section' and 'release' are required" });
            return;
        }

        var spread = _getSpread();
        if (spread == null) { await Respond(ctx, 503, new { error = "spread engine unavailable" }); return; }

        // Same participant rule the Dashboard uses: every connected server that has sections.
        var connected = spread.GetConnectedServerIds().ToHashSet(StringComparer.Ordinal);
        var serverIds = _config.Servers
            .Where(s => s.Enabled && connected.Contains(s.Id) && s.SpreadSite.Sections.Count > 0)
            .Select(s => s.Id).ToList();

        if (serverIds.Count < 2)
        {
            await Respond(ctx, 409, new
            {
                error = "need 2+ connected servers with sections configured",
                connected = _config.Servers.Where(s => connected.Contains(s.Id)).Select(s => s.Name)
            });
            return;
        }

        try
        {
            var job = spread.StartRace(section!, release!, serverIds, SpreadMode.Race);
            if (job == null) { await Respond(ctx, 409, new { error = "race not started (queued or rejected)" }); return; }
            Log.Information("Control API started race {Id}: [{Section}] {Release}", job.Id, section, release);
            await Respond(ctx, 202, new { id = job.Id, release = job.ReleaseName, section = job.Section });
        }
        catch (Exception ex)
        {
            await Respond(ctx, 500, new { error = ex.Message });
        }
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        return x.Length == y.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(x, y);
    }

    private static async Task Respond(HttpListenerContext ctx, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts?.Dispose();
    }
}
