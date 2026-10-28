using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace GlDrive.Config;

/// <summary>Passive, local-only IRC attention. These settings never affect message routing.</summary>
[JsonConverter(typeof(IrcAttentionConfigConverter))]
public sealed class IrcAttentionConfig
{
    public bool NickHighlightsEnabled { get; set; } = true;
    public bool PrivateMessageAlertsEnabled { get; set; } = true;
    public List<string> Keywords { get; set; } = [];
    public List<string> IgnoredNicks { get; set; } = [];
    internal string? UnparsedJson { get; init; }
}

internal sealed class IrcAttentionConfigConverter : JsonConverter<IrcAttentionConfig>
{
    public override bool HandleNull => true;
    private sealed class Fields
    {
        public bool NickHighlightsEnabled { get; set; } = true;
        public bool PrivateMessageAlertsEnabled { get; set; } = true;
        public List<string> Keywords { get; set; } = [];
        public List<string> IgnoredNicks { get; set; } = [];
    }
    public override IrcAttentionConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var raw = document.RootElement.GetRawText();
        try
        {
            var fields = JsonSerializer.Deserialize<Fields>(raw, options) ?? throw new JsonException();
            if (fields.Keywords == null || fields.IgnoredNicks == null ||
                fields.Keywords.Concat(fields.IgnoredNicks).Any(s => s == null || s.Length > 128) ||
                fields.Keywords.Count > 64 || fields.IgnoredNicks.Count > 64) throw new JsonException();
            return new() { NickHighlightsEnabled = fields.NickHighlightsEnabled,
                PrivateMessageAlertsEnabled = fields.PrivateMessageAlertsEnabled,
                Keywords = fields.Keywords, IgnoredNicks = fields.IgnoredNicks };
        }
        catch (JsonException)
        {
            Log.Warning("IRC attention settings are invalid; notices disabled and raw settings retained");
            return new() { NickHighlightsEnabled = false, PrivateMessageAlertsEnabled = false, UnparsedJson = raw };
        }
    }
    public override void Write(Utf8JsonWriter writer, IrcAttentionConfig value, JsonSerializerOptions options)
    {
        if (value?.UnparsedJson is { } raw) writer.WriteRawValue(raw);
        else JsonSerializer.Serialize(writer, new Fields { NickHighlightsEnabled = value?.NickHighlightsEnabled ?? true,
            PrivateMessageAlertsEnabled = value?.PrivateMessageAlertsEnabled ?? true,
            Keywords = value?.Keywords ?? [], IgnoredNicks = value?.IgnoredNicks ?? [] }, options);
    }
}
