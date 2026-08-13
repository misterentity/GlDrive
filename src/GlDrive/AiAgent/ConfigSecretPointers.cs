using System;
using System.Security.Cryptography;
using System.Text;

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
    /// </summary>
    public static string? Mask(string? value)
    {
        if (value == null) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "sha256:" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
