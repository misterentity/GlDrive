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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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
                max_tokens = 4000
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
                return JsonSerializer.Deserialize<AiSetupResult>(resultJson, JsonOptions);
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
    /// Deterministic fallback: scan SITE RULES text for common restriction patterns
    /// and generate skiplist rules without needing an LLM. Called when AI fails or
    /// to supplement AI results with patterns it may have missed.
    /// </summary>
    public static List<AiSkiplistRule> ParseRulesFallback(string rulesText, IReadOnlyDictionary<string, string> sections)
    {
        var rules = new List<AiSkiplistRule>();
        var lines = rulesText.Split('\n').Select(l => l.Trim()).ToList();

        // Detect which section context we're in by scanning headers
        string? currentSection = null;
        var sectionNames = sections.Keys.ToList();

        foreach (var line in lines)
        {
            var lower = line.ToLowerInvariant();

            // Detect section headers like "0Day Section Rules", "Apps Section Rules"
            if (lower.Contains("section rules") || lower.Contains("section rule"))
            {
                currentSection = DetectSectionFromHeader(line, sectionNames);
            }

            // "English Only" → deny non-English language tags
            if (lower.Contains("english only"))
            {
                var langs = new[] { "GERMAN", "FRENCH", "SPANISH", "ITALIAN", "DUTCH", "SWEDISH",
                    "DANISH", "NORWEGIAN", "POLISH", "RUSSIAN", "PORTUGUESE", "CHINESE", "JAPANESE",
                    "KOREAN", "ARABIC", "TURKISH", "HINDI", "THAI", "CZECH", "HUNGARIAN", "ROMANIAN",
                    "FINNISH", "SUBBED", "DUBBED", "MULTi" };
                foreach (var lang in langs)
                    rules.Add(new AiSkiplistRule { Pattern = $"*{lang}*", MatchDirectories = true, MatchFiles = false, Section = currentSection });
            }

            // "No X" patterns
            var noPatterns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["driverware"] = ["*DRIVERWARE*", "*DRIVER.WARE*"],
                ["deviceware"] = ["*DEVICEWARE*", "*DEVICE.WARE*"],
                ["themeware"] = ["*THEMEWARE*", "*THEME.WARE*"],
                ["cadcam"] = ["*CADCAM*", "*CAD.CAM*", "*CAD-CAM*"],
                ["carcad"] = ["*CARCAD*", "*CAR.CAD*"],
                ["hardware specific"] = ["*HARDWARE*"],
                ["religious"] = ["*RELIGIOUS*", "*BIBLE*"],
                ["crap"] = ["*CRAP*"],
            };

            foreach (var (keyword, patterns) in noPatterns)
            {
                if (lower.Contains($"no {keyword}") || lower.Contains($"no {keyword.Replace(" ", "")}"))
                {
                    foreach (var pattern in patterns)
                        rules.Add(new AiSkiplistRule { Pattern = pattern, MatchDirectories = true, MatchFiles = false, Section = currentSection });
                }
            }

            // "Windows only" → deny Linux/Mac-specific
            if (lower.Contains("windows only"))
            {
                rules.Add(new AiSkiplistRule { Pattern = "*LINUX*", MatchDirectories = true, MatchFiles = false, Section = currentSection });
                rules.Add(new AiSkiplistRule { Pattern = "*MACOS*", MatchDirectories = true, MatchFiles = false, Section = currentSection });
                rules.Add(new AiSkiplistRule { Pattern = "*MAC.OSX*", MatchDirectories = true, MatchFiles = false, Section = currentSection });
            }
        }

        // Deduplicate
        return rules.DistinctBy(r => $"{r.Pattern}|{r.Section}").ToList();
    }

    private static string? DetectSectionFromHeader(string header, List<string> sectionNames)
    {
        var lower = header.ToLowerInvariant();
        // Try to match "0Day Section Rules" → find "0DAY" in section names
        foreach (var name in sectionNames)
        {
            var normName = name.ToLowerInvariant().Replace("_", "").Replace("-", "");
            if (lower.Contains(normName)) return name;
            // Also try common aliases
            if (normName.Contains("0day") && lower.Contains("0day")) return name;
            if (normName.Contains("app") && lower.Contains("app")) return name;
            if (normName.Contains("game") && lower.Contains("game")) return name;
            if ((normName.Contains("tv") || normName.Contains("tvsport")) && lower.Contains("tv")) return name;
        }
        return null;
    }

    public void Dispose() => _http.Dispose();
}
