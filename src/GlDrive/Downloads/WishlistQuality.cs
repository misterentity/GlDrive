using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GlDrive.Downloads;

/// <summary>Quality defaults for new wishlist entries; persisted entries keep their quality.</summary>
internal static class WishlistQuality
{
    internal static QualityProfile Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "any" => QualityProfile.Any,
        "sd" => QualityProfile.SD,
        "720p" or "q720p" => QualityProfile.Q720p,
        "2160p" or "q2160p" => QualityProfile.Q2160p,
        _ => QualityProfile.Q1080p
    };

    internal static string Display(string? value) => Parse(value) switch
    {
        QualityProfile.Any => "Any",
        QualityProfile.SD => "SD",
        QualityProfile.Q720p => "720p",
        QualityProfile.Q2160p => "2160p",
        _ => "1080p"
    };

    internal static List<WishlistItem> ReadImport(string json, string? defaultQuality)
    {
        var array = JsonNode.Parse(json)?.AsArray();
        if (array == null) return [];
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
        return array.Select(node =>
        {
            var row = node?.AsObject() ?? throw new JsonException("Wishlist import contains an empty entry.");
            if (!row.Any(p => p.Key.Equals("quality", StringComparison.OrdinalIgnoreCase)))
                row["quality"] = (int)Parse(defaultQuality);
            return row.Deserialize<WishlistItem>(options) ?? throw new JsonException("Invalid wishlist entry.");
        }).ToList();
    }
}
