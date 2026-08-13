using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GlDrive.Config;
using GlDrive.Services.Control;
using GlDrive.Services.Control.Endpoints;
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
///   GET  /                       — route index
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
    private readonly RouteTable _routes = new();
    private readonly StatusEndpoints _statusEndpoints;
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

        // Endpoints receive only what they need. See ControlApiBoundaryTests for why they
        // must never take ServerManager/AppConfig-reaching handles once readers land.
        _statusEndpoints = new StatusEndpoints(_config, _getSpread, _getConnectedServerIds, _routes);
        _statusEndpoints.Register(_routes);
        new SpreadEndpoints(_config, _getSpread).Register(_routes);
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

            // Routing dispatch: this used to be a hand-written `switch (method, path)` plus
            // two StartsWith prefix scans that sliced /races/{id} and /races/{id}/stop out
            // by string arithmetic. It is now RouteTable.TryMatch, with {id} bound by the
            // router. Both the loopback and token gates above still run first — the switch
            // (now the router) only ever sees an already-authenticated, already-loopback request.
            if (_routes.TryMatch(method, path, out var handler, out var parameters))
            {
                await handler!(ControlRequest.FromContext(ctx, path, parameters));
                return;
            }

            if (_routes.MethodNotAllowed(path))
            {
                await Respond(ctx, 405, new { error = "method not allowed", code = "method_not_allowed", path });
                return;
            }

            await Respond(ctx, 404, new { error = "not found", code = "not_found", path });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Control API request failed");
            try { await Respond(ctx, 500, new { error = ex.Message }); } catch { }
        }
    }

    /// <summary>
    /// Kept so ControlApiSecurityTests' reflection lookup (GetMethod("Sections",
    /// NonPublic|Instance) on ControlApi) still finds a target now that the handler body
    /// lives in StatusEndpoints. Delegates to the same projection — see
    /// StatusEndpoints.Sections() for the actual keys-only logic.
    /// </summary>
    private object Sections() => _statusEndpoints.Sections();

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
