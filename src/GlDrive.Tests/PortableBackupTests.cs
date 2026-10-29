using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Services.Automation;
using GlDrive.Services.Recovery;
using Xunit;

namespace GlDrive.Tests;

public sealed class PortableBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "portable-backup-" + Guid.NewGuid().ToString("N"));
    public PortableBackupTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonObject Node(AppConfig config) => JsonSerializer.SerializeToNode(config, Json)!.AsObject();
    private static AppConfig Config() => new()
    {
        Servers = [new() { Id = "fixture", Connection = new() { Host = "ftp.example.invalid", Username = "fixture" },
            Irc = new() { Host = "irc.example.invalid", Channels = [new() { Name = "#one", Key = "current-channel-secret" }] } }],
        ControlApi = new() { Token = new string('a', 64) }
    };

    [Fact]
    public void Envelope_roundtrips_without_persisting_passphrase_or_plaintext()
    {
        const string passphrase = "separate long fixture passphrase";
        var plain = Encoding.UTF8.GetBytes("private fixture secret value");
        var first = PortableBackupProtection.Encrypt(plain, passphrase);
        var second = PortableBackupProtection.Encrypt(plain, passphrase);
        Assert.Equal(plain, PortableBackupProtection.Decrypt(first, passphrase));
        Assert.NotEqual(first, second);
        Assert.DoesNotContain(passphrase, Encoding.UTF8.GetString(first));
        Assert.DoesNotContain("private fixture secret value", Encoding.UTF8.GetString(first));
    }

    [Fact]
    public void Wrong_passphrase_and_modified_payload_or_header_are_rejected()
    {
        var encrypted = PortableBackupProtection.Encrypt("payload"u8, "test passphrase");
        Assert.Throws<InvalidDataException>(() => PortableBackupProtection.Decrypt(encrypted, "wrong passphrase"));
        encrypted[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => PortableBackupProtection.Decrypt(encrypted, "test passphrase"));
        encrypted[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => PortableBackupProtection.Decrypt(encrypted, "test passphrase"));
        Assert.Throws<InvalidDataException>(() => PortableBackupProtection.Decrypt(new byte[10], "test passphrase"));
    }

    [Fact]
    public void Existing_secrets_follow_server_and_channel_identity_not_array_position()
    {
        var local = Config();
        local.Servers[0].Irc.Channels.Add(new() { Name = "#two", Key = "second-local" });
        var backup = Config();
        backup.ControlApi.Token = new string('b', 64);
        backup.Servers[0].Irc.Channels = [new() { Name = "#two", Key = "old-second" }, new() { Name = "#one", Key = "old-first" }];
        var merged = PortableBackupService.PrepareConfiguration(Node(backup), Node(local), false).Deserialize<AppConfig>(Json)!;
        Assert.Equal(local.ControlApi.Token, merged.ControlApi.Token);
        Assert.Equal("second-local", merged.Servers[0].Irc.Channels[0].Key);
        Assert.Equal("current-channel-secret", merged.Servers[0].Irc.Channels[1].Key);
        var replaced = PortableBackupService.PrepareConfiguration(Node(backup), Node(local), true).Deserialize<AppConfig>(Json)!;
        Assert.Equal(backup.ControlApi.Token, replaced.ControlApi.Token);
        Assert.Equal("old-second", replaced.Servers[0].Irc.Channels[0].Key);
    }

    [Fact]
    public void Missing_local_servers_and_channels_are_retained_and_security_policy_is_current()
    {
        var local = Config();
        local.ControlApi.Port = 9000;
        local.Servers.Add(new() { Id = "local-only" });
        var backup = Config();
        backup.Servers[0].Irc.Channels.Clear();
        var merged = PortableBackupService.PrepareConfiguration(Node(backup), Node(local), false).Deserialize<AppConfig>(Json)!;
        Assert.Equal(9000, merged.ControlApi.Port);
        Assert.Equal(2, merged.Servers.Count);
        Assert.Equal("current-channel-secret", Assert.Single(merged.Servers[0].Irc.Channels).Key);
        backup.Servers[0].Irc.Host = "different.example.invalid";
        merged = PortableBackupService.PrepareConfiguration(Node(backup), Node(local), false).Deserialize<AppConfig>(Json)!;
        Assert.Equal(local.Servers[0].Irc.Host, merged.Servers[0].Irc.Host);
    }

    [Fact]
    public async Task Import_is_validated_attributed_idempotent_and_preserves_existing_vault_secrets()
    {
        var local = Config(); var vault = new FakeCredentials();
        vault.Values["GlDrive:api:tmdb"] = "newer-local";
        string? actor = null; var writes = 0;
        var configuration = new ConfigurationAutomation(local, _ => { actor = ConfigWriteScope.Current.Actor; writes++; });
        var service = Create(local, configuration, vault);
        var payload = new PortableBackupPayload { Config = Node(local), Credentials =
            [new() { Kind = "api", Service = "tmdb", Value = "old-secret" }, new() { Kind = "api", Service = "omdb", Value = "missing-secret" }] };
        payload.Config["downloads"]!["qualityDefault"] = "2160p";
        var result = await service.ImportPayloadAsync(payload, false, default);
        Assert.Equal("local:portable-backup-import", actor);
        Assert.Equal(1, writes);
        Assert.Equal(1, result.ImportedSecrets);
        Assert.Equal(1, result.PreservedSecrets);
        Assert.Equal("newer-local", vault.Values["GlDrive:api:tmdb"]);
        result = await service.ImportPayloadAsync(payload, false, default);
        Assert.Equal(0, result.ImportedSecrets);
        Assert.Equal(2, result.PreservedSecrets);
        result = await service.ImportPayloadAsync(payload, true, default);
        Assert.Equal(2, result.ImportedSecrets);
        Assert.Equal("old-secret", vault.Values["GlDrive:api:tmdb"]);
    }

    [Fact]
    public async Task Unavailable_vault_or_invalid_payload_changes_nothing()
    {
        var local = Config(); var vault = new FakeCredentials { FailRead = true }; var saved = 0;
        var service = Create(local, new(local, _ => saved++), vault);
        var payload = new PortableBackupPayload { Config = Node(local), Credentials = [new() { Kind = "api", Service = "tmdb", Value = "secret" }] };
        await Assert.ThrowsAsync<IOException>(() => service.ImportPayloadAsync(payload, false, default));
        Assert.Equal(0, saved); Assert.Equal(0, vault.Writes);
        vault.FailRead = false;
        payload.Credentials[0].Service = "unconfigured-provider";
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportPayloadAsync(payload, false, default));
        Assert.Equal(0, saved); Assert.Equal(0, vault.Writes);
    }

    [Fact]
    public async Task Failed_settings_save_and_online_recovery_gate_never_touch_credentials()
    {
        var local = Config(); var vault = new FakeCredentials();
        var payload = new PortableBackupPayload { Config = Node(local), Credentials = [new() { Kind = "api", Service = "tmdb", Value = "secret" }] };
        payload.Config["logging"]!["level"] = "Warning";
        var service = Create(local, new(local, _ => throw new IOException("injected")), vault);
        await Assert.ThrowsAsync<IOException>(() => service.ImportPayloadAsync(payload, false, default));
        Assert.Equal(0, vault.Writes); Assert.Equal("Information", local.Logging.Level);
        service = new(local, new(local, _ => { }), () => false, _ => new Dictionary<string, FishKeyEntry>(), _ => throw new Exception(), vault);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportPayloadAsync(payload, false, default));
        Assert.Equal(0, vault.Writes);
    }

    [Fact]
    public async Task Fish_import_retains_full_key_trio_and_never_replaces_existing_by_default()
    {
        var local = Config(); var vault = new FakeCredentials();
        var fish = new FishKeyStore(Path.Combine(_root, "fish.json"), b => b, b => b);
        fish.SetKey("#existing", "current");
        var service = new PortableBackupService(local, new(local, _ => { }), () => true, _ => fish.GetAllKeys(), _ => fish, vault);
        var entry = new FishKeyEntry { Key = "primary", AltKey = "alternate", DhSecretHex = "abcd", Mode = FishMode.CBC, Manual = false, SetAt = new(2020, 1, 1) };
        var payload = new PortableBackupPayload { Config = Node(local), Fish =
            [new() { ServerId = "fixture", Target = "#new", Entry = entry }, new() { ServerId = "fixture", Target = "#existing", Entry = entry }] };
        var result = await service.ImportPayloadAsync(payload, false, default);
        Assert.Equal(1, result.ImportedSecrets); Assert.Equal(1, result.PreservedSecrets);
        Assert.Equal("current", fish.GetKey("#existing")!.Key);
        var restored = fish.GetKey("#new")!;
        Assert.Equal(entry.AltKey, restored.AltKey); Assert.Equal(entry.DhSecretHex, restored.DhSecretHex);
        Assert.Equal(entry.Mode, restored.Mode); Assert.Equal(entry.Manual, restored.Manual); Assert.Equal(entry.SetAt, restored.SetAt);
    }

    [Fact]
    public async Task Export_is_authenticated_readable_and_never_overwrites_an_existing_file()
    {
        var local = Config(); var vault = new FakeCredentials(); vault.Values["GlDrive:api:tmdb"] = "fixture-vault-secret";
        var service = Create(local, new(local, _ => { }), vault);
        var path = Path.Combine(_root, "backup.gldrive");
        var summary = await service.ExportAsync(path, "fixture passphrase");
        Assert.Equal(1, summary.Credentials); Assert.Equal(1, summary.Servers);
        Assert.Equal(summary, await service.InspectAsync(path, "fixture passphrase"));
        var original = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<IOException>(() => service.ExportAsync(path, "other passphrase"));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.DoesNotContain("fixture-vault-secret", Encoding.UTF8.GetString(original));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("[cbc:current-config]", "[cbc:current-config]")]
    public async Task Imported_bracket_key_cannot_replace_protected_local_key_but_current_config_keeps_precedence(string currentKey, string expected)
    {
        var local = Config(); local.Servers[0].Irc.Channels[0].Key = currentKey;
        var backup = Config(); backup.Servers[0].Irc.Channels[0].Key = "[cbc:old-backup]";
        var protectedKeys = new Dictionary<string, FishKeyEntry> { ["#one"] = new() { Key = "current-protected" } };
        var service = new PortableBackupService(local, new(local, _ => { }), () => true, _ => protectedKeys,
            _ => throw new InvalidOperationException(), new FakeCredentials());
        await service.ImportPayloadAsync(new() { Config = Node(backup) }, false, default);
        Assert.Equal(expected, local.Servers[0].Irc.Channels[0].Key);
        Assert.Equal("current-protected", protectedKeys["#one"].Key);
    }

    [Fact]
    public async Task File_changed_after_preview_is_rejected_before_mutation()
    {
        var local = Config(); var saved = 0;
        var service = Create(local, new(local, _ => saved++), new FakeCredentials());
        var path = Path.Combine(_root, "first.gldbackup");
        var preview = await service.ExportAsync(path, "fixture passphrase");
        var payload = new PortableBackupPayload { Config = Node(local) };
        payload.Config["logging"]!["level"] = "Warning";
        File.WriteAllBytes(path, PortableBackupProtection.Encrypt(JsonSerializer.SerializeToUtf8Bytes(payload, Json), "fixture passphrase"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(path, "fixture passphrase", expectedDigest: preview.Digest));
        Assert.Equal(0, saved);
        Assert.Equal("Information", local.Logging.Level);
    }

    [Fact]
    public async Task Case_colliding_server_ids_are_rejected_before_mutation()
    {
        var local = Config(); var saved = 0;
        var backup = Config(); backup.Servers.Add(new() { Id = "FIXTURE" });
        var service = Create(local, new(local, _ => saved++), new FakeCredentials());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportPayloadAsync(new() { Config = Node(backup) }, false, default));
        Assert.Equal(0, saved);
    }

    [Theory]
    [InlineData("different", false)]
    [InlineData("same", true)]
    public async Task Aliased_credential_targets_require_identical_values(string secondValue, bool accepted)
    {
        var local = Config(); var saved = 0; var vault = new FakeCredentials();
        var backup = Config();
        backup.Servers.Add(new() { Id = "second", Connection = new() { Host = "FTP.EXAMPLE.INVALID", Username = "fixture" } });
        var service = Create(local, new(local, _ => saved++), vault);
        var payload = new PortableBackupPayload { Config = Node(backup), Credentials =
            [new() { Kind = "ftp", ServerId = "fixture", Value = "same" }, new() { Kind = "ftp", ServerId = "second", Value = secondValue }] };
        if (accepted)
        {
            await service.ImportPayloadAsync(payload, false, default);
            Assert.Equal(1, saved);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportPayloadAsync(payload, false, default));
            Assert.Equal(0, saved); Assert.Equal(0, vault.Writes);
        }
    }

    private static PortableBackupService Create(AppConfig config, ConfigurationAutomation configuration, FakeCredentials vault)
        => new(config, configuration, () => true, _ => new Dictionary<string, FishKeyEntry>(), _ => throw new InvalidOperationException(), vault);

    [Fact]
    public async Task ExportSkipsInvalidFishEntriesAndReportsTheirCount()
    {
        var config = Config();
        var keys = new Dictionary<string, FishKeyEntry>
        { ["#good"] = new() { Key = "working-secret" }, [""] = new() { Key = "orphan" }, ["#bad"] = new() { Key = "" } };
        var service = new PortableBackupService(config, new(config, _ => { }), () => true, _ => keys,
            _ => throw new InvalidOperationException(), new FakeCredentials());
        var path = Path.Combine(_root, "valid-only.gldbackup");
        var exported = await service.ExportAsync(path, "fixture passphrase");
        Assert.Equal(1, exported.FishKeys);
        Assert.Equal(2, exported.SkippedFishKeys);
        Assert.Equal(1, (await service.InspectAsync(path, "fixture passphrase")).FishKeys);
    }

    [Fact]
    public async Task WholeServerImportMakesOneSafetyCopyAndRetryCannotOverwriteIt()
    {
        var config = Config();
        var path = Path.Combine(_root, "fish-keys-fixture.json");
        var store = new FishKeyStore(path, b => b.ToArray(), b => b.ToArray());
        store.SetKey("#one", "newer-one"); store.SetKey("#two", "newer-two");
        var original = File.ReadAllBytes(path);
        var service = new PortableBackupService(config, new(config, _ => { }), () => true, _ => store.GetAllKeys(), _ => store, new FakeCredentials());
        var payload = new PortableBackupPayload { Config = Node(config), Fish = [
            new() { ServerId = "fixture", Target = "#one", Entry = new() { Key = "older-one" } },
            new() { ServerId = "fixture", Target = "#two", Entry = new() { Key = "older-two" } }] };
        Assert.Equal(2, (await service.ImportPayloadAsync(payload, true, default)).ImportedSecrets);
        var copy = Assert.Single(Directory.GetFiles(_root, "*.pre-import-*"));
        Assert.Equal(original, File.ReadAllBytes(copy));
        Assert.Equal(2, (await service.ImportPayloadAsync(payload, true, default)).ImportedSecrets);
        Assert.Equal(original, File.ReadAllBytes(copy));
        Assert.Equal(2, Directory.GetFiles(_root, "*.pre-import-*").Length);
    }

    private sealed class FakeCredentials : IPortableCredentialAccess
    {
        public Dictionary<string, string> Values { get; } = new();
        public int Writes;
        public bool FailRead;
        public string? Read(string target) => FailRead ? throw new IOException("injected read failure") : Values.GetValueOrDefault(target);
        public void WriteVerified(string target, string user, string value) { Writes++; Values[target] = value; }
    }
}
