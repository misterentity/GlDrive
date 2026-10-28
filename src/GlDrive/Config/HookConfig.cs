using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace GlDrive.Config;

/// <summary>Local executable hooks require a direct, deliberate appsettings edit and restart.</summary>
[JsonConverter(typeof(HookConfigConverter))]
public sealed class HookConfig
{
    public int QueueCapacity { get; set; } = 64;
    public List<HookCommandConfig> Commands { get; set; } = [];
    // Retain invalid optional configuration through unrelated saves and clones.
    internal string? UnparsedJson { get; init; }
}

internal sealed class HookConfigConverter : JsonConverter<HookConfig>
{
    public override bool HandleNull => true;

    private sealed class Fields
    {
        public int QueueCapacity { get; set; } = 64;
        public List<HookCommandConfig> Commands { get; set; } = [];
    }

    public override HookConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var raw = document.RootElement.GetRawText();
        try
        {
            var fields = JsonSerializer.Deserialize<Fields>(raw, options)
                ?? throw new JsonException("Hooks must be an object");
            if (fields.Commands == null) throw new JsonException("Hook commands must be an array");
            return new() { QueueCapacity = fields.QueueCapacity, Commands = fields.Commands };
        }
        catch (JsonException ex)
        {
            // Do not log raw configuration or serializer messages containing user values.
            Log.Error("Hooks configuration could not be parsed at {JsonPath}; hooks disabled; raw JSON retained",
                "/hooks" + (ex.Path ?? "$"));
            return new() { UnparsedJson = raw };
        }
    }

    public override void Write(Utf8JsonWriter writer, HookConfig value, JsonSerializerOptions options)
    {
        if (value?.UnparsedJson is { } raw) writer.WriteRawValue(raw);
        else JsonSerializer.Serialize(writer, new Fields
        {
            QueueCapacity = value?.QueueCapacity ?? 64, Commands = value?.Commands ?? []
        }, options);
    }
}

public sealed class HookCommandConfig
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; }
    public List<string> Events { get; set; } = [];
    public string Executable { get; set; } = "";
    // PowerShell scripts use an explicit -File path; inline shell commands are unsupported.
    public string ScriptPath { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 30;
    public int OutputLimitCharacters { get; set; } = 4096;
}
