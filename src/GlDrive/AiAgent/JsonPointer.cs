using System.Text.Json.Nodes;

namespace GlDrive.AiAgent;

public static class JsonPointer
{
    public static string Escape(string token)
        => token.Replace("~", "~0").Replace("/", "~1");

    public static string Unescape(string token)
        => token.Replace("~1", "/").Replace("~0", "~");

    public static string[] Split(string pointer)
    {
        if (string.IsNullOrEmpty(pointer)) return Array.Empty<string>();
        if (pointer[0] != '/') throw new ArgumentException("JSON Pointer must start with /");
        return pointer[1..].Split('/').Select(Unescape).ToArray();
    }

    public static JsonNode? Resolve(JsonNode root, string pointer)
    {
        if (string.IsNullOrEmpty(pointer)) return root;
        JsonNode? cur = root;
        foreach (var tok in Split(pointer))
        {
            if (cur is JsonObject obj) cur = obj.TryGetPropertyValue(tok, out var v) ? v : null;
            else if (cur is JsonArray arr && int.TryParse(tok, out var i) && i >= 0 && i < arr.Count)
                cur = arr[i];
            else return null;
            if (cur is null) return null;
        }
        return cur;
    }

    /// <summary>
    /// Returns true if `maybeAncestor` is an ancestor of `path` (or equal to it).
    /// E.g., IsAncestorOrSelf("/servers/srv-a", "/servers/srv-a/spread/maxSlots") == true.
    /// IsAncestorOrSelf("/servers/srv-a", "/servers/srv-abc") == false (prefix match but different segment).
    /// </summary>
    public static bool IsAncestorOrSelf(string maybeAncestor, string path)
        => path == maybeAncestor || path.StartsWith(maybeAncestor + "/", StringComparison.Ordinal);
}
