using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Irc;
using GlDrive.Spread;

namespace GlDrive.AiAgent;

public sealed record ImpactSummary(int Samples, int Changed, int Lost, int Implausible,
    bool CompleteEvidence, string? RejectionReason);
public sealed record ReplayRelease(string ServerId, string Section, string Release, string Source, bool Protected);
public sealed record ReplayMessage(string ServerId, string Channel, string Nick, string Text);
public sealed record ReplayEvidence(IReadOnlyList<ReplayRelease> Releases, IReadOnlyList<ReplayMessage> Messages,
    bool Complete = true);

/// <summary>Local counterfactual evidence. Only counts leave this component; channel text never reaches a prompt.</summary>
public static class ImpactSimulator
{
    public static bool RequiresReplay(string category) => category is AgentCategories.Skiplist
        or AgentCategories.SectionMapping or AgentCategories.ExcludedCategories or AgentCategories.AnnounceRule
        or AgentCategories.RequestFiller;

    public static ImpactSummary Evaluate(AgentChange change, AppConfig before, AppConfig after, ReplayEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        if (!evidence.Complete) return new(0, 0, 0, 0, false, "impact-evidence-unavailable");
        var id = change.Target.Split('/').ElementAtOrDefault(2);
        var oldSite = before.Servers.SingleOrDefault(s => s.Id == id);
        var newSite = after.Servers.SingleOrDefault(s => s.Id == id);
        if (oldSite == null || newSite == null) return new(0, 0, 0, 0, true, "impact-target-unavailable");
        if (change.Category == AgentCategories.RequestFiller)
            return new(0, 0, 0, 0, true, "impact-request-replay-unsupported");
        if (change.Category == AgentCategories.AnnounceRule)
            return AnnounceImpact(oldSite, newSite, before.Spread.AutoRaceOnNotification,
                evidence.Messages.Where(m => m.ServerId == id).ToArray(),
                evidence.Releases.Select(r => Identity(r.Section, r.Release)).ToHashSet(),
                evidence.Releases.Where(r => r.ServerId == id && r.Source == "announce")
                    .Select(r => Identity(r.Section, r.Release)).ToHashSet(), cancellationToken);
        if (change.Category == AgentCategories.SectionMapping && newSite.SpreadSite.DownloadOnly)
            return new(0, 0, 0, 0, true, "impact-no-destination-role");

        var samples = evidence.Releases.Where(r => r.ServerId == id)
            .DistinctBy(r => (r.Section, r.Release, r.Source)).ToArray();
        var changed = 0;
        var lost = 0;
        var evaluator = new SkiplistEvaluator();
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (change.Category == AgentCategories.ExcludedCategories)
            {
                // Exclusions stop listing categories, while IRC-fed wishlist matching ignores them.
                // Without historical delivery-source identity, a wanted match is conservative
                // evidence against excluding its only observed category, never against a race.
                var wasExcluded = oldSite.Notifications.ExcludedCategories.Contains(sample.Section, StringComparer.OrdinalIgnoreCase);
                var isExcluded = newSite.Notifications.ExcludedCategories.Contains(sample.Section, StringComparer.OrdinalIgnoreCase);
                if (wasExcluded != isExcluded) changed++;
                if (!wasExcluded && isExcluded && sample.Source == "wishlist" && sample.Protected) lost++;
                continue;
            }
            var oldRoute = ResolveRoute(oldSite.SpreadSite, sample.Section, sample.Release);
            var newRoute = ResolveRoute(newSite.SpreadSite, sample.Section, sample.Release);
            var parsed = SceneNameParser.Parse(sample.Release);
            var oldAllow = evaluator.EvaluateTiered(sample.Release, true, false, oldRoute?.RemoteSection ?? sample.Section,
                oldSite.SpreadSite.Skiplist, oldRoute?.TagRules ?? [], before.Spread.GlobalSkiplist,
                oldSite.SpreadSite.Affils, parsed) == SkiplistAction.Allow;
            var newAllow = evaluator.EvaluateTiered(sample.Release, true, false, newRoute?.RemoteSection ?? sample.Section,
                newSite.SpreadSite.Skiplist, newRoute?.TagRules ?? [], after.Spread.GlobalSkiplist,
                newSite.SpreadSite.Affils, parsed) == SkiplistAction.Allow;
            var routeChanged = change.Category == AgentCategories.SectionMapping
                && (oldRoute?.RemoteSection != newRoute?.RemoteSection || oldRoute?.Path != newRoute?.Path);
            if (oldAllow != newAllow || routeChanged) changed++;
            if (sample.Protected && oldAllow && !newAllow) lost++;
            if (routeChanged && oldRoute != null && newRoute == null) lost++;
        }
        return new(samples.Length, changed, lost, 0, true,
            samples.Length == 0 ? "impact-no-samples" : lost > 0 ? "impact-destructive" : changed == 0 ? "impact-no-effect" : null);
    }

    private sealed record Route(string RemoteSection, string Path, IReadOnlyList<SkiplistRule> TagRules);

    // Same explicit-path, exact, normalized, then substring order as SpreadJob destinations.
    private static Route? ResolveRoute(SiteSpreadConfig site, string section, string release)
    {
        var mapped = SectionMapper.Resolve(site, section, release);
        if (mapped is { } match)
        {
            var path = match.Mapping is { Path.Length: > 0 } mapping ? mapping.Path
                : site.Sections.FirstOrDefault(p => p.Key.Equals(match.RemoteSection, StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrEmpty(path)) return new(match.RemoteSection, path, match.TagRules);
        }
        static string Normalize(string value) => value.ToLowerInvariant().Replace("-", "").Replace("_", "");
        var route = site.Sections.FirstOrDefault(p => p.Key.Equals(section, StringComparison.OrdinalIgnoreCase));
        var normalized = Normalize(section);
        if (string.IsNullOrEmpty(route.Value)) route = site.Sections.FirstOrDefault(p => Normalize(p.Key) == normalized);
        if (string.IsNullOrEmpty(route.Value)) route = site.Sections.FirstOrDefault(p =>
            Normalize(p.Key).Contains(normalized) || normalized.Contains(Normalize(p.Key)));
        return string.IsNullOrEmpty(route.Value) ? null : new(route.Key, route.Value, []);
    }

    private static string Identity(string section, string release) => section.ToUpperInvariant() + "\0" + release;

    private static ImpactSummary AnnounceImpact(ServerConfig before, ServerConfig after, bool defaultAutoRace,
        IReadOnlyList<ReplayMessage> samples, HashSet<string> knownReleases, HashSet<string> acceptedLive,
        CancellationToken cancellationToken)
    {
        var oldMatcher = new AnnounceMatcher(before.Irc.AnnounceRules, defaultAutoRace, cancellationToken);
        var newMatcher = new AnnounceMatcher(after.Irc.AnnounceRules, defaultAutoRace, cancellationToken);
        var changed = 0;
        var lost = 0;
        var implausible = 0;
        var relevant = 0;
        var unverified = new HashSet<string>();
        var gained = new HashSet<string>();
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = before.Irc.Channels.FirstOrDefault(c => c.Name.Equals(sample.Channel, StringComparison.OrdinalIgnoreCase));
            if (channel == null) continue;
            // Text logs lack userhost identity. Do not invent trust when a configured hostmask is required.
            if (channel.Sitebot is { Enabled: true } trust && !string.IsNullOrWhiteSpace(trust.BotNick))
            {
                if (!string.IsNullOrWhiteSpace(trust.UserHostPattern)) continue;
                if (!SitebotEventParser.IsTrusted(trust, sample.Nick, null)) continue;
                if (SitebotEventParser.IsNonNewMessage(sample.Text, trust.Templates)) continue;
            }
            relevant++;
            var oldMatches = oldMatcher.Match(sample.Channel, sample.Text, cancellationToken);
            var newMatches = newMatcher.Match(sample.Channel, sample.Text, cancellationToken);
            // Both plausible results may be null on chat; count new bad captures BEFORE that fast path.
            if (newMatches.Any(m => !m.Plausible && !oldMatches.Any(old => !old.Plausible
                    && old.Release == m.Release && old.Section == m.Section))) implausible++;
            var old = oldMatches.FirstOrDefault(m => m.Plausible);
            var next = newMatches.FirstOrDefault(m => m.Plausible);
            if (old == next) continue;
            changed++;
            if (old != null && acceptedLive.Contains(Identity(old.Section, old.Release))
                && (next == null || Identity(old.Section, old.Release) != Identity(next.Section, next.Release))) lost++;
            if (next != null && (old == null || Identity(old.Section, old.Release) != Identity(next.Section, next.Release)))
            {
                // Capitalized prose can pass the listener's permissive plausibility check.
                // A new match needs independent recorded release identity, not just regex success.
                var identity = Identity(next.Section, next.Release);
                if (knownReleases.Contains(identity)) gained.Add(identity);
                else unverified.Add(identity);
            }
        }
        return new(relevant, changed, lost, implausible, true, relevant == 0 ? "impact-no-samples"
            : lost > 0 ? "impact-destructive" : implausible > 0 ? "impact-too-broad"
            : unverified.Count > gained.Count ? "impact-unverified-announces"
            : changed == 0 ? "impact-no-effect"
            : gained.Count + unverified.Count > 0 && gained.Count < 3 ? "impact-insufficient-new-matches" : null);
    }

    public static ReplayEvidence Read(string appDataRoot, AppConfig config, DateTime? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var from = now.AddDays(-Math.Clamp(config.Agent.WindowDays, 7, 30));
        var reader = new LogDigester(Path.Combine(appDataRoot, "ai-data"));
        var releases = new List<ReplayRelease>();
        var messages = new List<ReplayMessage>();
        var complete = true;
        bool InWindow(TelemetryEnvelope row)
        {
            if (!OutcomeEvaluator.TryUtc(row.Ts, out var at)) { complete = false; return false; }
            return at >= from && at <= now;
        }
        try
        {
            foreach (var row in reader.ReadStream<MatchedAnnounceEvent>("matched-announces", from.ToLocalTime(), now.ToLocalTime(), cancellationToken).Where(InWindow))
                releases.Add(new(row.ServerId, row.Section, row.Release, "announce", false));
            foreach (var row in reader.ReadStream<RaceOutcomeEvent>("races", from.ToLocalTime(), now.ToLocalTime(), cancellationToken).Where(InWindow))
                foreach (var participant in row.Participants)
                    releases.Add(new(participant.ServerId, row.Section, row.Release, "race", row.Result == "complete" && participant.Bytes > 0));
            foreach (var row in reader.ReadStream<WishlistAttemptEvent>("wishlist-attempts", from.ToLocalTime(), now.ToLocalTime(), cancellationToken).Where(InWindow))
                releases.Add(new(row.ServerId, row.Section, row.Release, "wishlist", row.Matched));
            foreach (var row in reader.ReadStream<AnnounceNoMatchEvent>("announces-nomatch", from.ToLocalTime(), now.ToLocalTime(), cancellationToken).Where(InWindow))
                messages.Add(new(row.ServerId, row.Channel, row.BotNick, row.Message));
            // Explicit date range: the scrollback reader intentionally covers only two days.
            foreach (var site in config.Servers)
            {
                if (site.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || site.Id.Contains(Path.DirectorySeparatorChar)) continue;
                for (var day = from.ToLocalTime().Date; day <= now.ToLocalTime().Date; day = day.AddDays(1))
                {
                    var path = Path.Combine(appDataRoot, "irc-logs", $"{site.Id}-{day:yyyy-MM-dd}.log");
                    if (!File.Exists(path)) continue;
                    foreach (var line in ReadSharedLines(path))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (!IrcLogStore.TryParseLine(line, day, out var parsed)) { complete = false; continue; }
                        if (parsed.Timestamp.ToUniversalTime() >= from && parsed.Timestamp.ToUniversalTime() <= now)
                            messages.Add(new(site.Id, parsed.Channel, parsed.Nick, parsed.Text));
                    }
                }
            }
            return new(releases, messages, complete && reader.ParseSkips == 0);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(releases, messages, false); }
    }

    internal static IEnumerable<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }
}
