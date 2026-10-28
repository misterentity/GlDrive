using GlDrive.Config;

namespace GlDrive.Spread;

/// <summary>Combines advisory routing information while preserving the admitted request's safety state.</summary>
internal static class RaceAnnounceMerge
{
    internal static PersistedRace Merge(PersistedRace current, PersistedRace incoming,
        Func<string, ServerConfig?> config, bool canChangeSection)
    {
        // Hints cannot re-admit a site removed by the caller's source rules,
        // metadata filters, or section eligibility checks.
        var hints = Hints(current).Where(h => current.ServerIds.Contains(h.ServerId, StringComparer.Ordinal))
            .Concat(Hints(incoming).Where(h => incoming.ServerIds.Contains(h.ServerId, StringComparer.Ordinal)))
            .Where(h => CanSource(config(h.ServerId)))
            .Select(h => h with { Path = SafePath(h.Path, current.ReleaseName) ? h.Path : null })
            .GroupBy(h => h.ServerId, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(h => SafePath(h.Path, current.ReleaseName))
                .ThenByDescending(h => ResolveSourcePath(config(h.ServerId)!, h, current.ReleaseName) != null).First())
            .Take(100).ToList();
        var selected = hints.OrderByDescending(h => SourceRank(config(h.ServerId)))
            .ThenByDescending(h => !string.IsNullOrWhiteSpace(h.Path)).FirstOrDefault();
        var sourceChanged = selected != null && (selected.ServerId != current.KnownSourceServerId
            || selected.Path != current.KnownSourcePath || selected.Section != current.Section);
        var ids = current.ServerIds.Concat(incoming.ServerIds).Concat(hints.Select(h => h.ServerId))
            .Distinct(StringComparer.Ordinal).ToList();
        var section = canChangeSection && sourceChanged && !current.HasOwnedPartial && !current.CleanupOnly
            ? selected!.Section : current.Section;
        var changed = !ids.SequenceEqual(current.ServerIds) || !hints.SequenceEqual(current.SourceHints ?? [])
            || selected?.ServerId != current.KnownSourceServerId || selected?.Path != current.KnownSourcePath
            || section != current.Section;
        if (!changed) return current;
        return current with
        {
            ServerIds = ids, SourceHints = hints, Section = section,
            KnownSourceServerId = selected?.ServerId, KnownSourcePath = selected?.Path
        };
    }

    internal static IEnumerable<RaceSourceHint> Hints(PersistedRace request)
    {
        if (request.KnownSourceServerId is { Length: > 0 } source &&
            !(request.SourceHints ?? []).Any(h => h.ServerId == source && h.Path == request.KnownSourcePath))
            yield return new(source, request.Section, request.KnownSourcePath);
        foreach (var hint in request.SourceHints ?? []) yield return hint;
    }

    internal static bool CanSource(ServerConfig? config) => config is { Enabled: true } && !config.SpreadSite.NeverSource;
    private static int SourceRank(ServerConfig? config) => !CanSource(config) ? 0 : config!.SpreadSite.DownloadOnly ? 2 : 1;

    internal static string? ResolveSourcePath(ServerConfig config, RaceSourceHint hint, string release)
    {
        if (string.IsNullOrWhiteSpace(release) || release.StartsWith('.') || release.Contains('/') || release.Contains('\\') || release.Any(char.IsControl)) return null;
        if (!CanSource(config)) return null;
        if (SafePath(hint.Path, release)) return hint.Path;
        var mapping = SectionMapper.Resolve(config.SpreadSite, hint.Section, release);
        var basePath = mapping?.Mapping?.Path;
        if (string.IsNullOrWhiteSpace(basePath))
        {
            var section = mapping?.RemoteSection ?? hint.Section;
            basePath = config.SpreadSite.Sections.FirstOrDefault(p => p.Key.Equals(section, StringComparison.OrdinalIgnoreCase)).Value;
        }
        // Dated trees require an actual announced/discovered path. Guessing today's
        // directory can silently point a queued or resumed release at the wrong day.
        var leaf = basePath?.TrimEnd('/').Split('/').LastOrDefault();
        if (leaf?.Equals("mp3", StringComparison.OrdinalIgnoreCase) == true || leaf?.Equals("0day", StringComparison.OrdinalIgnoreCase) == true) return null;
        var path = string.IsNullOrWhiteSpace(basePath) ? null : basePath.TrimEnd('/') + "/" + release;
        return SafePath(path, release) ? path : null;
    }

    private static bool SafePath(string? path, string release) => !string.IsNullOrWhiteSpace(path)
        && path.StartsWith('/') && path.Length <= 4096 && !path.Any(char.IsControl) && !path.Contains('\\')
        && !path.Split('/').Any(p => p is "." or "..")
        && path.TrimEnd('/').EndsWith("/" + release, StringComparison.OrdinalIgnoreCase);
}
