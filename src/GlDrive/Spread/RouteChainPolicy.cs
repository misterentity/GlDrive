using GlDrive.Config;

namespace GlDrive.Spread;

/// <summary>
/// A user-fixed order in which a release should travel between sites, replacing the
/// measured-speed part of a transfer's score. The next hop in the chain scores highest;
/// skipping ahead scores less the further it skips; going backwards, or a site outside
/// the chain, earns nothing. It ranks routes and never forbids one, so a race still
/// completes when a hop is down.
/// </summary>
public sealed class RouteChainPolicy
{
    // Above the speed component's 3000 and the other factors combined, so the chain
    // decides the route whenever an in-chain hop is available.
    public const int MaxScore = 6000;

    private readonly Dictionary<string, int> _position;

    private RouteChainPolicy(Dictionary<string, int> position) => _position = position;

    /// <summary>Null when the chain is off or names fewer than two known sites.</summary>
    public static RouteChainPolicy? Create(SpreadConfig spread, IEnumerable<ServerConfig> servers)
    {
        if (!spread.UseRouteChain) return null;
        var known = servers.ToList();
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in spread.RouteChain)
        {
            var name = entry?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            var server = known.FirstOrDefault(s => s.Id.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? known.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (server != null) position.TryAdd(server.Id, position.Count);
        }
        return position.Count >= 2 ? new RouteChainPolicy(position) : null;
    }

    public int Score(string srcId, string dstId) =>
        _position.TryGetValue(srcId, out var from) && _position.TryGetValue(dstId, out var to) && to > from
            ? MaxScore / (to - from)
            : 0;

    public static List<string> Parse(string? text) =>
        (text ?? "").Split([',', '>', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.TrimEnd('-').Trim()).Where(part => part.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
