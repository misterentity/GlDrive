using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GlDrive.Services.Control;

/// <summary>
/// Maps (method, path pattern) to a handler. Patterns use {name} for a captured segment:
/// "/irc/{serverId}/messages". Literal segments beat parameter segments, so "/races/active"
/// is reachable even when "/races/{id}" is registered.
///
/// Deliberately tiny: the surface is loopback-only and every route is registered in-process,
/// so there is nothing to gain from a general routing engine.
/// </summary>
public sealed class RouteTable
{
    private sealed record Route(string Method, string Pattern, string[] Segments,
                                Func<ControlRequest, Task> Handler, int LiteralCount);

    private readonly List<Route> _routes = [];

    public IReadOnlyList<(string Method, string Pattern)> Routes =>
        _routes.Select(r => (r.Method, r.Pattern)).ToList();

    public void Map(string method, string pattern, Func<ControlRequest, Task> handler)
    {
        var normalisedMethod = method.ToUpperInvariant();
        if (_routes.Any(r => r.Method == normalisedMethod
                             && string.Equals(r.Pattern, pattern, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Duplicate route: {normalisedMethod} {pattern}");

        var segments = Split(pattern);
        var literals = segments.Count(s => !IsParameter(s));
        _routes.Add(new Route(normalisedMethod, pattern, segments, handler, literals));
    }

    public bool TryMatch(string method, string path,
        out Func<ControlRequest, Task>? handler,
        out IReadOnlyDictionary<string, string> parameters)
    {
        handler = null;
        parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        var pathSegments = Split(path);
        var normalisedMethod = method.ToUpperInvariant();

        // Most literal segments first, so "/races/active" beats "/races/{id}".
        foreach (var route in _routes.Where(r => r.Method == normalisedMethod)
                                     .OrderByDescending(r => r.LiteralCount))
        {
            if (route.Segments.Length != pathSegments.Length) continue;
            if (!TryBind(route.Segments, pathSegments, out var bound)) continue;

            handler = route.Handler;
            parameters = bound;
            return true;
        }

        return false;
    }

    /// <summary>True when the path matches a registered route under some other verb.</summary>
    public bool MethodNotAllowed(string path)
    {
        var pathSegments = Split(path);
        return _routes.Any(r => r.Segments.Length == pathSegments.Length
                                && TryBind(r.Segments, pathSegments, out _));
    }

    private static bool TryBind(string[] pattern, string[] actual,
        out Dictionary<string, string> bound)
    {
        bound = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < pattern.Length; i++)
        {
            if (IsParameter(pattern[i]))
            {
                if (actual[i].Length == 0) return false;
                bound[pattern[i][1..^1]] = actual[i];
                continue;
            }
            if (!string.Equals(pattern[i], actual[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static bool IsParameter(string segment) =>
        segment.Length >= 2 && segment[0] == '{' && segment[^1] == '}';

    /// <summary>
    /// Splits on '/' keeping empty inner segments, so "/irc/" is two segments and cannot
    /// bind a parameter — a trailing slash must not smuggle an empty id through.
    /// </summary>
    private static string[] Split(string path)
    {
        var trimmed = path.StartsWith('/') ? path[1..] : path;
        return trimmed.Length == 0 ? [] : trimmed.Split('/');
    }
}
