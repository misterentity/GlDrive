using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GlDrive.Config;
using Serilog;

namespace GlDrive.Spread;

public class AiSetupResult
{
    [JsonPropertyName("max_upload_slots")]
    public int? MaxUploadSlots { get; set; }
    [JsonPropertyName("max_download_slots")]
    public int? MaxDownloadSlots { get; set; }
    [JsonPropertyName("affils")]
    public List<string> Affils { get; set; } = [];
    [JsonPropertyName("skiplist")]
    public List<AiSkiplistRule> SkiplistRules { get; set; } = [];
    [JsonPropertyName("sections")]
    public Dictionary<string, string> SuggestedSections { get; set; } = new();
    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = "";
}

public class AiSkiplistRule
{
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = "";
    [JsonPropertyName("is_regex")]
    public bool IsRegex { get; set; }
    [JsonPropertyName("action")]
    public string Action { get; set; } = "Deny";
    [JsonPropertyName("match_dirs")]
    public bool MatchDirectories { get; set; } = true;
    [JsonPropertyName("match_files")]
    public bool MatchFiles { get; set; } = true;
    [JsonPropertyName("section")]
    public string? Section { get; set; }

    public SkiplistRule ToSkiplistRule() => new()
    {
        Pattern = Pattern,
        IsRegex = IsRegex,
        Action = Action.Equals("Allow", StringComparison.OrdinalIgnoreCase) ? SkiplistAction.Allow : SkiplistAction.Deny,
        MatchDirectories = MatchDirectories,
        MatchFiles = MatchFiles,
        Section = Section
    };
}

public class OpenRouterClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private const string SystemPrompt = """
        You are an expert on glftpd FTP server configuration and scene release distribution rules.
        You analyze SITE RULES output and generate spread/FXP skiplist configuration that prevents
        uploading content that would get nuked.

        CRITICAL INSTRUCTIONS:
        1. Read EVERY section rule carefully. Rules like "English Only" mean deny non-english releases.
        2. "No X" rules mean create a deny pattern for X. Be specific with glob patterns.
        3. Section-specific rules MUST include the "section" field matching the FTP section name.
        4. Map rule section names to FTP sections: "0Day" → 0DAY, "Apps" → APPS, "Games" → GAMES,
           "TV" → TV_HD/TV_SD, etc. Use the CURRENT SECTIONS provided to match names exactly.
        5. Extract nuke-worthy restrictions only — ignore informational/social rules.

        PATTERN GUIDELINES:
        - Use case-insensitive glob patterns: *DUBS*, *SUBBED*, *.GERMAN.*, *READNFO*
        - For "No X" rules: *X* as a directory pattern (match_dirs=true, match_files=false)
        - For "English Only": create deny patterns for common non-English tags: *GERMAN*, *FRENCH*,
          *SPANISH*, *ITALIAN*, *DUTCH*, *SWEDISH*, *DANISH*, *NORWEGIAN*, *POLISH*, *RUSSIAN*,
          *PORTUGUESE*, *CHINESE*, *JAPANESE*, *KOREAN*, *ARABIC*, *TURKISH*, *HINDI*, *THAI*,
          *CZECH*, *HUNGARIAN*, *ROMANIAN*, *FINNISH*, *SUBBED*, *DUBBED*, *MULTi*
        - For "Current year only" (games): use deny pattern for prior year tags if detectable
        - For category bans like "No CADCAM": *CADCAM*, *CAD.CAM*, *CAD-CAM*
        - For "No Hardware Specific": *HARDWARE*, *DRIVER*, *FIRMWARE*

        Respond with ONLY a JSON object (no markdown fences, no text outside the JSON):
        {
            "max_upload_slots": <number or null if not mentioned>,
            "max_download_slots": <number or null if not mentioned>,
            "affils": ["GROUP1", "GROUP2"],
            "skiplist": [
                {"pattern": "*GERMAN*", "is_regex": false, "action": "Deny", "match_dirs": true, "match_files": false, "section": "0DAY"},
                {"pattern": "*CADCAM*", "is_regex": false, "action": "Deny", "match_dirs": true, "match_files": false, "section": "APPS"},
                {"pattern": "*.jpg", "is_regex": false, "action": "Deny", "match_dirs": false, "match_files": true, "section": null}
            ],
            "sections": {"TV_HD": "/incoming/tv-hd"},
            "explanation": "Brief summary: what rules were found and what deny patterns were generated"
        }
        """;

    public OpenRouterClient(string apiKey, string model = "openai/gpt-oss-120b:free")
    {
        _model = model;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/"),
            Timeout = TimeSpan.FromSeconds(180)
        };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        _http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/misterentity/GlDrive");
        _http.DefaultRequestHeaders.Add("X-Title", "GlDrive");
    }

    public async Task<AiSetupResult?> AnalyzeSiteRules(
        string siteRulesRaw,
        Dictionary<string, string> currentSections,
        List<string> currentAffils,
        Dictionary<string, List<string>>? sectionSamples = null,
        CancellationToken ct = default)
    {
        var samplesText = "";
        if (sectionSamples != null && sectionSamples.Count > 0)
        {
            var sb = new StringBuilder("\n=== SAMPLE RELEASES PER SECTION ===\n");
            foreach (var (section, releases) in sectionSamples)
            {
                sb.AppendLine($"[{section}]");
                foreach (var r in releases)
                    sb.AppendLine($"  {r}");
            }
            samplesText = sb.ToString();
        }

        var userPrompt = $"""
            Analyze this glftpd SITE RULES and generate deny patterns for EVERY restriction.
            Be thorough — a missed rule means content gets nuked.

            === SITE RULES ===
            {siteRulesRaw[..Math.Min(siteRulesRaw.Length, 8000)]}

            === FTP SECTIONS (use these exact names in skiplist "section" field) ===
            {string.Join("\n", currentSections.Select(kv => $"{kv.Key} = {kv.Value}"))}

            === CURRENT AFFILS (already configured, add any new ones) ===
            {(currentAffils.Count > 0 ? string.Join(", ", currentAffils) : "(none)")}
            {samplesText}
            For EACH section rule (0Day, Apps, Games, TV, etc.):
            1. Map it to the matching FTP section name from the list above
            2. Create deny patterns for every "No X" and "X Only" restriction
            3. "English Only" → deny all common non-English language tags
            4. Include slot limits if the rules mention upload/download limits
            """;

        try
        {
            var request = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.1,
                max_tokens = 8000
            };

            var json = JsonSerializer.Serialize(request, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync("chat/completions", content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("OpenRouter API error: {Status} {Body}", response.StatusCode, responseBody);
                return null;
            }

            // Parse the response
            using var doc = JsonDocument.Parse(responseBody);
            var messageContent = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            Log.Information("OpenRouter AI response ({Model}): {Length} chars", _model, messageContent.Length);

            // Extract JSON from response (AI might wrap it in markdown code fences)
            var jsonStart = messageContent.IndexOf('{');
            var jsonEnd = messageContent.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var resultJson = messageContent[jsonStart..(jsonEnd + 1)];
                try
                {
                    return JsonSerializer.Deserialize<AiSetupResult>(resultJson, JsonOptions);
                }
                catch (JsonException ex)
                {
                    Log.Warning("AI JSON parse failed, attempting repair: {Error}", ex.Message);
                    var repaired = RepairTruncatedJson(resultJson);
                    if (repaired != null)
                        return JsonSerializer.Deserialize<AiSetupResult>(repaired, JsonOptions);
                }
            }

            Log.Warning("AI response did not contain valid JSON: {Response}", messageContent[..Math.Min(200, messageContent.Length)]);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OpenRouter API call failed");
            return null;
        }
    }

    /// <summary>
    /// Attempts to repair truncated JSON from LLM responses that hit the token limit.
    /// Removes the last incomplete element and closes all open brackets/braces.
    /// </summary>
    private static string? RepairTruncatedJson(string json)
    {
        try
        {
            // Find the last complete object/value by looking for the last complete }, or ]
            // then close any remaining open structures
            var lastComplete = -1;
            var depth = 0;
            var inString = false;
            var escape = false;

            for (var i = 0; i < json.Length; i++)
            {
                var c = json[i];
                if (escape) { escape = false; continue; }
                if (c == '\\' && inString) { escape = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;

                if (c is '{' or '[') depth++;
                else if (c is '}' or ']') { depth--; lastComplete = i; }
            }

            if (depth <= 0) return null; // Not truncated, parse error is something else

            // Truncate at the last complete element boundary
            // Find the last comma before the incomplete element
            var truncateAt = json.Length;
            for (var i = json.Length - 1; i > lastComplete; i--)
            {
                if (json[i] == ',') { truncateAt = i; break; }
            }
            if (truncateAt == json.Length) truncateAt = lastComplete + 1;

            var repaired = json[..truncateAt];

            // Count remaining open brackets/braces and close them
            var openBraces = 0;
            var openBrackets = 0;
            inString = false;
            escape = false;
            for (var i = 0; i < repaired.Length; i++)
            {
                var c = repaired[i];
                if (escape) { escape = false; continue; }
                if (c == '\\' && inString) { escape = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') openBraces++;
                else if (c == '}') openBraces--;
                else if (c == '[') openBrackets++;
                else if (c == ']') openBrackets--;
            }

            // Close open structures
            for (var i = 0; i < openBrackets; i++) repaired += "]";
            for (var i = 0; i < openBraces; i++) repaired += "}";

            Log.Information("Repaired truncated AI JSON: {OrigLen} -> {RepairedLen} chars, closed {Brackets} brackets + {Braces} braces",
                json.Length, repaired.Length, openBrackets, openBraces);
            return repaired;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "JSON repair failed");
            return null;
        }
    }

    /// <summary>
    /// Deterministic parser: scans SITE RULES text for restriction patterns and generates
    /// skiplist deny rules. Runs after AI to supplement, or standalone if AI fails.
    /// Handles glftpd rule formats: "No X/Y/Z", "X Only", slash-separated ban lists,
    /// and section-scoped rules.
    /// </summary>
    public static List<AiSkiplistRule> ParseRulesFallback(string rulesText, IReadOnlyDictionary<string, string> sections)
    {
        var rules = new List<AiSkiplistRule>();
        var lines = rulesText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var sectionNames = sections.Keys.ToList();
        string? currentSection = null;

        // Keywords that map to deny patterns (case-insensitive matching in rule text)
        // Each keyword can have multiple glob patterns to generate
        var denyKeywords = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Software categories
            ["driverware"] = ["*DRIVERWARE*"],
            ["deviceware"] = ["*DEVICEWARE*"],
            ["themeware"] = ["*THEMEWARE*"],
            ["cadcam"] = ["*CADCAM*", "*CAD.CAM*", "*CAD-CAM*"],
            ["carcad"] = ["*CARCAD*", "*CAR.CAD*", "*CAR-CAD*"],
            ["cae"] = ["*CAE*"],
            ["casing"] = ["*CASING*"],
            ["casino"] = ["*CASINO*"],
            ["csy"] = ["*CSY*"],
            // Content types
            ["pop"] = ["*POP*"],
            ["fortune"] = ["*FORTUNE*"],
            ["monthly"] = ["*MONTHLY*"],
            ["beta"] = ["*BETA*"],
            ["abandoned"] = ["*ABANDONED*"],
            ["vanity"] = ["*VANITY*"],
            ["demo"] = ["*DEMO*"],
            // Hardware/religious
            ["hardware"] = ["*HARDWARE*", "*FIRMWARE*"],
            ["religious"] = ["*RELIGIOUS*", "*BIBLE*", "*CHRISTIAN*"],
            ["rpc"] = ["*RPC*"],
            // Quality
            ["crap"] = ["*CRAP*"],
            // Apps-specific
            ["basicapps"] = ["*BASICAPPS*", "*BASIC.APPS*"],
            ["courseware"] = ["*COURSEWARE*"],
            ["examware"] = ["*EXAMWARE*"],
            // OVA etc (0day specific)
            ["ova"] = ["*OVA*"],
            ["svamhapics"] = ["*SVAMHAPICS*"],
        };

        var languageTags = new[] {
            "GERMAN", "FRENCH", "SPANISH", "ITALIAN", "DUTCH", "SWEDISH", "DANISH",
            "NORWEGIAN", "POLISH", "RUSSIAN", "PORTUGUESE", "CHINESE", "JAPANESE",
            "KOREAN", "ARABIC", "TURKISH", "HINDI", "THAI", "CZECH", "HUNGARIAN",
            "ROMANIAN", "FINNISH", "HEBREW", "GREEK", "CROATIAN", "SERBIAN",
            "SUBBED", "DUBBED", "MULTi", "MULTiLANGUAGE"
        };

        void AddRule(string pattern, string? section) =>
            rules.Add(new AiSkiplistRule { Pattern = pattern, MatchDirectories = true, MatchFiles = false, Section = section });

        foreach (var line in lines)
        {
            var lower = line.ToLowerInvariant();

            // Detect section headers: "0Day Section Rules", "Apps Section Rules", etc.
            if (lower.Contains("section rules") || lower.Contains("section rule"))
            {
                currentSection = DetectSectionFromHeader(line, sectionNames);
                continue;
            }

            // "General Upload Rules" / "General Site Rules" → no section scope
            if (lower.Contains("general") && (lower.Contains("upload") || lower.Contains("site")) && lower.Contains("rule"))
            {
                currentSection = null;
                continue;
            }

            // === Language restrictions ===
            if (lower.Contains("english only"))
            {
                foreach (var lang in languageTags)
                    AddRule($"*{lang}*", currentSection);
            }

            // === Platform restrictions ===
            if (lower.Contains("windows only"))
            {
                AddRule("*LINUX*", currentSection);
                AddRule("*MACOS*", currentSection);
                AddRule("*MAC.OSX*", currentSection);
                AddRule("*MacOSX*", currentSection);
            }

            // === "No X" and "No X/Y/Z" patterns ===
            // Match lines containing "No" followed by keywords, handling slash-separated lists
            // e.g. "No Driverware/Deviceware/Themeware" → deny each
            // e.g. "No BETA/ABANDONED/Vanity/Demo" → deny each
            // e.g. "CADCAM/CARCAD Computer-Aided" → deny each (no "No" prefix needed)
            foreach (var (keyword, patterns) in denyKeywords)
            {
                // Check if this keyword appears in the line (as part of a ban rule)
                if (!lower.Contains(keyword.ToLowerInvariant())) continue;

                // Only generate deny if the line looks like a restriction:
                // starts with "no", contains nuke penalty, or is in a numbered rule list
                var isRestriction = lower.Contains("no ") || lower.Contains("nuke") ||
                    lower.Contains("| ") || System.Text.RegularExpressions.Regex.IsMatch(line, @"^\(\d+\)") ||
                    lower.Contains("not allowed") || lower.Contains("banned");

                // Also match standalone category mentions in section rules (e.g. "CADCAM/CARCAD Computer-Aided")
                if (!isRestriction && currentSection != null)
                    isRestriction = true; // In a section context, any keyword mention is likely a restriction

                if (isRestriction)
                {
                    foreach (var pattern in patterns)
                        AddRule(pattern, currentSection);
                }
            }

            // === "Current year only" (games) ===
            if (lower.Contains("current year only"))
            {
                // Deny prior year tags in release names
                var currentYear = DateTime.Now.Year;
                for (var y = currentYear - 3; y < currentYear; y++)
                    AddRule($"*{y}*", currentSection);
            }
        }

        // Deduplicate by pattern+section
        return rules.DistinctBy(r => $"{r.Pattern?.ToUpperInvariant()}|{r.Section}").ToList();
    }

    private static string? DetectSectionFromHeader(string header, List<string> sectionNames)
    {
        var lower = header.ToLowerInvariant();

        // Direct section name matching: "0Day" → "0DAY", "Apps" → "APPS", "Games" → "GAMES"
        var headerAliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["0day"] = ["0DAY", "0DAY_INT"],
            ["apps"] = ["APPS", "APPS_INT"],
            ["games"] = ["GAMES", "GAMES_INT", "NSW"],
            ["tv"] = ["TV_HD", "TV", "TV_SPORTS", "X264_HD"],
            ["flac"] = ["FLAC"],
            ["mp3"] = ["MP3"],
            ["mvid"] = ["MVID"],
            ["bookware"] = ["BOOKWARE"],
            ["x265"] = ["X265"],
            ["bluray"] = ["BLURAY", "BLURAY_UHD"],
        };

        foreach (var (alias, candidates) in headerAliases)
        {
            if (!lower.Contains(alias)) continue;
            foreach (var candidate in candidates)
            {
                if (sectionNames.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    return candidate;
            }
        }

        // Fuzzy: try normalizing both sides
        foreach (var name in sectionNames)
        {
            var normName = name.ToLowerInvariant().Replace("_", "").Replace("-", "");
            if (lower.Contains(normName)) return name;
        }

        return null;
    }

    public void Dispose() => _http.Dispose();
}
