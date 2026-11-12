using GlDrive.Config;
using GlDrive.Downloads;

namespace GlDrive.Spread;

/// <summary>Settings checked for each dispatch, including changes made during a race.</summary>
internal static class LiveSpreadPolicy
{
    internal static bool IsAffiliated(ServerConfig server, string release) =>
        !CandidatePredicates.CanReceiveRelease(release, server.SpreadSite.Affils,
            downloadOnly: false, destinationBlacklisted: false, destinationReachable: true);

    internal static SkiplistAction FileAction(SkiplistEvaluator evaluator, ServerConfig server,
        SpreadConfig spread, string section, string release, string file, bool requestFill = false)
    {
        var site = server.SpreadSite;
        var mapping = SectionMapper.Resolve(site, section, release);
        var parsed = SceneNameParser.Parse(release);
        // A request fill carries the release the site asked for; only file rules apply.
        var directoryAction = requestFill ? SkiplistAction.Allow : evaluator.EvaluateTiered(release, true, true, section,
            site.Skiplist, mapping?.TagRules ?? [], spread.GlobalSkiplist, site.Affils, parsed);
        if (directoryAction == SkiplistAction.Deny) return directoryAction;
        return evaluator.EvaluateTiered(System.IO.Path.GetFileName(file), false, true, section,
            site.Skiplist, mapping?.TagRules ?? [], spread.GlobalSkiplist, site.Affils, parsed);
    }
}
