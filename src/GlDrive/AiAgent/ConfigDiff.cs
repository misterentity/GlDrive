using System.Text.Json.Nodes;

namespace GlDrive.AiAgent;

public static class ConfigDiff
{
    /// <summary>Emits (jsonPointer, beforeValue, afterValue) for every scalar-leaf change.</summary>
    public static IEnumerable<(string pointer, string? before, string? after)> Diff(JsonNode? before, JsonNode? after, string pointer = "")
    {
        if (before is null && after is null) yield break;
        if (before is null) { yield return (pointer, null, after!.ToJsonString()); yield break; }
        if (after is null)  { yield return (pointer, before.ToJsonString(), null); yield break; }

        if (before is JsonObject bo && after is JsonObject ao)
        {
            var keys = new HashSet<string>(bo.Select(kv => kv.Key).Concat(ao.Select(kv => kv.Key)));
            foreach (var k in keys)
                foreach (var d in Diff(bo.ContainsKey(k) ? bo[k] : null, ao.ContainsKey(k) ? ao[k] : null, $"{pointer}/{EscapePointer(k)}"))
                    yield return d;
            yield break;
        }
        if (before is JsonArray ba && after is JsonArray aa)
        {
            var max = Math.Max(ba.Count, aa.Count);
            for (int i = 0; i < max; i++)
                foreach (var d in Diff(i < ba.Count ? ba[i] : null, i < aa.Count ? aa[i] : null, $"{pointer}/{i}"))
                    yield return d;
            yield break;
        }

        var beforeStr = before.ToJsonString();
        var afterStr  = after.ToJsonString();
        if (beforeStr != afterStr)
            yield return (pointer, beforeStr, afterStr);
    }

    private static string EscapePointer(string s) => s.Replace("~", "~0").Replace("/", "~1");
}
