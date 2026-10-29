using System.IO;
using GlDrive.Config;
using GlDrive.Irc;
using Meziantou.Framework.Win32;

namespace GlDrive.Services.Recovery;

public sealed record RecoveryInventoryRow(string Kind, string Item, string Presence, DateTime? ObservedAtUtc, string Note);

/// <summary>Local metadata only. Secret values and digests never leave this service.</summary>
public static class RecoveryInventory
{
    public static IReadOnlyList<RecoveryInventoryRow> Read(AppConfig config, string root,
        Func<string, FishKeyStore?>? liveKeys = null)
    {
        try { return ReadCore(config, root, liveKeys); }
        catch
        {
            // A malformed nested settings section must not take down the recovery UI.
            // Unknown metadata is not evidence that a credential is missing.
            return [new("Inventory", "Profile", "Unavailable", null,
                "Settings metadata could not be inspected. The original profile is unchanged; offline import remains available.")];
        }
    }

    private static IReadOnlyList<RecoveryInventoryRow> ReadCore(AppConfig config, string root,
        Func<string, FishKeyStore?>? liveKeys)
    {
        var rows = new List<RecoveryInventoryRow>();
        void Credential(string label, string target)
        {
            if (CredentialStore.AccessDisabled)
            { rows.Add(new("Credential", label, "Unavailable", null, "Credential access is disabled")); return; }
            try
            {
                var entry = CredentialManager.ReadCredential(target);
                rows.Add(new("Credential", label, entry == null ? "Missing" : "Present", null,
                    "Age unknown; inventory never reveals values"));
            }
            catch { rows.Add(new("Credential", label, "Unavailable", null, "Could not read credential metadata")); }
        }
        rows.Add(new("Config secret", "Control API token", string.IsNullOrEmpty(config.ControlApi.Token) ? "Missing" : "Present",
            null, "Original retained for older builds; age unknown"));
        foreach (var server in config.Servers)
        {
            Credential(server.Name + " / FTP", $"GlDrive:{server.Connection.Host}:{server.Connection.Port}:{server.Connection.Username}");
            if (server.Connection.Proxy is { Enabled: true } proxy)
                Credential(server.Name + " / proxy", $"GlDrive:proxy:{proxy.Host}:{proxy.Port}:{proxy.Username}");
            if (server.Irc.Enabled)
                Credential(server.Name + " / IRC", $"GlDrive:irc:{server.Irc.Host}:{server.Irc.Port}:{server.Irc.Nick}");
            foreach (var channel in server.Irc.Channels)
                rows.Add(new("Config secret", server.Name + " / " + channel.Name,
                    string.IsNullOrEmpty(channel.Key) ? "Missing" : "Present", null, "Original retained; age unknown"));
            try
            {
                var store = liveKeys?.Invoke(server.Id);
                if (store?.RecoveryRequired == true || store?.PersistenceHealthy == false)
                    rows.Add(new("FiSH storage", server.Name, "Needs review", null, "Damaged storage was quarantined or invalid entries skipped; available keys remain usable"));
                var keys = store?.GetAllKeys() ?? PortableBackupService.ReadFishFile(server.Id);
                foreach (var entry in keys)
                    rows.Add(FishKeyStore.ValidEntry(entry.Key, entry.Value)
                        ? new("FiSH key", server.Name + " / " + entry.Key, "Present", entry.Value.SetAt, "Key set time")
                        : new("FiSH key", server.Name, "Invalid entry skipped", null, "Other valid keys remain usable"));
            }
            catch { rows.Add(new("FiSH keys", server.Name, "Unavailable", null, "Unreadable protected store; original preserved")); }
        }
        foreach (var api in new[] { "omdb", "tmdb", "openrouter", "plex" }) Credential(api, "GlDrive:api:" + api);
        try
        {
            if (Directory.Exists(root))
                foreach (var path in Directory.EnumerateFiles(root).Where(IsRecoveryFile).Take(200))
                    rows.Add(new("Recovery file", Path.GetFileName(path), "Present", File.GetLastWriteTimeUtc(path),
                        path.EndsWith("config-secrets.dpapi", StringComparison.OrdinalIgnoreCase)
                            ? "Protected copy timestamp, not secret rotation time" : "Preserved locally; no automatic restore or deletion"));
        }
        catch { rows.Add(new("Recovery files", "Profile", "Unavailable", null, "Could not inspect local recovery metadata")); }
        return rows;
    }

    internal static bool IsRecoveryFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.Contains(".corrupt", StringComparison.OrdinalIgnoreCase) || name.Contains(".last-good.", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pending", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".shrink-rejected", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".pre-import", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".pre-recovery", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".pre-downgrade", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("config-secrets.dpapi", StringComparison.OrdinalIgnoreCase);
    }
}
