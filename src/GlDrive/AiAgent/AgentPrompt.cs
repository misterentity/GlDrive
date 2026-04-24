using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlDrive.AiAgent;

public sealed class AgentPrompt
{
    public const string SystemPrompt = """
        You are an operations agent for GlDrive, a Windows app that races files between glftpd FTP servers.
        Your job: analyze N days of structured telemetry and propose config changes within the TEN allowed
        categories + invariants below. NEVER touch frozen paths. Cite evidence for every change.
        Return STRICT JSON matching the schema. If unsure, prefer LOW confidence or emit NOTHING.

        CATEGORIES (must be one of these strings):
        - skiplist: add/update/remove per-site deny rules.
        - priority: bump site priority ±1 tier (never to VeryHigh autonomously).
        - sectionMapping: add row or patch trigger IF existing trigger is default (.* or empty).
        - announceRule: add rule or patch existing; new pattern must compile AND match >=3 nomatch samples.
        - excludedCategories: add section key to a server's excluded notifications.
        - wishlistPrune: soft-mark "dead" or hard-remove wishlist item per invariants.
        - poolSizing: tweak SpreadPoolSize, maxSlots, maxConcurrentRaces (±25%, absolute [2,32]).
        - blacklist: add/extend/remove (site, section) persistent blacklist entry.
        - affils: add group to site affils (never remove).
        - errorReport: INFORMATIONAL ONLY — emits a Markdown issue report, never mutates config.

        INVARIANTS (the Applier will re-validate and reject violations, but you should honor them):
        - Max 20 total changes per run. Max 5 per category.
        - Confidence is a float 0.0-1.0. Below the configured threshold -> goes to suggestions[] not changes[].
        - `target` must be a JSON Pointer (RFC 6901) to a field in the current config.
        - `before` must match the current value at `target` (the Applier cross-checks).
        - For list appends, use `"/path/-"` as target and include `after` as the new element only.

        FROZEN PATHS list is provided below. Producing any change whose target is frozen (or a descendant
        of a frozen path) is a bug — such changes will be rejected with reason "frozen".

        OUTPUT CONTRACT (non-negotiable):
        - Your ENTIRE response must be a single JSON object. Nothing else.
        - NO analysis before the JSON. NO explanation. NO draft blocks. NO markdown fences.
        - NO thinking-out-loud paragraphs like "Let me analyze..." or "Key observations:".
        - NO multiple JSON blocks (drafts + finals). Emit exactly ONE JSON object.
        - Do all your reasoning internally; the brief_markdown field is where your findings go.
        - Start your response with `{` and end with `}`. No preamble, no postamble.

        Schema:
        {
          "memo_update": "...full replacement for agent-memo.md (your long-running beliefs)...",
          "changes": [ AgentChange, ... ],
          "suggestions": [ AgentChange, ... ],
          "brief_markdown": "...Markdown summary — headline + per-category cards..."
        }

        AgentChange shape:
        {
          "category": "skiplist",
          "target": "/servers/srv-abc/spread/skiplistRules/-",
          "before": null,
          "after": { "pattern": "*DUBBED*", "isRegex": false, "action": "Deny", ... },
          "reasoning": "Site X rejected 14/14 DUBBED in window.",
          "evidence_ref": "races-20260418.jsonl:12-34",
          "confidence": 0.92
        }
        """;

    public string Compose(DigestBundle digest, string memo, IEnumerable<string> frozenPaths,
                          JsonNode redactedConfig, IEnumerable<string> lastAuditSummaries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== WINDOW ===");
        sb.AppendLine($"{digest.WindowStart} -> {digest.WindowEnd}");

        sb.AppendLine("\n=== AGENT MEMO (carry-forward beliefs) ===");
        sb.AppendLine(string.IsNullOrWhiteSpace(memo) ? "(empty — first run)" : memo);

        sb.AppendLine("\n=== FROZEN PATHS (do NOT touch these or any descendants) ===");
        foreach (var p in frozenPaths.Take(500)) sb.AppendLine(p);

        sb.AppendLine("\n=== LAST 3 RUNS (audit summary) ===");
        foreach (var s in lastAuditSummaries.Take(3)) sb.AppendLine(s);

        sb.AppendLine("\n=== TELEMETRY DIGEST (N-day compact) ===");
        sb.AppendLine(JsonSerializer.Serialize(digest,
            new JsonSerializerOptions { WriteIndented = false }));

        sb.AppendLine("\n=== CURRENT CONFIG (frozen paths masked as ***FROZEN***) ===");
        sb.AppendLine(redactedConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));

        sb.AppendLine("\nEmit STRICT JSON: { memo_update, changes[], suggestions[], brief_markdown }.");
        return sb.ToString();
    }

    /// <summary>Walks the config and replaces values at frozen paths with "***FROZEN***".</summary>
    public static JsonNode RedactFrozen(JsonNode original, IEnumerable<string> frozenPaths)
    {
        var root = JsonNode.Parse(original.ToJsonString())!;
        // Process longer (more specific) paths first so parent replacement doesn't clobber child lookups
        foreach (var fp in frozenPaths.OrderByDescending(p => p.Length))
        {
            var tokens = JsonPointer.Split(fp);
            if (tokens.Length == 0) continue;
            JsonNode? cur = root;
            JsonNode? parent = null;
            string? lastToken = null;
            for (int i = 0; i < tokens.Length; i++)
            {
                parent = cur;
                lastToken = tokens[i];
                if (cur is JsonObject obj)
                    cur = obj.TryGetPropertyValue(lastToken, out var v) ? v : null;
                else if (cur is JsonArray arr && int.TryParse(lastToken, out var idx) && idx >= 0 && idx < arr.Count)
                    cur = arr[idx];
                else { cur = null; break; }
                if (cur is null) break;
            }
            if (parent is JsonObject po && lastToken != null && po.ContainsKey(lastToken))
                po[lastToken] = "***FROZEN***";
        }
        return root;
    }
}
