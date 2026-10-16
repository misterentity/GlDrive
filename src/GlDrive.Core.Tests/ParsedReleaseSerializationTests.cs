using System.Text.Json;
using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public sealed class ParsedReleaseSerializationTests
{
    [Fact]
    public void Existing_json_and_quality_values_roundtrip_after_extraction()
    {
        const string persisted = """{"title":"Sample","year":2026,"season":null,"episode":null,"quality":3,"group":"GROUP","isSeasonPack":false,"source":"WEB"}""";
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var restored = JsonSerializer.Deserialize<ParsedRelease>(persisted, options)!;
        Assert.Equal(new ParsedRelease("Sample", 2026, null, null, QualityProfile.Q1080p, "GROUP", false, "WEB"), restored);
        Assert.Equal(persisted, JsonSerializer.Serialize(restored, options));
    }
}
