using System.Diagnostics;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Spread;

namespace GlDrive.Services.Operations;

/// <summary>Config-only predictions. This type has no operational service or transport dependency.</summary>
internal static class ReleasePrediction
{
    internal static AppConfig? Capture(AppConfig current)
    {
        try
        {
            if (current.Servers.Count > 64 || current.Spread.GlobalSkiplist.Count > 128 || current.Downloads.Profiles.Count > 32)
                return null;
            var sites = new List<ServerConfig>();
            var projectedEntries = current.Spread.GlobalSkiplist.Count;
            foreach (var server in current.Servers)
            {
                var site = server.SpreadSite;
                if (server.Id.Length > 256 || site.Skiplist.Count > 128 || site.SectionMappings.Count > 128 ||
                    (projectedEntries += site.Skiplist.Count + site.Sections.Count + site.Affils.Count + site.SectionMappings.Count + site.SectionMappings.Sum(m => m.TagRules.Count)) > 2048 ||
                    site.Sections.Count > 128 || site.Affils.Count > 128 || site.Affils.Any(v => v == null || v.Length > 256) ||
                    site.Sections.Any(p => p.Key.Length > 256 || p.Value?.Length > 1024) ||
                    site.SectionMappings.Any(m => m == null || m.TagRules.Count > 128 || m.IrcSection.Length > 256 ||
                        m.RemoteSection.Length > 256 || m.Path.Length > 1024 || m.TriggerRegex.Length > 2048 || !RulesBounded(m.TagRules)) ||
                    !RulesBounded(site.Skiplist)) return null;
                sites.Add(new ServerConfig { Id = server.Id, Enabled = server.Enabled, SpreadSite = new SiteSpreadConfig
                {
                    DownloadOnly = site.DownloadOnly, NeverSource = site.NeverSource,
                    Sections = new(site.Sections), Affils = site.Affils.ToList(), Skiplist = CopyRules(site.Skiplist),
                    MetadataFilter = new() { Enabled = site.MetadataFilter.Enabled },
                    SectionMappings = site.SectionMappings.Select(m => new SectionMapping
                    { IrcSection = m.IrcSection, RemoteSection = m.RemoteSection, Path = m.Path, TriggerRegex = m.TriggerRegex,
                        Enabled = m.Enabled, TagRules = CopyRules(m.TagRules) }).ToList()
                }});
            }
            if (!RulesBounded(current.Spread.GlobalSkiplist)) return null;
            foreach (var profile in current.Downloads.Profiles)
                if (profile.Id.Length > 256 || profile.Name.Length > 256 || profile.AllowedResolutions.Count > 16 ||
                    profile.CustomFormats.Count > 32 || profile.CustomFormats.Any(f => f == null || f.Name.Length > 256 || f.Pattern.Length > 2048)) return null;
            // Only the bounded policy fields are copied; credentials, executable hooks and provider settings are absent.
            return new AppConfig
            {
                Servers = sites,
                Spread = new() { AutoRaceOnNotification = current.Spread.AutoRaceOnNotification, GlobalSkiplist = CopyRules(current.Spread.GlobalSkiplist) },
                Downloads = new()
                {
                    AutoDownloadWishlist = current.Downloads.AutoDownloadWishlist,
                    BlockLowQualityWishlistSources = current.Downloads.BlockLowQualityWishlistSources,
                    ScoredQualityEnabled = current.Downloads.ScoredQualityEnabled,
                    WantedDecisionWindowEnabled = current.Downloads.WantedDecisionWindowEnabled,
                    Profiles = current.Downloads.Profiles.Select(p => JsonSerializer.Deserialize<ScoredQualityProfile>(JsonSerializer.Serialize(p))!).ToList()
                }
            };
        }
        catch { return null; } // Concurrent edits or malformed optional policy are unknown evidence.
    }

    private static bool RulesBounded(IReadOnlyList<SkiplistRule> rules) => rules.All(rule => rule != null &&
        rule.Pattern?.Length <= 2048 && (rule.Expression?.Length ?? 0) <= 2048 && (rule.Section?.Length ?? 0) <= 256);
    private static List<SkiplistRule> CopyRules(IEnumerable<SkiplistRule> rules) => rules.Select(rule => new SkiplistRule
    { Pattern = rule.Pattern, IsRegex = rule.IsRegex, Action = rule.Action, Scope = rule.Scope,
        MatchDirectories = rule.MatchDirectories, MatchFiles = rule.MatchFiles, Section = rule.Section, Expression = rule.Expression }).ToList();

    internal static IReadOnlyList<DossierPrediction> Evaluate(AppConfig? config, IReadOnlyList<WishlistItem> wishlist,
        bool wishlistPartial, string release, string? section, Action<string> partial, CancellationToken ct, Func<bool>? budgetExceeded = null)
    {
        if (config == null) { partial("config-snapshot-unavailable"); return [new("", "prediction", "unknown", "Configuration could not be copied within the diagnostic bounds.")]; }
        var result = new List<DossierPrediction>();
        result.Add(new("", "auto-race", config.Spread.AutoRaceOnNotification ? "enabled" : "disabled", "Current automatic race switch. Runtime admission and source availability are not evaluated."));
        var watch = Stopwatch.StartNew();
        bool OverBudget() => budgetExceeded?.Invoke() ?? watch.ElapsedMilliseconds > 3500;
        void CheckBudget()
        {
            ct.ThrowIfCancellationRequested();
            if (OverBudget()) throw new PredictionBudgetExceededException();
        }
        // Capture bounds unique policy entries. Global rules may be evaluated once
        // per site, so account for that repetition without starving normal policies.
        var siteWork = 2048 + config.Spread.GlobalSkiplist.Count * config.Servers.Count;
        var work = 2048;
        // A preview sees each pattern only a few times. Reuse one local cache for
        // shared site/global rules, without paying compiled-regex startup per site.
        var evaluator = new SkiplistEvaluator { CheckBudget = CheckBudget, CompileRegex = false };
        var parsed = SceneNameParser.Parse(release);
        foreach (var server in config.Servers)
        {
            ct.ThrowIfCancellationRequested();
            if (!server.Enabled) { result.Add(new(server.Id, "site-rules", "disabled", "Server is disabled in configuration.")); continue; }
            var site = server.SpreadSite;
            var cost = site.Skiplist.Count + config.Spread.GlobalSkiplist.Count + site.SectionMappings.Count + site.SectionMappings.Sum(m => m.TagRules.Count);
            if (OverBudget() || cost > siteWork)
            { partial("prediction-work-limit"); result.Add(new(server.Id, "site-rules", "unknown", "Rule evaluation exceeds the bounded preview budget; no rule list was truncated.")); continue; }
            siteWork -= cost;
            if (string.IsNullOrWhiteSpace(section)) { result.Add(new(server.Id, "site-rules", "unknown", "Supply an announce section to evaluate section-scoped rules and mappings.")); continue; }
            try
            {
                var mapping = SectionMapper.Resolve(site, section, release, CheckBudget);
                var effective = mapping?.RemoteSection ?? section;
                var action = evaluator.EvaluateTiered(release, true, false, effective, site.Skiplist,
                    mapping?.TagRules ?? [], config.Spread.GlobalSkiplist, site.Affils, parsed);
                var receive = CandidatePredicates.CanReceiveRelease(release, site.Affils, site.DownloadOnly,
                    destinationBlacklisted: false, destinationReachable: true);
                var reason = $"Tiered rule action {action}; section {effective}; source role {(site.NeverSource ? "disabled" : "eligible by config")}; " +
                    $"destination role {(receive ? "eligible by config" : "excluded by download-only/affiliate policy")}.";
                if (!SectionMapper.HasSectionFor(site, section)) reason += " No configured section route was found.";
                if (site.MetadataFilter.Enabled) reason += " Metadata filter is enabled and remains unevaluated (no provider request).";
                result.Add(new(server.Id, "site-rules", action == SkiplistAction.Deny ? "denied-by-config" : "conditional", reason));
            }
            catch (PredictionBudgetExceededException)
            { partial("prediction-work-limit"); result.Add(new(server.Id, "site-rules", "unknown", "Rule evaluation reached the preview time budget; no partial policy verdict was used.")); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { partial("prediction-policy-unavailable"); result.Add(new(server.Id, "site-rules", "unknown", "A policy could not be evaluated.")); }
        }
        if (wishlistPartial)
        {
            partial("wishlist-snapshot-incomplete");
            result.Add(new("", "wishlist", "unknown", "Wishlist evidence is incomplete; missing claims must not become a predicted download."));
            return result;
        }
        // The isolated store reuses acquisition rules without touching shared dedupe, claims, timers or persistence.
        var detached = new WishlistStore(() => JsonSerializer.Serialize(wishlist, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            _ => throw new InvalidOperationException("A diagnostic preview cannot save"));
        detached.Load();
        foreach (var item in wishlist)
        {
            ct.ThrowIfCancellationRequested();
            if (watch.ElapsedMilliseconds > 3500) { partial("prediction-work-limit"); break; }
            try
            {
                if (!WishlistAcquisition.MatchesTitle(item, release)) continue;
                if (!config.Downloads.AutoDownloadWishlist || item.Dead || item.Status == WishlistStatus.Paused)
                { result.Add(new("", "wishlist", "inactive", item.Id + ": automatic wishlist acquisition is disabled or this item is inactive.")); continue; }
                var tracked = config.Downloads.ScoredQualityEnabled || item.MonitorMode != SeriesMonitorMode.Legacy;
                if (tracked)
                {
                    var profile = WishlistAcquisition.ResolveProfile(item, config.Downloads);
                    // Scoring also examines claims belonging to another entry for the same title.
                    // Charge every parse even when there are no custom regex formats.
                    var claims = wishlist.Where(other => WishlistAcquisition.SameTitle(item, other))
                        .Sum(other => other.BestOwned.Count + other.PendingGrabs.Count + other.GrabbedReleases.Count);
                    var scoreCost = (1 + (profile?.CustomFormats.Count ?? 0)) * (1 + claims);
                    if (scoreCost > work) { partial("prediction-work-limit"); result.Add(new("", "wishlist", "unknown", item.Id + ": scoring exceeds the preview budget.")); continue; }
                    work -= scoreCost;
                    var decision = detached.EvaluateScoredRelease(item.Id, release, config.Downloads);
                    result.Add(new("", "wishlist", decision.Accepted ? "conditional" : "denied-by-config-or-history", item.Id + ": " + decision.Reason));
                }
                else
                {
                    var accepted = item.Status == WishlistStatus.Watching && !item.GrabbedReleases.Contains(release, StringComparer.OrdinalIgnoreCase) &&
                        WishlistSourceFloor.Allows(item, release, config.Downloads) && (item.Type == MediaType.Movie
                            ? SceneNameParser.MatchesMovie(release, item.Title, item.Year, item.Quality)
                            : SceneNameParser.MatchesTvEpisode(release, item.Title, item.Quality));
                    result.Add(new("", "wishlist", accepted ? "conditional" : "denied-by-config-or-history", item.Id +
                        (accepted ? ": title, quality and source policy match; source readiness and active dedupe remain unverified." : ": current item status, previous grab, quality or source floor excludes this candidate.")));
                }
            }
            catch { partial("wishlist-policy-unavailable"); result.Add(new("", "wishlist", "unknown", "A wishlist policy could not be evaluated.")); }
        }
        return result;
    }

    private sealed class PredictionBudgetExceededException : Exception;
}
