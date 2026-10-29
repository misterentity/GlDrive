using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlDrive.Config;

/// <summary>Optional persistence protections do not participate in engine admission.</summary>
[JsonConverter(typeof(DataStewardshipConfigConverter))]
public sealed class DataStewardshipConfig
{
    public bool CopyConfigSecrets { get; set; } = true;
    public bool GuardFeedPreferences { get; set; }
    internal string? UnparsedJson { get; init; }
}

internal sealed class DataStewardshipConfigConverter : JsonConverter<DataStewardshipConfig>
{
    public override bool HandleNull => true;
    public override DataStewardshipConfig Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p =>
                p.Name is not ("copyConfigSecrets" or "guardFeedPreferences") ||
                p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
            return new() { CopyConfigSecrets = false, UnparsedJson = root.GetRawText() };
        return new()
        {
            CopyConfigSecrets = !root.TryGetProperty("copyConfigSecrets", out var copy) || copy.GetBoolean(),
            GuardFeedPreferences = root.TryGetProperty("guardFeedPreferences", out var guard) && guard.GetBoolean()
        };
    }
    public override void Write(Utf8JsonWriter writer, DataStewardshipConfig value, JsonSerializerOptions options)
    {
        if (value.UnparsedJson is { } raw) { writer.WriteRawValue(raw); return; }
        writer.WriteStartObject();
        writer.WriteBoolean("copyConfigSecrets", value.CopyConfigSecrets);
        writer.WriteBoolean("guardFeedPreferences", value.GuardFeedPreferences);
        writer.WriteEndObject();
    }
}
