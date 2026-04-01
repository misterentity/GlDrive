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
        You are an expert on glftpd FTP server configuration and scene release distribution.
        You analyze SITE RULES output and generate optimal spread/FXP configuration.

        When analyzing rules, extract:
        1. Max upload/download slot limits
        2. Disallowed/nuked release groups → affils list
        3. Denied patterns (dubs, wrong resolution, banned content) → skiplist deny rules
        4. Section-specific rules (TV sections only allow 1080p, etc.)
        5. Any other restrictions that should prevent spreading

        For skiplist rules:
        - Use glob patterns (*, ?) not regex unless necessary
        - Set match_dirs=true for directory-level patterns (release names)
        - Set match_files=true for file-level patterns (extensions)
        - Action should be "Deny" for blocked content
        - Include the section name if the rule only applies to a specific section

        Respond with ONLY a JSON object (no markdown, no explanation outside the JSON):
        {
            "max_upload_slots": <number or null>,
            "max_download_slots": <number or null>,
            "affils": ["GROUP1", "GROUP2"],
            "skiplist": [
                {"pattern": "*dubs*", "is_regex": false, "action": "Deny", "match_dirs": true, "match_files": false, "section": "TV_1080"},
                {"pattern": "*.jpg", "is_regex": false, "action": "Deny", "match_dirs": false, "match_files": true, "section": null}
            ],
            "sections": {"TV_HD": "/incoming/tv-hd"},
            "explanation": "Brief explanation of what was detected and applied"
        }
        """;

    public OpenRouterClient(string apiKey, string model = "openai/gpt-oss-120b:free")
    {
        _model = model;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/"),
            Timeout = TimeSpan.FromSeconds(60)
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
            Analyze this glftpd SITE RULES output and generate spread configuration.
            Use the sample releases to verify your rules make sense for actual content on the server.

            === SITE RULES ===
            {siteRulesRaw[..Math.Min(siteRulesRaw.Length, 3000)]}

            === CURRENT SECTIONS ===
            {string.Join("\n", currentSections.Select(kv => $"{kv.Key}={kv.Value}"))}

            === CURRENT AFFILS ===
            {string.Join(", ", currentAffils)}
            {samplesText}
            Generate the optimal skiplist rules, slot limits, affils, and any section suggestions.
            Ensure suggested sections include paths discovered from the sample data.
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
                max_tokens = 2000
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

    public void Dispose() => _http.Dispose();
}
