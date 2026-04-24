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
        var request = new
        {
            model = _model,
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
                resp.StatusCode, responseBody.Length, _model);

            if (!resp.IsSuccessStatusCode)
                return new AgentRunOutcome { ErrorMessage = $"HTTP {(int)resp.StatusCode}" };

            using var doc = JsonDocument.Parse(responseBody);
            var msg = doc.RootElement.GetProperty("choices")[0]
                         .GetProperty("message").GetProperty("content").GetString() ?? "";

            int inputTok = 0, outputTok = 0;
            if (doc.RootElement.TryGetProperty("usage", out var u))
            {
                if (u.TryGetProperty("prompt_tokens", out var pt)) inputTok = pt.GetInt32();
                if (u.TryGetProperty("completion_tokens", out var ot)) outputTok = ot.GetInt32();
            }

            var jsonStart = msg.IndexOf('{');
            var jsonEnd = msg.LastIndexOf('}');
            AgentRunResult? result = null;
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var rawJson = msg[jsonStart..(jsonEnd + 1)];
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

            return new AgentRunOutcome
            {
                Result = result,
                InputTokens = inputTok,
                OutputTokens = outputTok,
                EstimatedCostUsd = EstimateCost(_model, inputTok, outputTok),
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
