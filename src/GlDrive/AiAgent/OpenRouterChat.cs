using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace GlDrive.AiAgent;

/// <summary>
/// OpenRouter chat transport shared by the agent loop (<see cref="AgentClient"/>) and the
/// server dialog's AI Setup (<see cref="GlDrive.Spread.OpenRouterClient"/>). Each caller keeps
/// its own policy (prompt, fallback chain, token budget); this owns the HTTP client, the request
/// shape and the response-text helpers. Every request body passes
/// <see cref="OutboundPayloadRedactor"/> inside <see cref="PostAsync"/> — there is no other way
/// to send one, so neither caller can skip redaction.
/// </summary>
internal sealed class OpenRouterChat : IDisposable
{
    internal readonly record struct Reply(HttpStatusCode Status, string Body)
    {
        public bool IsSuccess => (int)Status is >= 200 and <= 299;
    }

    private static readonly JsonSerializerOptions RequestJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Known provider migrations are applied before the first network call. OpenRouter's
    // retired-model 404 is still parsed dynamically for future migrations, but a model we
    // have already observed to be permanently dead must not fail once per app restart
    // before healing to the same successor again.
    private static readonly IReadOnlyDictionary<string, string> KnownModelMigrations =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai/gpt-oss-120b:free"] = "openai/gpt-oss-120b"
        };

    private readonly HttpClient _http;
    private readonly Func<IEnumerable<string>> _liveSecrets;

    /// <param name="liveSecrets">Secret values to mask; defaults to the live inventory. Tests
    /// substitute a fixed list — the redactor itself always runs.</param>
    internal OpenRouterChat(string apiKey, string title, TimeSpan timeout,
        HttpMessageHandler? handler = null, Func<IEnumerable<string>>? liveSecrets = null)
    {
        _liveSecrets = liveSecrets ?? OutboundSecretInventory.Collect;
        _http = new HttpClient(handler ?? new HttpClientHandler())
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/"),
            Timeout = timeout
        };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        _http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/misterentity/GlDrive");
        _http.DefaultRequestHeaders.Add("X-Title", title);
    }

    /// <summary>
    /// Posts one system + user chat completion. Returns null — without sending anything — when
    /// outbound redaction cannot be verified.
    /// </summary>
    internal async Task<Reply?> PostAsync(string model, string systemPrompt, string userPrompt,
        double temperature, int maxTokens, bool jsonMode, CancellationToken ct)
    {
        var request = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature,
            max_tokens = maxTokens,
            response_format = jsonMode ? new { type = "json_object" } : null
        };

        var json = JsonSerializer.Serialize(request, RequestJson);
        if (!OutboundPayloadRedactor.TryPrepare(json, _liveSecrets, out var safeJson)) return null;
        using var content = new StringContent(safeJson, Encoding.UTF8, "application/json");

        using var response = await _http.PostAsync("chat/completions", content, ct);
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    internal static string ResolveKnownModelMigration(string model) =>
        KnownModelMigrations.TryGetValue(model, out var replacement) ? replacement : model;

    /// <summary>Pulls the replacement slug out of a retired-model 404 body.</summary>
    internal static string? TryParseSuggestedModel(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            body, @"use this slug instead:\s*([A-Za-z0-9._\-]+/[A-Za-z0-9._:\-]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.TrimEnd('.', ',', '"') : null;
    }

    /// <summary>
    /// Keep provider diagnostics useful without copying the complete response envelope
    /// into a local log. Provider envelopes can contain account/user/request identifiers;
    /// only the error code and message are needed for model healing and operator action.
    /// </summary>
    internal static string SummarizeErrorBodyForLog(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "empty response body";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var error = root.TryGetProperty("error", out var errorEl) ? errorEl : root;
            var message = error.ValueKind == JsonValueKind.Object &&
                          error.TryGetProperty("message", out var messageEl)
                ? messageEl.GetString()
                : null;
            var code = error.ValueKind == JsonValueKind.Object &&
                       error.TryGetProperty("code", out var codeEl)
                ? codeEl.ToString()
                : null;
            if (!string.IsNullOrWhiteSpace(message))
            {
                var safeMessage = message.Length > 400 ? message[..400] + "…" : message;
                return string.IsNullOrWhiteSpace(code)
                    ? $"message={safeMessage}"
                    : $"code={code} message={safeMessage}";
            }
        }
        catch (JsonException)
        {
            // Deliberately do not echo unstructured provider bodies: unlike a parsed
            // error.message, their contents have no bounded or known-safe shape.
        }
        return $"unstructured response body ({Encoding.UTF8.GetByteCount(body)} bytes)";
    }

    /// <summary>
    /// Scans the message for ALL balanced top-level JSON objects and returns the LARGEST.
    /// Models (especially Sonnet) often emit chain-of-thought + multiple draft JSON blocks
    /// before the real payload — taking the first yields a tiny sketch with placeholder
    /// values, and taking "first { to last }" yields invalid concatenated objects.
    /// Largest-by-length is a reliable heuristic since the real response dwarfs any sketch.
    /// Falls back to "first { to last }" for truncation repair if no balanced object found.
    /// </summary>
    internal static string? ExtractLargestJsonObject(string msg)
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
        // RepairTruncatedJson downstream can try to close it.
        int firstBrace = msg.IndexOf('{');
        int lastBrace = msg.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace) return msg[firstBrace..(lastBrace + 1)];
        if (firstBrace >= 0) return msg[firstBrace..];  // totally unclosed — let the repair try
        return null;
    }

    /// <summary>
    /// Repairs JSON truncated at the token limit: drops the last incomplete element and closes
    /// every open bracket/brace. Null when the input is not truncated (the parse error is
    /// something else) or cannot be repaired.
    /// </summary>
    internal static string? RepairTruncatedJson(string json)
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

    public void Dispose() => _http.Dispose();
}
