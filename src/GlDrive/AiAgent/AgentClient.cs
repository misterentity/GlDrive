using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class AgentRunOutcome
{
    public AgentRunResult? Result { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public double EstimatedCostUsd { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class AgentClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string _model;

    // Reliable JSON-mode fallback for when a free/flaky model returns malformed JSON.
    // Matches AppConfig.ResolveAgentModel default.
    private const string FallbackModel = "anthropic/claude-sonnet-4-6";

    public AgentClient(string apiKey, string model)
    {
        _model = model;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/"),
            Timeout = TimeSpan.FromMinutes(5)
        };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        _http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/misterentity/GlDrive");
        _http.DefaultRequestHeaders.Add("X-Title", "GlDrive Agent");
    }

    public async Task<AgentRunOutcome> RunAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var outcome = await RunInternalAsync(_model, systemPrompt, userPrompt, ct);

        // Free models (notably openai/gpt-oss-120b:free) fail two ways the paid tier doesn't:
        //   1. malformed JSON — unescaped newlines mid-key, stray quotes, unbalanced brackets;
        //   2. HTTP 429 rate-limits — the free quota is exhausted (observed: 5 of 6 scheduled
        //      runs returned 429/day, incl. the 04:00 job, so the agent applied nothing).
        // Either way the run produces no useful output. Auto-retry once with a paid quality
        // model. This keeps "free first" economics — we only spend a paid call when the free
        // model actually fails, not on every run.
        bool freeParseFail = outcome.ErrorMessage == "failed-to-parse-json";
        bool freeRateLimited = outcome.ErrorMessage == "HTTP 429"
            || (outcome.ErrorMessage?.StartsWith("upstream:429", StringComparison.OrdinalIgnoreCase) ?? false);
        if ((freeParseFail || freeRateLimited)
            && _model.Contains(":free", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(_model, FallbackModel, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("AgentClient: free model {Model} {Reason} — retrying with {Fallback}",
                _model, freeRateLimited ? "was rate-limited (HTTP 429)" : "returned malformed JSON", FallbackModel);
            var retry = await RunInternalAsync(FallbackModel, systemPrompt, userPrompt, ct);
            if (retry.Result is not null)
            {
                Log.Information("AgentClient: fallback {Fallback} succeeded after {Primary} {Reason}",
                    FallbackModel, _model, freeRateLimited ? "rate-limit" : "parse failure");
                return retry;
            }
            Log.Warning("AgentClient: fallback {Fallback} also failed: {Err}", FallbackModel, retry.ErrorMessage);
        }

        return outcome;
    }

    private async Task<AgentRunOutcome> RunInternalAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var request = new
        {
            model = model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = 0.2,
            max_tokens = 32000,
            response_format = new { type = "json_object" }
        };

        var body = JsonSerializer.Serialize(request, JsonOpts);
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        try
        {
            var resp = await _http.PostAsync("chat/completions", content, ct);
            var responseBody = await resp.Content.ReadAsStringAsync(ct);
            Log.Information("AgentClient response status={Status} bytes={Bytes} model={Model}",
                resp.StatusCode, responseBody.Length, model);

            if (!resp.IsSuccessStatusCode)
                return new AgentRunOutcome { ErrorMessage = $"HTTP {(int)resp.StatusCode}" };

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            // OpenRouter sometimes returns upstream errors as HTTP 200 with an `error` envelope
            // (free-tier rate limits, provider failures). Surface those instead of crashing on missing `choices`.
            if (root.TryGetProperty("error", out var err))
            {
                var em = err.TryGetProperty("message", out var m) ? m.GetString() : "unknown";
                var code = err.TryGetProperty("code", out var c) ? c.ToString() : "?";
                Log.Warning("AgentClient upstream error code={Code} msg={Msg}", code, em);
                return new AgentRunOutcome { ErrorMessage = $"upstream:{code}:{em}" };
            }

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out var messageEl)
                || !messageEl.TryGetProperty("content", out var contentEl))
            {
                var preview = responseBody.Length > 400 ? responseBody[..400] : responseBody;
                Log.Warning("AgentClient unexpected response shape, body preview: {Preview}", preview);
                return new AgentRunOutcome { ErrorMessage = "malformed-response" };
            }
            var msg = contentEl.GetString() ?? "";

            int inputTok = 0, outputTok = 0;
            if (root.TryGetProperty("usage", out var u))
            {
                if (u.TryGetProperty("prompt_tokens", out var pt)) inputTok = pt.GetInt32();
                if (u.TryGetProperty("completion_tokens", out var ot)) outputTok = ot.GetInt32();
            }

            // Extract the outermost balanced JSON object. Models sometimes wrap the
            // response in markdown fences or add prose; the prior naive IndexOf('{')
            // + LastIndexOf('}') can grab too much (spanning multiple objects).
            var rawJson = ExtractFirstBalancedJsonObject(msg);
            AgentRunResult? result = null;
            if (rawJson != null)
            {
                try { result = JsonSerializer.Deserialize<AgentRunResult>(rawJson, JsonOpts); }
                catch (JsonException ex)
                {
                    Log.Warning("AgentClient JSON parse fail, attempting repair: {Msg}", ex.Message);
                    var repaired = RepairJson(rawJson);
                    if (repaired != null)
                    {
                        try { result = JsonSerializer.Deserialize<AgentRunResult>(repaired, JsonOpts); }
                        catch (Exception ex2) { Log.Warning("AgentClient JSON repair parse also failed: {Msg}", ex2.Message); }
                    }
                }
            }

            if (result is null)
            {
                // Log first 400 chars of the response so the failure is visible in-log without opening a dump file.
                var preview = msg.Length > 400 ? msg[..400].Replace("\n", "\\n") : msg.Replace("\n", "\\n");
                Log.Warning("AgentClient parse failed — response preview (first 400 chars): {Preview}", preview);

                // Persist full raw response for user inspection.
                try
                {
                    var dumpDir = Path.Combine(GlDrive.Config.ConfigManager.AppDataPath, "ai-data");
                    Directory.CreateDirectory(dumpDir);
                    var dumpPath = Path.Combine(dumpDir,
                        $"last-response-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                    File.WriteAllText(dumpPath,
                        $"# model: {model}\n# tokens: {inputTok} in / {outputTok} out\n\n{msg}");
                    Log.Warning("AgentClient parse failed — full response dumped to {Path}", dumpPath);
                }
                catch { /* best-effort */ }
            }

            return new AgentRunOutcome
            {
                Result = result,
                InputTokens = inputTok,
                OutputTokens = outputTok,
                EstimatedCostUsd = EstimateCost(model, inputTok, outputTok),
                ErrorMessage = result is null ? "failed-to-parse-json" : null
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AgentClient call failed");
            return new AgentRunOutcome { ErrorMessage = ex.Message };
        }
    }

    /// <summary>Very rough per-model cost estimator. Revisit when pricing changes.</summary>
    private static double EstimateCost(string model, int inTok, int outTok)
    {
        double ip = 3.0 / 1e6, op = 15.0 / 1e6; // Sonnet default
        if (model.Contains("opus")) { ip = 15.0 / 1e6; op = 75.0 / 1e6; }
        else if (model.Contains("gemini-2.5-pro")) { ip = 1.25 / 1e6; op = 5.0 / 1e6; }
        else if (model.Contains(":free")) { ip = 0; op = 0; }
        return inTok * ip + outTok * op;
    }

    /// <summary>
    /// Attempts to repair a truncated JSON response by trimming at the last complete
    /// array/object boundary and closing any remaining open brackets/braces.
    /// Adapted from OpenRouterClient.RepairTruncatedJson.
    /// </summary>
    /// <summary>
    /// Scans the message for ALL balanced top-level JSON objects and returns the LARGEST.
    /// Models (especially Sonnet) often emit chain-of-thought + multiple draft JSON blocks
    /// before the real payload — taking the first yields a tiny sketch with placeholder
    /// values, and taking "first { to last }" yields invalid concatenated objects.
    /// Largest-by-length is a reliable heuristic since the real response dwarfs any sketch.
    /// Falls back to "first { to last }" for truncation repair if no balanced object found.
    /// </summary>
    internal static string? ExtractFirstBalancedJsonObject(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return null;
        string? best = null;
        int bestLen = 0;
        int start = -1;
        int depth = 0;
        bool inString = false;
        bool escape = false;
        for (int i = 0; i < msg.Length; i++)
        {
            var c = msg[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && inString) { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (c == '}')
            {
                if (depth == 0) continue;
                depth--;
                if (depth == 0 && start >= 0)
                {
                    var candidate = msg[start..(i + 1)];
                    if (candidate.Length > bestLen) { best = candidate; bestLen = candidate.Length; }
                    start = -1;
                }
            }
        }
        if (best != null) return best;
        // No balanced object completed — possibly truncated. Fall back to first { to last } so
        // RepairJson downstream can try to close it.
        int firstBrace = msg.IndexOf('{');
        int lastBrace = msg.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace) return msg[firstBrace..(lastBrace + 1)];
        if (firstBrace >= 0) return msg[firstBrace..];  // totally unclosed — let RepairJson try
        return null;
    }

    private static string? RepairJson(string json)
    {
        try
        {
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
            if (depth <= 0) return null;

            var truncateAt = json.Length;
            for (var i = json.Length - 1; i > lastComplete; i--)
                if (json[i] == ',') { truncateAt = i; break; }
            if (truncateAt == json.Length) truncateAt = lastComplete + 1;

            var repaired = json[..truncateAt];

            int openBraces = 0, openBrackets = 0;
            inString = false; escape = false;
            foreach (var c in repaired)
            {
                if (escape) { escape = false; continue; }
                if (c == '\\' && inString) { escape = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') openBraces++;
                else if (c == '}') openBraces--;
                else if (c == '[') openBrackets++;
                else if (c == ']') openBrackets--;
            }
            for (var i = 0; i < openBrackets; i++) repaired += "]";
            for (var i = 0; i < openBraces; i++) repaired += "}";
            return repaired;
        }
        catch { return null; }
    }

    public void Dispose() => _http.Dispose();
}
