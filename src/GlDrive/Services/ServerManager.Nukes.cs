using System.IO;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Spread;
using Serilog;

namespace GlDrive.Services;

public partial class ServerManager
{
    private OwnNukeAlertTracker _nukes = null!;
    public event Action<OwnNukeAlert>? OwnNukeAlerted;

    private void InitializeNukes()
    {
        _nukes = new OwnNukeAlertTracker(Path.Combine(ConfigManager.AppDataPath, "nuke-observations.json"));
        // Only exact nuke evidence holds a race. Unreadable or full local bookkeeping
        // must never refuse every race and stop the active ones.
        if (_spreadManager != null) _spreadManager.IsReleaseNuked = (server, release) =>
            _nukes.IsNuked(server, release);
    }

    public IReadOnlyList<OwnNukeState> NukeStatus() => _nukes.Snapshot();
    public bool NukeStateAvailable => _nukes.StateAvailable;
    public bool NukePersistenceHealthy => _nukes.PersistenceHealthy;

    public void ObservePolledNuke(string serverId, ParsedNuke observation)
    {
        if (_disposed) return;
        var server = _config.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null) return;
        if (observation.ReleaseTruncated && _spreadManager != null)
        {
            var names = _spreadManager.History.Items.Select(h => h.ReleaseName)
                .Concat(_spreadManager.QueuedRaces.Select(q => q.ReleaseName))
                .Concat(_spreadManager.ActiveJobs.Select(j => j.ReleaseName));
            var resolved = NukeReleaseResolver.ResolveUnique(observation.Release, names);
            // A unique prefix names the alert but stays approximate, so it never holds a race:
            // SITE NUKES cuts Dir at ~50 chars, often inside the group, and another group's nuke
            // of the same episode shares that prefix with the release we raced.
            if (resolved != null) observation = observation with { Release = resolved };
        }
        var known = _nukes.IsNuked(serverId, observation.Release);
        var alert = _nukes.ObservePoll(serverId, server.Connection.Username, observation);
        if (!known && !observation.ReleaseTruncated && _nukes.IsNuked(serverId, observation.Release))
            Hooks.Publish(new("nuke", observation.Release, observation.Section, "", "Nuked"));
        _spreadManager?.RefreshNukeHolds();
        if (_nukes.IsNuked(serverId, observation.Release)) _wantedCoordinator.Invalidate(serverId, observation.Release);
        NotifyOwnNuke(alert);
    }

    private void ObserveNukeSitebot(SitebotEvent observation)
    {
        if (_disposed || observation.Kind is not (SitebotEventKind.Nuke or SitebotEventKind.Unnuke)) return;
        var current = _spreadManager?.SitebotFacts.TryGet(observation.ServerId, observation.Release)?.Latest;
        if (current?.ObservedAtUtc != observation.ObservedAtUtc || current.Kind != observation.Kind) return;
        var server = _config.Servers.FirstOrDefault(s => s.Id == observation.ServerId);
        if (server != null)
        {
            if (observation.Kind == SitebotEventKind.Nuke && !_nukes.IsNuked(observation.ServerId, observation.Release))
                Hooks.Publish(new("nuke", observation.Release, observation.Section, "", "Nuked"));
            NotifyOwnNuke(_nukes.ObserveSitebot(observation, server.Connection.Username));
            _spreadManager?.RefreshNukeHolds();
        }
    }

    private void NotifyOwnNuke(OwnNukeAlert? notice)
    {
        if (notice == null || !_config.Spread.OwnNukeAlertsEnabled) return;
        Log.Warning("Own-account nuke on {Server}: {Release} ({Multiplier}x): {Reason}",
            notice.ServerId, notice.Release, notice.Multiplier, notice.Reason);
        var listeners = OwnNukeAlerted;
        if (listeners == null) return;
        foreach (Action<OwnNukeAlert> listener in listeners.GetInvocationList())
            try { listener(notice); } catch (Exception ex) { Log.Warning(ex, "Own-nuke notification failed"); }
    }
}

internal static class NukeReleaseResolver
{
    internal static string? ResolveUnique(string truncated, IEnumerable<string> knownReleases)
    {
        var prefix = truncated.Trim().TrimEnd('.');
        if (prefix.Length < 12 || prefix.Any(c => char.IsControl(c) || c is '/' or '\\')) return null;
        var matches = knownReleases.Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
