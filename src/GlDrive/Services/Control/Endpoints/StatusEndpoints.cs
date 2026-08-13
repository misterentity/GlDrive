using System;
using System.Collections.Generic;
using System.Linq;
using GlDrive.Config;
using GlDrive.Spread;

namespace GlDrive.Services.Control.Endpoints;

public sealed class StatusEndpoints : IControlEndpoint
{
    private readonly AppConfig _config;
    private readonly Func<SpreadManager?> _getSpread;
    private readonly Func<IReadOnlyList<string>> _getConnectedServerIds;
    private readonly RouteTable _routes;

    public StatusEndpoints(AppConfig config, Func<SpreadManager?> getSpread,
        Func<IReadOnlyList<string>> getConnectedServerIds, RouteTable routes)
    {
        _config = config;
        _getSpread = getSpread;
        _getConnectedServerIds = getConnectedServerIds;
        _routes = routes;
    }

    public void Register(RouteTable routes)
    {
        routes.Map("GET", "/", r => r.RespondAsync(200, Index()));
        routes.Map("GET", "/status", r => r.RespondAsync(200, Status()));
        routes.Map("GET", "/sections", r => r.RespondAsync(200, Sections()));
    }

    private object Index() => new
    {
        version = typeof(ControlApi).Assembly.GetName().Version?.ToString(),
        routes = _routes.Routes.Select(r => $"{r.Method} {r.Pattern}").OrderBy(s => s)
    };

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

    // internal, not private: ControlApi keeps a private Sections() that delegates here so
    // ControlApiSecurityTests' reflection lookup (GetMethod("Sections", NonPublic|Instance)
    // on ControlApi) still finds a target. See ControlApi.Sections().
    internal object Sections()
    {
        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _config.Servers)
            foreach (var k in s.SpreadSite.Sections.Keys) keys.Add(k);

        // Section KEYS only — the values are each site's real remote paths. See v3.10.58.
        return new
        {
            sections = keys,
            perServer = _config.Servers.ToDictionary(
                s => s.Name,
                s => (IEnumerable<string>)s.SpreadSite.Sections.Keys
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList())
        };
    }
}
