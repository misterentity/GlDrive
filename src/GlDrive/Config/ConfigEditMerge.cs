using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlDrive.Config;

/// <summary>Preserves settings published while a detached editor was open.</summary>
internal static class ConfigEditMerge
{
    internal static T Merge<T>(T baseline, T edited, T current) where T : class
        => MergeNode(JsonSerializer.SerializeToNode(baseline), JsonSerializer.SerializeToNode(edited),
            JsonSerializer.SerializeToNode(current), "")!.Deserialize<T>()!;

    private static JsonNode? MergeNode(JsonNode? baseline, JsonNode? edited, JsonNode? current, string path)
    {
        if (JsonNode.DeepEquals(baseline, edited)) return current?.DeepClone();
        if (JsonNode.DeepEquals(baseline, current) || JsonNode.DeepEquals(edited, current)) return edited?.DeepClone();
        if (baseline is JsonObject before && edited is JsonObject after && current is JsonObject live)
        {
            var merged = new JsonObject();
            foreach (var key in before.Select(p => p.Key).Union(after.Select(p => p.Key)).Union(live.Select(p => p.Key)))
                merged[key] = MergeNode(before[key], after[key], live[key], path + "/" + key);
            return merged;
        }
        // Arrays are edited as units: merging indexes after insertion/removal can
        // silently change which rule is being edited. Ask the operator to reopen.
        throw new InvalidOperationException($"Settings changed while this editor was open ({path}). Reopen it before saving.");
    }
}
