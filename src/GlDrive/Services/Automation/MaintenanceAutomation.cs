using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using GlDrive.Tls;
using GlDrive.Util;

namespace GlDrive.Services.Automation;

/// <summary>App certificate pins and restricted configuration snapshots.</summary>
public sealed class MaintenanceAutomation : IAutomationModule
{
    private readonly CertificateManager _certificates;
    private readonly ConfigurationAutomation _configuration;
    private readonly DiagnosticAutomation _diagnostics;
    private readonly string _snapshots;
    private readonly object _gate = new();

    public MaintenanceAutomation(AppConfig config, CertificateManager certificates, string? appDataRoot = null, Action<AppConfig>? persist = null,
        Action? settingsApplied = null)
    {
        _certificates = certificates;
        _configuration = new ConfigurationAutomation(config, persist, settingsApplied: settingsApplied);
        _diagnostics = new DiagnosticAutomation(config, appDataRoot);
        _snapshots = Path.Combine(appDataRoot ?? ConfigManager.AppDataPath, "ai-data", "ai-snapshots");
    }

    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("certificates_list", "Read all trusted TLS certificate pins and certificate metadata used by the app.",
            new { type = "object", properties = new { }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("certificates_trust", "Trust or replace a TLS certificate pin for host:port using its full SHA-256 fingerprint. Affects future connections immediately.",
            new { type = "object", properties = new { hostPort = new { type = "string" }, fingerprint = new { type = "string" } }, required = new[] { "hostPort", "fingerprint" }, additionalProperties = false }, destructive: true),
        AutomationToolDefinition.Create("certificates_remove", "Remove one trusted TLS certificate pin. GlDrive uses trust-on-first-use for previously unseen hosts; the next connection may trust a new certificate automatically.",
            new { type = "object", properties = new { hostPort = new { type = "string" } }, required = new[] { "hostPort" }, additionalProperties = false }, destructive: true),
        AutomationToolDefinition.Create("certificates_clear", "Clear all trusted certificate pins. GlDrive uses trust-on-first-use for unseen hosts; subsequent connections may trust new certificates automatically.",
            new { type = "object", properties = new { }, additionalProperties = false }, destructive: true),
        AutomationToolDefinition.Create("config_snapshots", "List configuration snapshots, newest first. Names are accepted by config_snapshot_get/restore/delete. Paginate using offset and limit.",
            new { type = "object", properties = new { offset = new { type = "integer", minimum = 0 }, limit = new { type = "integer", minimum = 1, maximum = 200 } }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("config_snapshot_get", "Read a configuration snapshot with credentials redacted by default. includeSecrets=true reveals stored channel keys, but the control API token is always redacted. External Credential Manager passwords are not in snapshots.",
            new { type = "object", properties = new { name = new { type = "string" }, includeSecrets = new { type = "boolean" } }, required = new[] { "name" }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("config_snapshot_create", "Create a private, atomic snapshot of current application settings, including stored channel keys. External Credential Manager passwords are not copied.",
            new { type = "object", properties = new { }, additionalProperties = false }),
        AutomationToolDefinition.Create("config_snapshot_restore", "Restore a named configuration snapshot through typed validation and atomic persistence, saving a pre-restore snapshot first. Retains the current control API token and executable hook definitions. Restart required. Optional expectedRevision prevents overwriting newer settings.",
            new { type = "object", properties = new { name = new { type = "string" }, expectedRevision = new { type = "string" } }, required = new[] { "name" }, additionalProperties = false }, destructive: true),
        AutomationToolDefinition.Create("config_snapshot_delete", "Delete one named configuration snapshot from the app's snapshot directory.",
            new { type = "object", properties = new { name = new { type = "string" } }, required = new[] { "name" }, additionalProperties = false }, destructive: true)
    ];

    public async Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (operation == "config_snapshot_restore" && System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            return await (await dispatcher.InvokeAsync(() => ExecuteAsync(operation, arguments, ct)));
        if (operation.StartsWith("certificates_", StringComparison.Ordinal)) return Certificates(operation, arguments);
        lock (_gate)
        {
            _diagnostics.EnsureDirectory(_snapshots);
            switch (operation)
            {
                case "config_snapshots":
                    var files = Directory.Exists(_snapshots) ? new DirectoryInfo(_snapshots).EnumerateFiles("*.json")
                        .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0).OrderByDescending(f => f.LastWriteTimeUtc).ToArray() : [];
                    var offset = Math.Max(0, ConfigurationAutomation.Integer(arguments, "offset", 0));
                    var limit = Math.Clamp(ConfigurationAutomation.Integer(arguments, "limit", 50), 1, 200);
                    return new { total = files.Length, offset, snapshots = files.Skip(offset).Take(limit).Select(f => new { name = f.Name, sizeBytes = f.Length, modifiedUtc = f.LastWriteTimeUtc }).ToArray() };
                case "config_snapshot_get":
                    var name = Required(arguments, "name");
                    return new { name, config = ConfigurationAutomation.Redact(ReadSnapshot(name), ConfigurationAutomation.Boolean(arguments, "includeSecrets")) };
                case "config_snapshot_create":
                    CheckWritable();
                    return new { saved = true, name = SaveSnapshot("manual") };
                case "config_snapshot_restore": return Restore(arguments);
                case "config_snapshot_delete":
                    CheckWritable();
                    var deleteName = Required(arguments, "name");
                    File.Delete(SnapshotPath(deleteName));
                    return new { deleted = true, name = deleteName };
                default: throw new ArgumentException("Unknown maintenance operation");
            }
        }
    }

    private object Certificates(string operation, JsonElement arguments)
    {
        if (operation != "certificates_list") CheckWritable();
        switch (operation)
        {
            case "certificates_list":
                return new { certificates = _certificates.GetTrustedCertificates().OrderBy(p => p.Key).Select(p => new
                {
                    hostPort = p.Key, fingerprint = p.Value.Fingerprint, trustedAt = p.Value.TrustedAt,
                    subject = p.Value.Subject, issuer = p.Value.Issuer, notAfter = p.Value.NotAfter
                }).ToArray() };
            case "certificates_trust":
                var hostPort = HostPort(Required(arguments, "hostPort"));
                var fingerprint = Required(arguments, "fingerprint").Replace(":", "").Replace(" ", "").ToUpperInvariant();
                if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)) throw new ArgumentException("fingerprint must contain exactly 32 SHA-256 bytes (64 hex digits)");
                _certificates.TrustCertificate(hostPort, fingerprint);
                return new { trusted = true, hostPort, fingerprint };
            case "certificates_remove":
                var remove = HostPort(Required(arguments, "hostPort"));
                _certificates.RemoveTrustedCertificate(remove);
                return new { removed = true, hostPort = remove };
            case "certificates_clear":
                _certificates.ClearTrustedCertificates();
                return new { cleared = true };
            default: throw new ArgumentException("Unknown certificate operation");
        }
    }

    private object Restore(JsonElement arguments)
    {
        using var attribution = ConfigWriteScope.Begin("restore:snapshot");
        CheckWritable();
        var name = Required(arguments, "name");
        var candidate = ReadSnapshot(name);
        object? result = null;
        SecureFile.WithPathLock(ConfigManager.ConfigPath, () =>
        {
            var revision = ConfigurationAutomation.String(arguments, "expectedRevision");
            if (revision != null)
            {
                var current = JsonSerializer.SerializeToElement(_configuration.Get()).GetProperty("revision").GetString();
                if (current != revision) throw new InvalidOperationException("Settings changed since they were read; fetch them again before restoring");
            }
            // Restore ordinary settings without reviving a revoked token or substituting
            // executable code. Preserve these values before parsing the historical config.
            var currentSettings = _configuration.CurrentSnapshot();
            candidate["controlApi"] ??= new JsonObject();
            candidate["controlApi"]!["token"] = currentSettings["controlApi"]?["token"]?.DeepClone();
            candidate["hooks"] = currentSettings["hooks"]?.DeepClone();
            var snapshot = candidate.Deserialize<AppConfig>(ConfigurationAutomation.Json)
                ?? throw new ArgumentException("Snapshot does not contain app settings");
            ConfigurationAutomation.Validate(snapshot);
            var replacements = JsonSerializer.SerializeToNode(snapshot, ConfigurationAutomation.Json)!.AsObject();
            var operations = new JsonArray(replacements.Select(p => (JsonNode?)new JsonObject
            {
                ["op"] = "replace", ["path"] = "/" + p.Key, ["value"] = p.Value?.DeepClone()
            }).ToArray());
            var backup = SaveSnapshot("pre-restore");
            var saved = _configuration.Apply(operations, revision, allowSecrets: true);
            result = new { name, backup, result = saved, restartRequired = true };
        });
        return result!;
    }

    private string SaveSnapshot(string label)
    {
        Directory.CreateDirectory(_snapshots);
        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{label}-{Guid.NewGuid():N}.json";
        // Take the config path lock before the snapshot path lock, as restoration does.
        var current = _configuration.CurrentSnapshot().ToJsonString();
        SecureFile.WriteAllTextRestricted(Path.Combine(_snapshots, name), current);
        return name;
    }

    private JsonNode ReadSnapshot(string name)
    {
        var path = SnapshotPath(name);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 4 * 1024 * 1024) throw new ArgumentException("Snapshot exceeds the 4 MiB read limit");
        return JsonNode.Parse(stream) ?? throw new ArgumentException("Snapshot is empty");
    }

    private string SnapshotPath(string name)
    {
        DiagnosticAutomation.ValidateName(name);
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Snapshot names must end in .json");
        var path = Path.Combine(_snapshots, name);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Linked snapshots are not supported");
        return path;
    }

    private static string Required(JsonElement arguments, string name) => ConfigurationAutomation.String(arguments, name) ?? throw new ArgumentException(name + " is required");
    private static void CheckWritable()
    {
        if (ConfigManager.ReadOnly) throw new InvalidOperationException("Application data is read-only in this mode");
    }

    private static string HostPort(string hostPort)
    {
        var split = hostPort.LastIndexOf(':');
        if (hostPort.Length > 512 || split <= 0 || hostPort.Any(char.IsWhiteSpace) || hostPort.IndexOfAny(['/', '\\', '?', '#', '@']) >= 0
            || !int.TryParse(hostPort[(split + 1)..], out var port) || port is < 1 or > 65535)
            throw new ArgumentException("hostPort must be a hostname/IP followed by :port");
        return hostPort;
    }
}
