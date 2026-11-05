using GlDrive.Config;
using Serilog;

namespace GlDrive.Spread;

/// <summary>
/// The server dialog's editable values that AI Setup can change, as plain data — the dialog
/// reads them from its controls and writes the merged copy back. Text fields hold exactly what
/// the boxes hold (comma lists, one announce rule per line).
/// </summary>
public sealed record AiSetupEditState
{
    public string MaxUploadSlots { get; init; } = "";
    public string MaxDownloadSlots { get; init; } = "";
    /// <summary>Comma-separated affil groups.</summary>
    public string Affils { get; init; } = "";
    public IReadOnlyList<SkiplistRule> Skiplist { get; init; } = [];
    public IReadOnlyList<SectionMapping> SectionMappings { get; init; } = [];
    /// <summary>One "[#channel ]pattern" per line.</summary>
    public string AnnounceRules { get; init; } = "";
    /// <summary>Null when the dialog has no priority selected.</summary>
    public SitePriority? Priority { get; init; }
    public bool DownloadOnly { get; init; }
    /// <summary>Comma-separated notification categories to exclude.</summary>
    public string ExcludedCategories { get; init; } = "";
    public bool RequestFillerEnabled { get; init; }
    public string RequestFillerPattern { get; init; } = "";
    public string RequestFillerChannel { get; init; } = "";
}

public sealed record AiSetupMergeResult(AiSetupEditState State, IReadOnlyList<string> Changes);

/// <summary>
/// AI Setup's merge rules: folds an <see cref="AiSetupResult"/> plus the deterministic SITE RULES
/// rules into the dialog's local edit state and lists what changed. Pure — it returns a new state
/// and never touches the input, the UI or <see cref="AppConfig"/>. This is for the user-reviewed
/// dialog only: the AiAgent pipeline must not call it (validators own AppConfig mutation).
/// </summary>
public static class AiSetupMerger
{
    public static AiSetupMergeResult Merge(AiSetupResult ai, IEnumerable<SkiplistRule> deterministicRules,
        AiSetupEditState current)
    {
        var changes = new List<string>();
        var mappings = current.SectionMappings.Select(Clone).ToList();
        var state = current with { Skiplist = [.. current.Skiplist], SectionMappings = mappings };

        if (ai.MaxUploadSlots.HasValue)
        {
            state = state with { MaxUploadSlots = ai.MaxUploadSlots.Value.ToString() };
            changes.Add($"Max uploads: {ai.MaxUploadSlots.Value}");
        }
        if (ai.MaxDownloadSlots.HasValue)
        {
            state = state with { MaxDownloadSlots = ai.MaxDownloadSlots.Value.ToString() };
            changes.Add($"Max downloads: {ai.MaxDownloadSlots.Value}");
        }

        if (ai.Affils.Count > 0)
        {
            var affils = SplitList(state.Affils);
            foreach (var a in ai.Affils) affils.Add(a);
            state = state with { Affils = string.Join(", ", affils.OrderBy(a => a)) };
            changes.Add($"Affils: {ai.Affils.Count} groups");
        }

        state = MergeSkiplist(ai, deterministicRules, state, changes);
        MergeSuggestedSections(ai, mappings, changes);
        MergeSectionMappings(ai, mappings, changes);
        state = MergeAnnounceRules(ai, state, changes);

        if (ParsePriority(ai.Priority) is { } priority && priority != state.Priority)
        {
            state = state with { Priority = priority };
            changes.Add($"Priority → {ai.Priority}");
        }

        if (ai.DownloadOnly == true && !state.DownloadOnly)
        {
            state = state with { DownloadOnly = true };
            changes.Add("Marked as download-only");
        }

        if (ai.ExcludedNotificationCategories.Count > 0)
        {
            var excluded = SplitList(state.ExcludedCategories);
            var added = 0;
            foreach (var cat in ai.ExcludedNotificationCategories)
                if (!string.IsNullOrWhiteSpace(cat) && excluded.Add(cat.Trim())) added++;
            if (added > 0)
            {
                state = state with { ExcludedCategories = string.Join(", ", excluded.OrderBy(c => c)) };
                changes.Add($"{added} notification category exclusion(s)");
            }
        }

        // Auto request filler — enable only if AI detected a channel pattern
        if (!string.IsNullOrWhiteSpace(ai.RequestFillerPattern) && !state.RequestFillerEnabled)
        {
            state = state with
            {
                RequestFillerPattern = ai.RequestFillerPattern,
                RequestFillerChannel = string.IsNullOrWhiteSpace(ai.RequestFillerChannel)
                    ? state.RequestFillerChannel : ai.RequestFillerChannel,
                RequestFillerEnabled = true
            };
            changes.Add("Auto request filler enabled");
        }

        return new AiSetupMergeResult(state, changes);
    }

    /// <summary>
    /// The AI's rules, supplemented by deterministic ones it missed (by pattern). Section-scoped
    /// rules from earlier runs are dropped first so they don't accumulate; the user's global
    /// rules are kept and never duplicated.
    /// </summary>
    private static AiSetupEditState MergeSkiplist(AiSetupResult ai, IEnumerable<SkiplistRule> deterministicRules,
        AiSetupEditState state, List<string> changes)
    {
        var incoming = ai.SkiplistRules.Select(r => r.ToSkiplistRule()).ToList();
        var incomingPatterns = incoming.Select(r => r.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in deterministicRules)
            if (incomingPatterns.Add(rule.Pattern)) incoming.Add(rule);

        if (incoming.Count == 0) return state;

        var skiplist = state.Skiplist.Where(r => r.Section == null).ToList();
        var removed = state.Skiplist.Count - skiplist.Count;
        var existing = skiplist.Select(r => r.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var rule in incoming)
        {
            if (!existing.Add(rule.Pattern)) continue;
            skiplist.Add(rule);
            added++;
        }
        if (added > 0 || removed > 0)
            changes.Add($"{added} skiplist rules (replaced {removed} old section rules)");
        return state with { Skiplist = skiplist };
    }

    /// <summary>"sections": a new key becomes a 1:1 mapping; an existing one only gains a missing path.</summary>
    private static void MergeSuggestedSections(AiSetupResult ai, List<SectionMapping> mappings, List<string> changes)
    {
        var added = 0;
        foreach (var (name, path) in ai.SuggestedSections)
        {
            var existing = mappings.FirstOrDefault(m => m.RemoteSection.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (string.IsNullOrEmpty(existing.Path))
                {
                    existing.Path = path;
                    added++;
                }
                continue;
            }
            mappings.Add(new SectionMapping
            {
                IrcSection = name,
                RemoteSection = name,
                Path = path,
                TriggerRegex = SectionMapping.DefaultTrigger,
                Enabled = true
            });
            added++;
        }
        if (added > 0)
            changes.Add($"{added} section mapping(s) added");
    }

    /// <summary>
    /// "section_mappings": new (IRC, remote) pairs are added, borrowing the path of any row with
    /// the same remote section. An existing pair only has its trigger replaced while that trigger
    /// is still the default — re-running AI Setup refreshes discovered triggers but never
    /// overwrites one the user edited.
    /// </summary>
    private static void MergeSectionMappings(AiSetupResult ai, List<SectionMapping> mappings, List<string> changes)
    {
        var added = 0;
        var updated = 0;
        foreach (var sm in ai.SectionMappings)
        {
            if (string.IsNullOrWhiteSpace(sm.IrcSection) || string.IsNullOrWhiteSpace(sm.RemoteSection)) continue;

            var aiTrigger = string.IsNullOrWhiteSpace(sm.Trigger) ? SectionMapping.DefaultTrigger : sm.Trigger.Trim();

            var existing = mappings.FirstOrDefault(m =>
                m.IrcSection.Equals(sm.IrcSection, StringComparison.OrdinalIgnoreCase) &&
                m.RemoteSection.Equals(sm.RemoteSection, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                var existingTrigger = (existing.TriggerRegex ?? "").Trim();
                var isDefault = SectionMapping.IsDefaultTrigger(existingTrigger);
                if (isDefault && aiTrigger != SectionMapping.DefaultTrigger)
                {
                    Log.Information("AI Setup: updating trigger for mapping {Irc}→{Remote}: '{Old}' → '{New}'",
                        existing.IrcSection, existing.RemoteSection, existingTrigger, aiTrigger);
                    existing.TriggerRegex = aiTrigger;
                    updated++;
                }
                else if (!isDefault)
                {
                    Log.Information("AI Setup: preserving user-edited trigger for {Irc}→{Remote}: '{Existing}' (AI suggested '{Ai}')",
                        existing.IrcSection, existing.RemoteSection, existingTrigger, aiTrigger);
                }
                else
                {
                    Log.Information("AI Setup: AI suggested '.*' for {Irc}→{Remote}, nothing to update",
                        existing.IrcSection, existing.RemoteSection);
                }
                continue;
            }

            var pathSource = mappings.FirstOrDefault(m =>
                m.RemoteSection.Equals(sm.RemoteSection, StringComparison.OrdinalIgnoreCase));

            mappings.Add(new SectionMapping
            {
                IrcSection = sm.IrcSection,
                RemoteSection = sm.RemoteSection,
                Path = pathSource?.Path ?? "",
                TriggerRegex = aiTrigger,
                Enabled = true
            });
            added++;
        }
        if (added > 0)
            changes.Add($"{added} IRC section mapping(s) detected");
        if (updated > 0)
            changes.Add($"{updated} mapping trigger(s) updated from IRC logs");
    }

    /// <summary>Appends announce rules the user doesn't already have (line match, ignoring case).</summary>
    private static AiSetupEditState MergeAnnounceRules(AiSetupResult ai, AiSetupEditState state, List<string> changes)
    {
        if (ai.AnnounceRules.Count == 0) return state;

        var existing = state.AnnounceRules.Trim();
        var existingLines = existing.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newLines = new List<string>();
        foreach (var ar in ai.AnnounceRules)
        {
            if (string.IsNullOrWhiteSpace(ar.Pattern)) continue;
            var line = string.IsNullOrWhiteSpace(ar.Channel) ? ar.Pattern : $"{ar.Channel} {ar.Pattern}";
            if (existingLines.Add(line))
                newLines.Add(line);
        }
        if (newLines.Count == 0) return state;

        changes.Add($"{newLines.Count} IRC announce rule(s) detected");
        return state with
        {
            AnnounceRules = string.IsNullOrEmpty(existing)
                ? string.Join("\n", newLines)
                : existing + "\n" + string.Join("\n", newLines)
        };
    }

    internal static SitePriority? ParsePriority(string? priority) =>
        priority?.Trim().ToLowerInvariant() switch
        {
            "verylow" or "very low" or "very_low" => SitePriority.VeryLow,
            "low" => SitePriority.Low,
            "normal" => SitePriority.Normal,
            "high" => SitePriority.High,
            "veryhigh" or "very high" or "very_high" => SitePriority.VeryHigh,
            _ => null
        };

    private static HashSet<string> SplitList(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static SectionMapping Clone(SectionMapping m) => new()
    {
        IrcSection = m.IrcSection,
        RemoteSection = m.RemoteSection,
        Path = m.Path,
        TriggerRegex = m.TriggerRegex,
        TagRules = [.. m.TagRules],
        Enabled = m.Enabled
    };
}
