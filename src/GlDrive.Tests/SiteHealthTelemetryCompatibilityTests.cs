using System.Text.Json;
using GlDrive.AiAgent;
using Xunit;

namespace GlDrive.Tests;

public sealed class SiteHealthTelemetryCompatibilityTests
{
    [Fact]
    public void New_health_events_omit_unmeasured_5xx_metric()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new SiteHealthEvent()));
        Assert.False(json.RootElement.TryGetProperty("errors5xx", out _));
    }

    [Theory]
    [InlineData("{}", null)]
    [InlineData("{\"errors5xx\":null}", null)]
    [InlineData("{\"errors5xx\":0}", 0)]
    [InlineData("{\"errors5xx\":7}", 7)]
    public void Historical_logs_preserve_the_metric_when_present(string json, int? expected)
    {
        var value = JsonSerializer.Deserialize<SiteHealthEvent>(json)!;
        Assert.Equal(expected, value.Errors5xx);
        Assert.Equal(expected, JsonSerializer.Deserialize<SiteHealthEvent>(JsonSerializer.Serialize(value))!.Errors5xx);
    }
}
