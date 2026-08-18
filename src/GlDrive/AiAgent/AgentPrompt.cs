using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlDrive.AiAgent;

public sealed class AgentPrompt
{
    public const string SystemPrompt = """
        You are an operations agent for GlDrive, a Windows app that races files between glftpd FTP servers.
        Your job: analyze N days of structured telemetry and propose config changes within the ELEVEN allowed
        categories + invariants below. NEVER touch frozen paths. Cite evidence for every change.
        Return STRICT JSON matching the schema. If unsure, prefer LOW confidence or emit NOTHING.

        CATEGORIES (must be one of these strings):
        - skiplist: add/update/remove per-site deny rules.
        - priority: bump site priority ±1 tier (never to VeryHigh autonomously).
        - sectionMapping: add row or patch trigger IF existing trigger is default (.* or empty).
          Drive this from the `sectionFolder` digest (see DIGESTS below): when the data shows a release
          type/quality in an ircSection consistently lands in one observedRemoteSection, tighten that
          section's default ".*" trigger to a discriminating regex (e.g. "(?i)\.1080p\." for 1080p rows).
          Only ADD a mapping when announceCount is meaningful (not 1-off noise). HARD RULE: a proposed
          RemoteSection MUST already exist in that site's Sections — the validator rejects unknown ones.
        - announceRule: add rule or patch existing; new pattern must compile AND match >=3 nomatch samples.
        - excludedCategories: add section key to a server's excluded notifications.
        - wishlistPrune: soft-mark "dead" or hard-remove wishlist item per invariants.
        - poolSizing: tweak SpreadPoolSize, maxSlots, maxConcurrentRaces (±25%, absolute [2,32]).
        - affils: add group to site affils (never remove).
        - errorReport: INFORMATIONAL ONLY — emits a Markdown issue report, never mutates config.
        - downloadOnly: flip /servers/{id}/spread/downloadOnly bool. Use HIGH confidence — prefer
          true when site shows consistent upload-side failures (>80% MKD-denied or 530s); prefer
          false ONLY when the user has been manually trying to upload to a flagged-download-only site.
        - requestFiller: tweak /servers/{id}/irc/requestFiller/{enabled|pattern|channel}. Pattern
          must compile AND contain (?<release>...) capture group. Channel may be empty (=any).

        DISABLED — DO NOT EMIT: the `blacklist` category is not applyable (Phase 8 mutation pipeline
        is unimplemented; the Applier rejects every blacklist change). Never emit a blacklist change.

        INVARIANTS (the Applier will re-validate and reject violations, but you should honor them):
        - Max 20 total changes per run. Max 5 per category.
        - Confidence is a float 0.0-1.0. Below the configured threshold -> goes to suggestions[] not changes[].
        - `target` must be a JSON Pointer (RFC 6901) to a field in the current config.
        - `before` must match the current value at `target` (the Applier cross-checks).
        - For list appends, use `"/path/-"` as target and include `after` as the new element only.

        FROZEN PATHS list is provided below. Producing any change whose target is frozen (or a descendant
        of a frozen path) is a bug — such changes will be rejected with reason "frozen".

        DIGESTS (new): the telemetry digest now includes `sectionFolder` — a deterministic co-occurrence
        table. Each row is (serverId, ircSection, parsedType, quality) -> observedRemoteSection, with
        announceCount and raceCompletionRate. It is EVIDENCE of which release types actually land in which
        remote folders. Use it to propose discriminating sectionMapping triggers (per the sectionMapping
        category rule above): high announceCount + a clear observedRemoteSection for a specific
        parsedType/quality is the signal to tighten a default ".*" trigger or add a targeted row.

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

    /// <summary>
    /// Whole-prompt character ceiling. The smallest model in the fallback chain
    /// (openai/gpt-oss-120b) has a 131,072-token context and we reserve
    /// <see cref="AgentClient.MaxOutputTokens"/> of it for output, leaving ~99k for input.
    /// JSON telemetry runs about 3.5 chars/token, so this stays inside that ceiling with room
    /// to spare. Deliberately a CHARACTER budget: we cannot tokenize locally, and a conservative
    /// char count is the honest approximation.
    /// </summary>
    public const int MaxPromptChars = 300_000;

    /// <summary>
    /// Per-string ceiling inside the serialized digest and config. Server ids, section keys and
    /// release names are all well under this; anything longer is a defect upstream, not data.
    /// </summary>
    public const int MaxFieldChars = 512;

    /// <summary>
    /// Appended wherever content was cut, so the model never reasons over a silently mangled value.
    /// Deliberately pure ASCII: System.Text.Json escapes non-ASCII by default, so a "…" here would
    /// be written as … and the marker would not survive into the prompt verbatim.
    /// </summary>
    public const string TruncationMarker = "...[TRUNCATED]";

    public string Compose(DigestBundle digest, string memo, IEnumerable<string> frozenPaths,
                          JsonNode redactedConfig, IEnumerable<string> lastAuditSummaries)
    {
        // Clamp per-field FIRST: one pathological telemetry row (a 2,000,000-char section, seen
        // 2026-08-14) is what produced a 1.5M-token prompt that every model refused with HTTP 400.
        var digestJson = ClampJsonStrings(JsonSerializer.Serialize(digest,
            new JsonSerializerOptions { WriteIndented = false }));
        var configJson = ClampJsonStrings(redactedConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));

        var sb = new StringBuilder();
        sb.AppendLine("=== WINDOW ===");
        sb.AppendLine($"{digest.WindowStart} -> {digest.WindowEnd}");

        sb.AppendLine("\n=== AGENT MEMO (carry-forward beliefs) ===");
        // The memo is model-authored and fully replaced each run, so it can grow without bound.
        sb.AppendLine(string.IsNullOrWhiteSpace(memo) ? "(empty — first run)" : Fit(memo, 16_000));

        sb.AppendLine("\n=== FROZEN PATHS (do NOT touch these or any descendants) ===");
        foreach (var p in frozenPaths.Take(500)) sb.AppendLine(Fit(p, MaxFieldChars));

        sb.AppendLine("\n=== LAST 3 RUNS (audit summary) ===");
        foreach (var s in lastAuditSummaries.Take(3)) sb.AppendLine(Fit(s, MaxFieldChars));

        const string trailer = "\nEmit STRICT JSON: { memo_update, changes[], suggestions[], brief_markdown }.";

        // Whole-prompt backstop: per-field clamping alone cannot stop bulk spread across many
        // individually-legal fields. Digest and config are the only two unbounded blobs; give the
        // digest first call on what's left, since it is the actual evidence.
        var overhead = sb.Length + trailer.Length + 200;
        var remaining = Math.Max(0, MaxPromptChars - overhead);

        // Config gets first call on the budget: every proposed change carries a JSON Pointer into
        // it, and the Applier cross-checks `before` against the live value, so a trimmed config
        // produces changes that cannot apply. The digest is evidence and can be sampled instead.
        var configBudget = Math.Min(configJson.Length, remaining);
        var digestBudget = Math.Max(0, remaining - configBudget);

        sb.AppendLine("\n=== TELEMETRY DIGEST (N-day compact) ===");
        sb.AppendLine(FitJson(digestJson, digestBudget));

        sb.AppendLine("\n=== CURRENT CONFIG (frozen paths masked as ***FROZEN***) ===");
        sb.AppendLine(FitJson(configJson, configBudget));

        sb.Append(trailer);
        return sb.ToString();
    }

    /// <summary>
    /// Brings a JSON document under <paramref name="budget"/> chars by dropping ARRAY ELEMENTS,
    /// largest array first, rather than cutting the text.
    ///
    /// String-truncating JSON hands the model a document it cannot parse — the telemetry then
    /// reads as corrupt rather than as sampled, which is worse than sending less of it. Structural
    /// trimming keeps the document well-formed at every step.
    /// </summary>
    private static string FitJson(string json, int budget)
    {
        if (json.Length <= budget) return json;

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return Fit(json, budget); }
        if (root is null) return Fit(json, budget);

        var opts = new JsonSerializerOptions { WriteIndented = false };
        for (var guard = 0; guard < 1000; guard++)
        {
            var s = root.ToJsonString(opts);
            if (s.Length <= budget) return s;

            // Objects as well as arrays: the digests carry most of their bulk in DICTIONARIES
            // (KbpsByRoute, CompletionRateBySection, …), so trimming only arrays left the
            // document over budget and fell through to a mid-JSON string cut.
            var biggest = LargestContainer(root);
            if (biggest is null) return Fit(s, budget);

            // Halve rather than drop one at a time: a 20k-entry container would otherwise need
            // 20k full re-serializations to converge.
            switch (biggest)
            {
                case JsonArray arr when arr.Count > 0:
                    for (var keep = arr.Count / 2; arr.Count > keep;) arr.RemoveAt(arr.Count - 1);
                    break;
                case JsonObject o when o.Count > 0:
                    foreach (var k in o.Select(kv => kv.Key).Skip(o.Count / 2).ToList()) o.Remove(k);
                    break;
                default:
                    return Fit(s, budget);
            }

            if (root is JsonObject ro) ro["_truncated"] = TruncationMarker;
        }
        return Fit(root.ToJsonString(opts), budget);
    }

    /// <summary>The array or object holding the most direct entries, anywhere in the document.</summary>
    private static JsonNode? LargestContainer(JsonNode node)
    {
        JsonNode? best = null;
        var bestCount = 0;
        Visit(node);
        return best;

        void Visit(JsonNode n)
        {
            switch (n)
            {
                case JsonArray arr:
                    if (arr.Count > bestCount) { best = arr; bestCount = arr.Count; }
                    foreach (var c in arr) if (c is not null) Visit(c);
                    break;
                case JsonObject obj:
                    if (obj.Count > bestCount) { best = obj; bestCount = obj.Count; }
                    foreach (var kv in obj) if (kv.Value is not null) Visit(kv.Value);
                    break;
            }
        }
    }

    /// <summary>Truncates <paramref name="s"/> to <paramref name="max"/> chars, marking the cut. Idempotent.</summary>
    private static string Fit(string? s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        if (max <= TruncationMarker.Length) return TruncationMarker;
        return string.Concat(s.AsSpan(0, max - TruncationMarker.Length), TruncationMarker);
    }

    /// <summary>
    /// Clamps every string VALUE in a JSON document to <see cref="MaxFieldChars"/>, leaving
    /// structure and short values untouched. Returns valid JSON; idempotent.
    /// </summary>
    public static string ClampJsonStrings(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return Fit(json, MaxPromptChars); }
        if (root is null) return json;

        Walk(root);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

        static void Walk(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    // Materialize keys first: mutating the object during enumeration would throw.
                    var keys = obj.Select(kv => kv.Key).ToList();

                    foreach (var key in keys)
                    {
                        var child = obj[key];
                        if (child is JsonValue v && v.TryGetValue<string>(out var s))
                        {
                            if (s.Length > MaxFieldChars) obj[key] = Fit(s, MaxFieldChars);
                        }
                        else if (child is not null) Walk(child);
                    }

                    // KEYS as well as values. Several digests key dictionaries BY section name
                    // (CompletionRateBySection, WinRateByServer, AbortReasonHistogram), so the
                    // 2026-08-14 poison section arrived as a 2,000,000-char property NAME and
                    // clamping only values left it completely untouched.
                    foreach (var key in keys.Where(k => k.Length > MaxFieldChars))
                    {
                        var detached = obj[key]?.DeepClone();
                        obj.Remove(key);

                        var clamped = Fit(key, MaxFieldChars);
                        // Two distinct long keys can clamp onto the same name; suffix until free
                        // so no row silently swallows another.
                        var unique = clamped;
                        for (var n = 2; obj.ContainsKey(unique); n++) unique = $"{clamped}#{n}";
                        obj[unique] = detached;
                    }
                    break;

                case JsonArray arr:
                    for (var i = 0; i < arr.Count; i++)
                    {
                        var child = arr[i];
                        if (child is JsonValue v && v.TryGetValue<string>(out var s))
                        {
                            if (s.Length > MaxFieldChars) arr[i] = Fit(s, MaxFieldChars);
                        }
                        else if (child is not null) Walk(child);
                    }
                    break;
            }
        }
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
