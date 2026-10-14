using System.Text.Json;

namespace GlDrive.Services.Automation;

/// <summary>Validate the JSON Schema subset used by the app's tool catalog before any mutation.</summary>
internal static class AutomationSchema
{
    public static void Validate(JsonElement schema, JsonElement value, string path = "arguments")
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            var valid = type.GetString() switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => true
            };
            if (!valid) throw new ArgumentException($"{path} must be {type.GetString()}");
        }
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(c => JsonElement.DeepEquals(c, value)))
            throw new ArgumentException($"{path} is not an allowed value");
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var field in required.EnumerateArray())
                    if (!value.TryGetProperty(field.GetString()!, out _)) throw new ArgumentException($"{path}.{field.GetString()} is required");
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var field in value.EnumerateObject())
                {
                    if (properties.TryGetProperty(field.Name, out var child)) Validate(child, field.Value, path + "." + field.Name);
                    else if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False)
                        throw new ArgumentException($"Unknown argument: {path}.{field.Name}");
                }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("maxItems", out var max) && value.GetArrayLength() > max.GetInt32()) throw new ArgumentException($"{path} has too many items");
            if (schema.TryGetProperty("minItems", out var min) && value.GetArrayLength() < min.GetInt32()) throw new ArgumentException($"{path} has too few items");
            if (schema.TryGetProperty("items", out var itemSchema))
                foreach (var item in value.EnumerateArray()) Validate(itemSchema, item, path + "[]");
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var length = value.GetString()!.Length;
            if (schema.TryGetProperty("maxLength", out var max) && length > max.GetInt32()) throw new ArgumentException($"{path} is too long");
            if (schema.TryGetProperty("minLength", out var min) && length < min.GetInt32()) throw new ArgumentException($"{path} is too short");
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            var number = value.GetDouble();
            if (schema.TryGetProperty("maximum", out var max) && number > max.GetDouble()) throw new ArgumentException($"{path} exceeds maximum");
            if (schema.TryGetProperty("minimum", out var min) && number < min.GetDouble()) throw new ArgumentException($"{path} is below minimum");
        }
    }
}
