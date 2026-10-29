using System.IO;
using System.Text;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Services.Automation;
using GlDrive.Util;

namespace GlDrive.Services.Recovery;

internal sealed record RecoveryConfiguration(ConfigurationAutomation Configuration, bool NeedsExplicitRepair);

/// <summary>
/// Offline import can repair malformed settings only after explicit secret replacement.
/// The unreadable source is preserved byte-for-byte before the normal validated save.
/// </summary>
internal static class RecoveryConfigurationFactory
{
    // Call off the dispatcher: discovering corruption and preserving its source require file I/O.
    internal static RecoveryConfiguration Create(AppConfig config, bool offline, bool replaceExistingSecrets = false)
        => Create(config, offline, replaceExistingSecrets, ConfigManager.ConfigPath, ConfigManager.Save);

    internal static RecoveryConfiguration Create(AppConfig config, bool offline, bool replaceExistingSecrets,
        string configPath, Action<AppConfig> persist, Action<string, byte[]>? preserve = null)
    {
        preserve ??= SecureFile.WriteAllBytesRestricted;
        AppConfig ReadCurrent() => File.Exists(configPath) ? Parse(File.ReadAllBytes(configPath)) : config;
        if (!offline)
            return new(new ConfigurationAutomation(config, persist, ReadCurrent), false);

        byte[]? damaged = null;
        SecureFile.WithPathLock(configPath, () =>
        {
            if (!File.Exists(configPath)) return;
            var bytes = File.ReadAllBytes(configPath);
            try { Parse(bytes); }
            catch (JsonException) { damaged = bytes; }
        });
        if (damaged == null)
            return new(new ConfigurationAutomation(config, persist, ReadCurrent), false);

        // Freeze the fallback used for preview/revision checks; never infer that a damaged
        // source has no secrets. Only the explicit replacement path may persist this baseline.
        var fallback = JsonSerializer.SerializeToUtf8Bytes(config, ConfigurationAutomation.Json);
        var repaired = false;
        void VerifyUnchanged()
        {
            if (!File.Exists(configPath) || !File.ReadAllBytes(configPath).AsSpan().SequenceEqual(damaged))
                throw new InvalidOperationException("Settings changed after recovery inspection; inspect them again before importing.");
        }
        AppConfig ReadForRepair()
        {
            AppConfig? current = null;
            SecureFile.WithPathLock(configPath, () =>
            {
                if (repaired) { current = ReadCurrent(); return; }
                VerifyUnchanged();
                current = Parse(fallback);
            });
            return current!;
        }
        void SaveRepair(AppConfig candidate)
        {
            SecureFile.WithPathLock(configPath, () =>
            {
                if (repaired) { persist(candidate); return; }
                if (!replaceExistingSecrets)
                    throw new InvalidOperationException("Damaged settings require explicit replacement of secrets from the backup.");
                if (ConfigManager.ReadOnly)
                    throw new InvalidOperationException("Application settings are read-only.");
                VerifyUnchanged();
                // Unique evidence files never replace an earlier recovery attempt.
                var savedSource = configPath + "." + Guid.NewGuid().ToString("N") + ".pre-recovery";
                preserve(savedSource, damaged);
                if (!File.ReadAllBytes(savedSource).AsSpan().SequenceEqual(damaged))
                    throw new IOException("The damaged settings copy could not be verified.");
                VerifyUnchanged();
                persist(candidate);
                repaired = true;
            });
        }
        return new(new ConfigurationAutomation(config, SaveRepair, ReadForRepair), true);
    }

    private static AppConfig Parse(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return JsonSerializer.Deserialize<AppConfig>(reader.ReadToEnd(), ConfigurationAutomation.Json)
            ?? throw new JsonException("Settings contain no configuration object.");
    }
}
