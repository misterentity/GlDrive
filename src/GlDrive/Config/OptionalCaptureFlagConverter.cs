using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlDrive.Config;

/// <summary>A malformed optional switch disables capture without discarding ordinary settings.</summary>
internal sealed class OptionalCaptureFlagConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var value = JsonDocument.ParseValue(ref reader);
        return value.RootElement.ValueKind == JsonValueKind.True;
    }
    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
}
