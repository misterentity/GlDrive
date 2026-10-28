using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace GlDrive.AiAgent;

/// <summary>The single boundary for all model request bodies. Authentication headers are separate.</summary>
public static class OutboundPayloadRedactor
{
    public static JsonNode RedactConfig(JsonNode config)
    {
        var copy = config.DeepClone();
        MaskLeaves(copy);
        return copy;
    }

    private static void MaskLeaves(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj.ToArray())
            {
                if (ConfigSecretPointers.IsSecret(pair.Key))
                    obj[pair.Key] = ConfigSecretPointers.Mask(pair.Value?.ToJsonString());
                else MaskLeaves(pair.Value);
            }
        else if (node is JsonArray array)
            foreach (var child in array) MaskLeaves(child);
    }

    public static bool TryPrepare(string payload, out string safePayload)
    {
        try
        {
            safePayload = Prepare(payload, OutboundSecretInventory.Collect());
            return true;
        }
        catch
        {
            // Neither the exception nor the payload is safe to log here.
            Log.Warning("LLM request refused: outbound secret redaction could not be verified");
            safePayload = "";
            return false;
        }
    }

    public static string Prepare(string payload, IEnumerable<string> liveSecrets)
    {
        var secrets = liveSecrets.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length).ToArray();
        var replacements = secrets.SelectMany(s => Variants(s).Select(v => (Value: v, Mask: ConfigSecretPointers.Mask(s)!)))
            .Distinct().OrderByDescending(p => p.Value.Length).ToArray();
        var root = JsonNode.Parse(payload) ?? throw new InvalidOperationException("Empty model payload");
        MaskLeaves(root);
        string Replace(string value)
        {
            foreach (var pair in replacements) value = value.Replace(pair.Value, pair.Mask, StringComparison.Ordinal);
            return value;
        }
        void Walk(JsonNode node)
        {
            if (node is JsonObject obj)
                foreach (var pair in obj.ToArray())
                {
                    if (pair.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) obj[pair.Key] = Replace(text);
                    else if (pair.Value != null) Walk(pair.Value);
                }
            else if (node is JsonArray array)
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue scalar && scalar.TryGetValue<string>(out var text)) array[i] = Replace(text);
                    else if (array[i] != null) Walk(array[i]!);
                }
        }
        Walk(root);
        var result = root.ToJsonString();
        if (replacements.Any(p => result.Contains(p.Value, StringComparison.Ordinal)))
            throw new InvalidOperationException("Model payload failed secret verification");
        return result;
    }

    private static IEnumerable<string> Variants(string value)
    {
        // Prompts may contain a serialized config or telemetry row inside the outer request JSON.
        for (var depth = 0; depth < 4; depth++)
        {
            yield return value;
            value = JsonSerializer.Serialize(value)[1..^1];
        }
    }
}
