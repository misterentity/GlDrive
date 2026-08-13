using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace GlDrive.AiAgent;

/// <summary>
/// Decides whether a JSON pointer into appsettings.json addresses a secret.
///
/// ConfigManager.Save diffs the old and new config and records every changed scalar to
/// ai-data/overrides-{date}.jsonl. Without this, changing a password, a FiSH channel key
/// or an API key wrote the plaintext value to disk.
///
/// The rule keys on the LEAF NAME's suffix rather than a list of pointers observed to
/// carry secrets: config grows, and an enumeration of known cases silently fails to cover
/// the field somebody adds next month.
/// </summary>
public static class ConfigSecretPointers
{
    private static readonly string[] SecretLeafSuffixes =
        ["password", "passphrase", "token", "secret", "apikey", "key"];

    /// <summary>True when the pointer's last segment names a credential.</summary>
    public static bool IsSecret(string? jsonPointer)
    {
        if (string.IsNullOrEmpty(jsonPointer)) return false;

        var lastSlash = jsonPointer.LastIndexOf('/');
        var leaf = lastSlash >= 0 ? jsonPointer[(lastSlash + 1)..] : jsonPointer;
        return IsSecretLeaf(leaf);
    }

    private static bool IsSecretLeaf(string leaf)
    {
        if (leaf.Length == 0) return false;

        foreach (var suffix in SecretLeafSuffixes)
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// Replaces a value with a stable digest. Stable so the agent can still see THAT a
    /// field changed; one-way so it cannot see to what. Null passes through, keeping
    /// "added" and "removed" distinguishable from "changed".
    ///
    /// Threat model: this digest is unsalted, truncated SHA-256. It detects change and keeps
    /// plaintext out of the telemetry file — it is NOT a defence against an offline
    /// dictionary attack on a short secret. Don't rely on it to withstand a determined
    /// attacker who already has the overrides file; rely on it only to keep that file from
    /// being a plaintext credential dump in the first place.
    /// </summary>
    public static string? Mask(string? value)
    {
        if (value == null) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "sha256:" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Masks a diff value. A secret-pointed value is masked whole; an object or array value
    /// is masked leaf-by-leaf, because ConfigDiff emits a whole subtree as one blob when a
    /// node is added or removed and the pointer's leaf is then an array index, not a field
    /// name (e.g. adding an IRC channel emits pointer ".../channels/1" with the whole
    /// {"name":...,"key":...} object as the value — IsSecret(pointer) alone can't see the
    /// "key" field inside it). Non-JSON scalars pass through.
    /// </summary>
    public static string? MaskValue(string? jsonPointer, string? value)
    {
        if (value == null) return null;
        if (IsSecret(jsonPointer)) return Mask(value);

        try
        {
            var node = JsonNode.Parse(value);
            if (node is JsonObject or JsonArray)
            {
                MaskSecretLeaves(node);
                return node.ToJsonString();
            }
        }
        catch
        {
            // Not JSON (or malformed) — pass through unchanged rather than throw.
        }

        return value;
    }

    /// <summary>Walks an object/array tree in place, masking any property whose NAME is a secret leaf.</summary>
    private static void MaskSecretLeaves(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (IsSecretLeaf(key))
                        obj[key] = Mask(obj[key]?.ToJsonString());
                    else
                        MaskSecretLeaves(obj[key]);
                }
                break;

            case JsonArray arr:
                foreach (var item in arr)
                    MaskSecretLeaves(item);
                break;
        }
    }
}
