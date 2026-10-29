using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlDrive.Config;

/// <summary>Opt-in client restrictions; the existing owner token retains every capability.</summary>
[JsonConverter(typeof(AutomationSecurityConfigConverter))]
public sealed class AutomationSecurityConfig
{
    public bool RequireDangerousApproval { get; set; } = true;
    public bool RequireConfigurationApproval { get; set; } = true;
    public bool RequireLegacyApproval { get; set; }
    public int ApprovalTimeoutSeconds { get; set; } = 120;
    public bool LedgerEnabled { get; set; } = true;
    public int LedgerCapacity { get; set; } = 2000;
    public List<AutomationClientConfig> Clients { get; set; } = [];
    internal string? UnparsedJson { get; init; }

    public static readonly string[] ScopeNames = ["Observe", "Operate", "Configure", "Secrets", "RemoteAdmin", "LocalFiles"];

    internal static void Validate(AutomationSecurityConfig settings)
    {
        if (settings.UnparsedJson != null) return; // Isolated optional block; legacy access remains usable.
        if (settings.ApprovalTimeoutSeconds is < 5 or > 600 || settings.LedgerCapacity is < 100 or > 10000 || settings.Clients == null || settings.Clients.Count > 128)
            throw new ArgumentException("Invalid automation security timeout, ledger capacity, or client count.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var client in settings.Clients)
        {
            if (client == null || !ValidId(client.Id) || !ids.Add(client.Id) || string.IsNullOrWhiteSpace(client.Name) ||
                client.Name.Length > 80 || client.Name.Any(char.IsControl) || client.TokenHash == null || client.TokenHash.Length != 64 ||
                !client.TokenHash.All(char.IsAsciiHexDigit) || !hashes.Add(client.TokenHash) || client.Scopes == null ||
                client.Scopes.Count is < 1 or > 6 || client.Scopes.Any(scope => !ScopeNames.Contains(scope, StringComparer.OrdinalIgnoreCase)) ||
                client.ToolFamilies == null || client.ToolFamilies.Count > 64 || client.ToolFamilies.Any(family =>
                    string.IsNullOrEmpty(family) || family.Length > 64 || family.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) ||
                client.AllowedLocalRoots == null || client.AllowedLocalRoots.Count > 32 || client.AllowedLocalRoots.Any(root =>
                    string.IsNullOrWhiteSpace(root) || root.Length > 4096 || !System.IO.Path.IsPathFullyQualified(root)))
                throw new ArgumentException("Invalid scoped automation client. Use unique IDs and token hashes, known scopes and exact tool families.");
        }
    }

    private static bool ValidId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 80 &&
        id is not ("legacy" or "unauthenticated") && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

public sealed class AutomationClientConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public List<string> Scopes { get; set; } = ["Observe"];
    public List<string> ToolFamilies { get; set; } = [];
    public List<string> AllowedLocalRoots { get; set; } = [];
    public bool AllowDestructive { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
}

internal sealed class AutomationSecurityConfigConverter : JsonConverter<AutomationSecurityConfig>
{
    public override bool HandleNull => true;
    public override AutomationSecurityConfig Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        try
        {
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p =>
                    p.Name is not ("requireDangerousApproval" or "requireConfigurationApproval" or "requireLegacyApproval" or "approvalTimeoutSeconds" or "ledgerEnabled" or "ledgerCapacity" or "clients")))
                throw new JsonException();
            var result = new AutomationSecurityConfig
            {
                RequireDangerousApproval = !root.TryGetProperty("requireDangerousApproval", out var approval) || approval.GetBoolean(),
                RequireConfigurationApproval = !root.TryGetProperty("requireConfigurationApproval", out var configuration) || configuration.GetBoolean(),
                RequireLegacyApproval = root.TryGetProperty("requireLegacyApproval", out var legacy) && legacy.GetBoolean(),
                ApprovalTimeoutSeconds = root.TryGetProperty("approvalTimeoutSeconds", out var timeout) ? timeout.GetInt32() : 120,
                LedgerEnabled = !root.TryGetProperty("ledgerEnabled", out var ledger) || ledger.GetBoolean(),
                LedgerCapacity = root.TryGetProperty("ledgerCapacity", out var capacity) ? capacity.GetInt32() : 2000,
                Clients = root.TryGetProperty("clients", out var clients) ? clients.Deserialize<List<AutomationClientConfig>>(options)! : []
            };
            AutomationSecurityConfig.Validate(result);
            return result;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { return new() { UnparsedJson = root.GetRawText() }; }
    }

    public override void Write(Utf8JsonWriter writer, AutomationSecurityConfig value, JsonSerializerOptions options)
    {
        if (value.UnparsedJson is { } raw) { writer.WriteRawValue(raw); return; }
        writer.WriteStartObject();
        writer.WriteBoolean("requireDangerousApproval", value.RequireDangerousApproval);
        writer.WriteBoolean("requireConfigurationApproval", value.RequireConfigurationApproval);
        writer.WriteBoolean("requireLegacyApproval", value.RequireLegacyApproval);
        writer.WriteNumber("approvalTimeoutSeconds", value.ApprovalTimeoutSeconds);
        writer.WriteBoolean("ledgerEnabled", value.LedgerEnabled);
        writer.WriteNumber("ledgerCapacity", value.LedgerCapacity);
        writer.WritePropertyName("clients"); JsonSerializer.Serialize(writer, value.Clients, options);
        writer.WriteEndObject();
    }
}
