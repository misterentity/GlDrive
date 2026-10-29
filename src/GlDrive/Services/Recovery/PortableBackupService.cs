using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Services.Automation;
using GlDrive.Util;
using Meziantou.Framework.Win32;

namespace GlDrive.Services.Recovery;

public sealed record PortableBackupSummary(DateTimeOffset CreatedUtc, int Servers, int FishKeys, int Credentials, string Digest = "", int SkippedFishKeys = 0);
public sealed record PortableImportResult(int ImportedSecrets, int PreservedSecrets, int FailedSecrets, bool RestartRequired = true);

/// <summary>Explicit local recovery only. File/KDF work runs off the dispatcher; restored settings use the normal validator.</summary>
public sealed class PortableBackupService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConfigurationAutomation _configuration;
    private readonly Func<bool> _canImport;
    private readonly Func<string, IReadOnlyDictionary<string, FishKeyEntry>> _readFish;
    private readonly Func<string, FishKeyStore> _fishStore;
    private readonly IPortableCredentialAccess _credentials;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PortableBackupService(AppConfig config, ConfigurationAutomation configuration, Func<bool> canImport,
        Func<string, IReadOnlyDictionary<string, FishKeyEntry>>? readFish = null)
        : this(config, configuration, canImport, readFish ?? ReadFishFile, id => new FishKeyStore(id), new PortableCredentialAccess()) { }

    internal PortableBackupService(AppConfig config, ConfigurationAutomation configuration, Func<bool> canImport,
        Func<string, IReadOnlyDictionary<string, FishKeyEntry>> readFish, Func<string, FishKeyStore> fishStore,
        IPortableCredentialAccess credentials)
    { _configuration = configuration; _canImport = canImport; _readFish = readFish; _fishStore = fishStore; _credentials = credentials; }

    public async Task<PortableBackupSummary> ExportAsync(string path, string passphrase, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var config = _configuration.CurrentSnapshot().AsObject();
                var typed = config.Deserialize<AppConfig>(ConfigurationAutomation.Json) ?? throw new InvalidDataException("Settings unavailable.");
                var payload = new PortableBackupPayload { Config = config };
                var skippedFishKeys = 0;
                foreach (var server in typed.Servers)
                {
                    ValidateServerId(server.Id);
                    foreach (var (target, key) in _readFish(server.Id))
                    {
                        if (!ExportableFishEntry(target, key)) { skippedFishKeys++; continue; }
                        payload.Fish.Add(new PortableFishSecret { ServerId = server.Id, Target = target, Entry = key });
                    }
                }
                foreach (var descriptor in Descriptors(typed))
                {
                    var value = _credentials.Read(descriptor.Target);
                    if (!string.IsNullOrEmpty(value)) payload.Credentials.Add(new PortableCredentialSecret
                        { Kind = descriptor.Kind, ServerId = descriptor.ServerId, Service = descriptor.Service, Value = value });
                }
                if (!JsonNode.DeepEquals(config, _configuration.CurrentSnapshot()))
                    throw new InvalidOperationException("Settings changed while the backup was being captured. Retry the export.");
                Validate(payload);
                var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
                try
                {
                    var encrypted = PortableBackupProtection.Encrypt(plaintext, passphrase);
                    ct.ThrowIfCancellationRequested();
                    return WriteNew(path, encrypted, passphrase, ct) with { SkippedFishKeys = skippedFishKeys };
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task<PortableBackupSummary> InspectAsync(string path, string passphrase, CancellationToken ct = default)
        => Task.Run(() => Summary(Read(path, passphrase, ct)), ct);

    public async Task<PortableImportResult> ImportAsync(string path, string passphrase,
        bool replaceExistingSecrets = false, CancellationToken ct = default, string? expectedDigest = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_canImport()) throw new InvalidOperationException("Import requires offline Recovery mode. Restart into Recovery first.");
            var payload = await Task.Run(() => Read(path, passphrase, ct), ct).ConfigureAwait(false);
            if (expectedDigest != null && !string.Equals(expectedDigest, payload.Digest, StringComparison.Ordinal))
                throw new InvalidOperationException("The backup changed after inspection. Inspect it again before importing.");
            return await ImportPayloadAsync(payload, replaceExistingSecrets, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal async Task<PortableImportResult> ImportPayloadAsync(PortableBackupPayload payload, bool replaceExistingSecrets, CancellationToken ct)
    {
        Validate(payload);
        if (!_canImport()) throw new InvalidOperationException("Import requires offline Recovery mode.");
        ct.ThrowIfCancellationRequested();
        var before = _configuration.CurrentSnapshot().AsObject();
        var candidate = PrepareConfiguration(payload.Config, before, replaceExistingSecrets);
        if (!replaceExistingSecrets)
        {
            // A configured bracket key would be re-applied at the next IRC join, replacing even
            // a newer protected key. Keep the complete existing protected entry authoritative.
            var localServers = before["servers"]!.AsArray().OfType<JsonObject>().ToDictionary(s => s["id"]!.GetValue<string>(), StringComparer.Ordinal);
            foreach (var server in candidate["servers"]!.AsArray().OfType<JsonObject>())
            {
                var id = server["id"]!.GetValue<string>();
                if (!localServers.TryGetValue(id, out var local)) continue;
                var existing = _readFish(id);
                foreach (var channel in server["irc"]!["channels"]!.AsArray().OfType<JsonObject>())
                {
                    var configured = channel["key"]?.GetValue<string>();
                    var currentChannel = local["irc"]!["channels"]!.AsArray().OfType<JsonObject>().FirstOrDefault(c =>
                        string.Equals(c["name"]?.GetValue<string>(), channel["name"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase));
                    if (configured is { Length: > 2 } && configured.StartsWith('[') && configured.EndsWith(']') &&
                        string.IsNullOrEmpty(currentChannel?["key"]?.GetValue<string>()) &&
                        existing.Keys.Any(target => target.Equals(channel["name"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)))
                        channel["key"] = "";
                }
            }
        }
        var importedConfig = payload.Config.Deserialize<AppConfig>(ConfigurationAutomation.Json)!;
        var candidateConfig = candidate.Deserialize<AppConfig>(ConfigurationAutomation.Json)!;
        ConfigurationAutomation.Validate(candidateConfig);
        ValidateServerIds(candidateConfig);
        var sourceDescriptors = Descriptors(importedConfig).ToDictionary(d => d.Identity);
        var targetDescriptors = Descriptors(candidateConfig).ToDictionary(d => d.Identity);

        // Read all relevant vault entries before any mutation. An unavailable vault is never mistaken for an empty one.
        var credentials = payload.Credentials.Select(secret =>
        {
            var source = sourceDescriptors[secret.Identity];
            var target = targetDescriptors.GetValueOrDefault(secret.Identity);
            var matches = target != null && source.Target == target.Target;
            return (Secret: secret, Descriptor: target, Matches: matches, Current: matches ? _credentials.Read(target!.Target) : null);
        }).ToArray();
        var operations = new JsonArray(candidate.Select(p => (JsonNode?)new JsonObject
            { ["op"] = "replace", ["path"] = "/" + JsonPointer.Escape(p.Key), ["value"] = p.Value?.DeepClone() }).ToArray());
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(before.ToJsonString()))).ToLowerInvariant();
        var args = JsonSerializer.SerializeToElement(new { operations, allowSecrets = true, expectedRevision = revision }, Json);
        using var attribution = ConfigWriteScope.Begin("local:portable-backup-import");
        // A failed validated settings save leaves every credential and FiSH entry untouched.
        await _configuration.ExecuteAsync("config_patch", args, ct).ConfigureAwait(false);
        var imported = 0; var preserved = 0; var failed = 0;
        foreach (var item in credentials)
        {
            ct.ThrowIfCancellationRequested();
            if (!item.Matches || (!replaceExistingSecrets && !string.IsNullOrEmpty(item.Current))) { preserved++; continue; }
            try
            {
                // Repeat the read at the write boundary in case another local process saved a credential meanwhile.
                if (!replaceExistingSecrets && !string.IsNullOrEmpty(_credentials.Read(item.Descriptor!.Target))) { preserved++; continue; }
                _credentials.WriteVerified(item.Descriptor!.Target, item.Descriptor.User, item.Secret.Value);
                imported++;
            }
            catch { failed++; }
        }
        foreach (var group in payload.Fish.GroupBy(f => f.ServerId))
        {
            var source = importedConfig.Servers.Single(s => s.Id == group.Key);
            var destination = candidateConfig.Servers.Single(s => s.Id == group.Key);
            if (!SameIrcIdentity(source, destination)) { preserved += group.Count(); continue; }
            FishKeyStore store;
            try { store = _fishStore(group.Key); }
            catch { failed += group.Count(); continue; }
            var entries = new Dictionary<string, FishKeyEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in group)
            {
                ct.ThrowIfCancellationRequested();
                if (!replaceExistingSecrets && store.GetKey(item.Target) != null) { preserved++; continue; }
                entries[item.Target] = item.Entry;
            }
            if (entries.Count == 0) continue;
            // One verified write and one immutable pre-import copy for this entire server.
            if (store.TryImportVerified(entries, replaceExistingSecrets)) imported += entries.Count;
            else failed += entries.Count;
        }
        return new(imported, preserved, failed);
    }

    internal static JsonObject PrepareConfiguration(JsonObject backup, JsonObject current, bool replaceSecrets)
    {
        var candidate = backup.DeepClone().AsObject();
        // Portable restore never installs executable hooks or resurrects an old security policy.
        candidate["hooks"] = current["hooks"]?.DeepClone();
        candidate["controlApi"] = current["controlApi"]?.DeepClone();
        var token = backup["controlApi"]?["token"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(token) && (replaceSecrets || string.IsNullOrEmpty(current["controlApi"]?["token"]?.GetValue<string>())))
            candidate["controlApi"]!["token"] = token;
        var incoming = candidate["servers"]!.AsArray();
        foreach (var localNode in current["servers"]!.AsArray())
        {
            var local = localNode!.AsObject();
            var match = incoming.OfType<JsonObject>().SingleOrDefault(s => s["id"]!.GetValue<string>() == local["id"]!.GetValue<string>());
            if (match == null) { incoming.Add(local.DeepClone()); continue; }
            if (replaceSecrets) continue;
            if (!SameIrcIdentity(local.Deserialize<ServerConfig>(ConfigurationAutomation.Json)!, match.Deserialize<ServerConfig>(ConfigurationAutomation.Json)!))
            {
                incoming[incoming.IndexOf(match)] = local.DeepClone();
                continue;
            }
            var channels = match["irc"]!["channels"]!.AsArray();
            foreach (var localChannel in local["irc"]!["channels"]!.AsArray().OfType<JsonObject>())
            {
                if (string.IsNullOrEmpty(localChannel["key"]?.GetValue<string>())) continue;
                var channel = channels.OfType<JsonObject>().SingleOrDefault(c => string.Equals(c["name"]?.GetValue<string>(), localChannel["name"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase));
                if (channel == null) channels.Add(localChannel.DeepClone());
                else channel["key"] = localChannel["key"]!.DeepClone();
            }
        }
        return candidate;
    }

    private static bool SameIrcIdentity(ServerConfig first, ServerConfig second) =>
        first.Irc.Host.Equals(second.Irc.Host, StringComparison.OrdinalIgnoreCase) && first.Irc.Port == second.Irc.Port;

    private static PortableBackupPayload Read(string path, string passphrase, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > PortableBackupProtection.MaxPlaintextBytes + 60) throw new InvalidDataException("Backup exceeds the size limit.");
        var envelope = new byte[checked((int)file.Length)];
        file.ReadExactly(envelope);
        var plaintext = PortableBackupProtection.Decrypt(envelope, passphrase);
        try
        {
            var payload = JsonSerializer.Deserialize<PortableBackupPayload>(plaintext, Json) ?? throw new InvalidDataException("Backup is empty.");
            Validate(payload);
            payload.Digest = Convert.ToHexString(SHA256.HashData(envelope));
            return payload;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static void Validate(PortableBackupPayload payload)
    {
        if (payload.Version != 1 || payload.Config == null || payload.Fish == null || payload.Credentials == null ||
            payload.Fish.Count > 10000 || payload.Credentials.Count > 2000)
            throw new InvalidDataException("Unsupported or malformed backup content.");
        var config = payload.Config.Deserialize<AppConfig>(ConfigurationAutomation.Json) ?? throw new InvalidDataException("Backup settings are empty.");
        ConfigurationAutomation.Validate(config);
        ValidateServerIds(config);
        var ids = config.Servers.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var fishIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fish in payload.Fish)
            if (fish == null || !ids.Contains(fish.ServerId) || !ExportableFishEntry(fish.Target, fish.Entry) ||
                !fishIds.Add(fish.ServerId + "\n" + fish.Target))
                throw new InvalidDataException("Invalid or duplicate FiSH record in backup.");
        var descriptors = Descriptors(config).ToDictionary(d => d.Identity, StringComparer.Ordinal);
        var allowed = descriptors.Keys.ToHashSet(StringComparer.Ordinal);
        var credentialIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var secret in payload.Credentials)
            if (secret == null || !allowed.Contains(secret.Identity) || !credentialIds.Add(secret.Identity) ||
                string.IsNullOrEmpty(secret.Value) || secret.Value.Length > 65536)
                throw new InvalidDataException("Invalid or duplicate credential record in backup.");
        foreach (var group in payload.Credentials.GroupBy(secret => descriptors[secret.Identity].Target, StringComparer.OrdinalIgnoreCase))
            if (group.Select(secret => secret.Value).Distinct(StringComparer.Ordinal).Skip(1).Any())
                throw new InvalidDataException("Backup contains conflicting values for the same credential target.");
    }

    private static bool ExportableFishEntry(string target, FishKeyEntry? entry) => FishKeyStore.ValidEntry(target, entry) &&
        target.Length <= 256 && !target.Any(char.IsControl) && entry!.Key.Length <= 65536 &&
        entry.AltKey is { Length: <= 65536 } && entry.DhSecretHex is { Length: <= 65536 };

    private static void ValidateServerIds(AppConfig config)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in config.Servers)
        {
            ValidateServerId(server.Id);
            if (!ids.Add(server.Id)) throw new InvalidDataException("Backup server identifiers collide on the local filesystem.");
        }
    }

    private static void ValidateServerId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id is "." or ".." || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Backup contains an unsafe server identifier.");
    }

    private static IEnumerable<PortableCredentialDescriptor> Descriptors(AppConfig config)
    {
        foreach (var server in config.Servers)
        {
            var c = server.Connection;
            yield return new("ftp", server.Id, "", $"GlDrive:{c.Host}:{c.Port}:{c.Username}", c.Username);
            var irc = server.Irc;
            yield return new("irc", server.Id, "", $"GlDrive:irc:{irc.Host}:{irc.Port}:{irc.Nick}", irc.Nick);
            if (c.Proxy is { } proxy) yield return new("proxy", server.Id, "", $"GlDrive:proxy:{proxy.Host}:{proxy.Port}:{proxy.Username}", proxy.Username);
        }
        foreach (var service in new[] { "omdb", "tmdb", "openrouter", "plex" }.Concat(config.Alerts.Targets.Select(t => "alerts-" + t.Id)).Distinct())
            yield return new("api", "", service, "GlDrive:api:" + service, service);
    }

    internal static IReadOnlyDictionary<string, FishKeyEntry> ReadFishFile(string serverId)
    {
        ValidateServerId(serverId);
        var path = Path.Combine(ConfigManager.AppDataPath, $"fish-keys-{serverId}.json");
        if (!File.Exists(path) && File.Exists(path + ".pending")) path += ".pending";
        byte[] bytes;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > PortableBackupProtection.MaxPlaintextBytes) throw new InvalidDataException("FiSH store exceeds the backup size limit.");
            bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
        }
        catch (FileNotFoundException) { return new Dictionary<string, FishKeyEntry>(); }
        catch (DirectoryNotFoundException) { return new Dictionary<string, FishKeyEntry>(); }
        byte[] plaintext;
        try { plaintext = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { plaintext = bytes; }
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(plaintext).TrimStart('\uFEFF'));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("FiSH store is empty.");
            var entries = new Dictionary<string, FishKeyEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.RootElement.EnumerateObject())
            {
                try { entries[item.Name] = item.Value.Deserialize<FishKeyEntry>(Json) ?? new(); }
                catch (JsonException) { entries[item.Name] = new(); }
            }
            return entries;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static PortableBackupSummary Summary(PortableBackupPayload payload) =>
        new(payload.CreatedUtc, payload.Config["servers"]!.AsArray().Count, payload.Fish.Count, payload.Credentials.Count, payload.Digest);

    private static PortableBackupSummary WriteNew(string path, byte[] bytes, string passphrase, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var verifiedCandidate = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            SecureFile.RestrictFilePermissions(temporary);
            // Authenticate and validate the actual disk candidate before publishing the backup.
            var verified = Read(temporary, passphrase, ct);
            if (verified.Digest != Convert.ToHexString(SHA256.HashData(bytes))) throw new IOException("Backup readback differs from the written candidate.");
            verifiedCandidate = true;
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
            return Summary(verified);
        }
        finally { if (verifiedCandidate && File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class PortableBackupPayload
{
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public JsonObject Config { get; set; } = new();
    public List<PortableFishSecret> Fish { get; set; } = [];
    public List<PortableCredentialSecret> Credentials { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public string Digest { get; set; } = "";
}

internal sealed class PortableFishSecret
{
    public string ServerId { get; set; } = "";
    public string Target { get; set; } = "";
    public FishKeyEntry Entry { get; set; } = new();
}

internal sealed class PortableCredentialSecret
{
    public string Kind { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string Service { get; set; } = "";
    public string Value { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string Identity => Kind + "\n" + ServerId + "\n" + Service;
}

internal sealed record PortableCredentialDescriptor(string Kind, string ServerId, string Service, string Target, string User)
{
    public string Identity => Kind + "\n" + ServerId + "\n" + Service;
}

internal interface IPortableCredentialAccess
{
    string? Read(string target);
    void WriteVerified(string target, string user, string value);
}

internal sealed class PortableCredentialAccess : IPortableCredentialAccess
{
    public string? Read(string target)
    {
        if (CredentialStore.AccessDisabled) throw new InvalidOperationException("Credential access is unavailable in this process.");
        return CredentialManager.ReadCredential(target)?.Password;
    }

    public void WriteVerified(string target, string user, string value)
    {
        if (CredentialStore.AccessDisabled) throw new InvalidOperationException("Credential access is unavailable in this process.");
        using var changed = GlDrive.Logging.DiagnosticRedactorCache.BeginInputChange();
        CredentialManager.WriteCredential(target, user, value, CredentialPersistence.Enterprise);
        if (!string.Equals(Read(target), value, StringComparison.Ordinal)) throw new IOException("Credential readback verification failed.");
    }
}
