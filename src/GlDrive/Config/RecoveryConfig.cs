using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlDrive.Config;

/// <summary>Recovery only reacts to positively identified crash restarts, never service health.</summary>
[JsonConverter(typeof(RecoveryConfigConverter))]
public sealed class RecoveryConfig
{
    public bool CrashLoopProtectionEnabled { get; set; } = true;
    public int CrashLoopThreshold { get; set; } = 3;
    public int CrashLoopWindowMinutes { get; set; } = 15;
    public int[] SafeModeRetryDelaysSeconds { get; set; } = [60, 300, 900];
    public bool UpdateProbationEnabled { get; set; } = true;
    public bool AutoInstallUpdates { get; set; } = true;
    internal string? UnparsedJson { get; init; }

    internal static void Validate(RecoveryConfig value)
    {
        if (value.UnparsedJson != null) return;
        if (value.CrashLoopThreshold is < 3 or > 20 || value.CrashLoopWindowMinutes is < 1 or > 60 ||
            value.SafeModeRetryDelaysSeconds == null || value.SafeModeRetryDelaysSeconds.Length > 8 ||
            value.SafeModeRetryDelaysSeconds.Any(seconds => seconds is < 3 or > 3600))
            throw new ArgumentException("Recovery needs a threshold of 3..20, a window of 1..60 minutes and at most eight retry delays of 3..3600 seconds.");
    }
}

internal sealed class RecoveryConfigConverter : JsonConverter<RecoveryConfig>
{
    public override bool HandleNull => true;
    public override RecoveryConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        try
        {
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => p.Name is not
                ("crashLoopProtectionEnabled" or "crashLoopThreshold" or "crashLoopWindowMinutes" or "safeModeRetryDelaysSeconds" or "updateProbationEnabled" or "autoInstallUpdates")) ||
                root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count())
                throw new JsonException();
            var value = new RecoveryConfig
            {
                CrashLoopProtectionEnabled = !root.TryGetProperty("crashLoopProtectionEnabled", out var enabled) || enabled.GetBoolean(),
                CrashLoopThreshold = root.TryGetProperty("crashLoopThreshold", out var threshold) ? threshold.GetInt32() : 3,
                CrashLoopWindowMinutes = root.TryGetProperty("crashLoopWindowMinutes", out var window) ? window.GetInt32() : 15,
                SafeModeRetryDelaysSeconds = root.TryGetProperty("safeModeRetryDelaysSeconds", out var delays) ? delays.Deserialize<int[]>(options)! : [60, 300, 900],
                UpdateProbationEnabled = !root.TryGetProperty("updateProbationEnabled", out var probation) || probation.GetBoolean(),
                AutoInstallUpdates = !root.TryGetProperty("autoInstallUpdates", out var install) || install.GetBoolean()
            };
            RecoveryConfig.Validate(value);
            return value;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            // A hand-edited optional block cannot invalidate the whole profile or gate startup.
            // Keep explicit opt-outs even if another field is malformed. For duplicate
            // booleans, any false wins so an edit cannot silently re-enable installation.
            bool OptedOut(string name) => root.ValueKind == JsonValueKind.Object && root.EnumerateObject()
                .Any(property => property.Name == name && property.Value.ValueKind == JsonValueKind.False);
            return new()
            {
                CrashLoopProtectionEnabled = false, UnparsedJson = root.GetRawText(),
                AutoInstallUpdates = !OptedOut("autoInstallUpdates"),
                UpdateProbationEnabled = !OptedOut("updateProbationEnabled")
            };
        }
    }

    public override void Write(Utf8JsonWriter writer, RecoveryConfig value, JsonSerializerOptions options)
    {
        if (value.UnparsedJson is { } raw) { writer.WriteRawValue(raw); return; }
        writer.WriteStartObject();
        writer.WriteBoolean("crashLoopProtectionEnabled", value.CrashLoopProtectionEnabled);
        writer.WriteNumber("crashLoopThreshold", value.CrashLoopThreshold);
        writer.WriteNumber("crashLoopWindowMinutes", value.CrashLoopWindowMinutes);
        writer.WritePropertyName("safeModeRetryDelaysSeconds"); JsonSerializer.Serialize(writer, value.SafeModeRetryDelaysSeconds, options);
        writer.WriteBoolean("updateProbationEnabled", value.UpdateProbationEnabled);
        writer.WriteBoolean("autoInstallUpdates", value.AutoInstallUpdates);
        writer.WriteEndObject();
    }
}
