using System.Text.Json;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class WishlistQualityTests
{
    [Theory]
    [InlineData(null, QualityProfile.Q1080p)]
    [InlineData("", QualityProfile.Q1080p)]
    [InlineData("invalid", QualityProfile.Q1080p)]
    [InlineData("0", QualityProfile.Q1080p)]
    [InlineData("Any", QualityProfile.Any)]
    [InlineData("SD", QualityProfile.SD)]
    [InlineData("720p", QualityProfile.Q720p)]
    [InlineData("1080p", QualityProfile.Q1080p)]
    [InlineData(" 2160P ", QualityProfile.Q2160p)]
    [InlineData("Q2160p", QualityProfile.Q2160p)]
    public void Parses_safe_defaults_and_preserves_explicit_choices(string? value, QualityProfile expected)
    {
        Assert.Equal(expected, WishlistQuality.Parse(value));
        Assert.Equal(expected, WishlistQuality.Parse(WishlistQuality.Display(value)));
    }

    [Fact]
    public void Import_distinguishes_omitted_quality_from_explicit_any_and_resolution()
    {
        var items = WishlistQuality.ReadImport("""
            [{"title":"Default"},{"title":"Any","quality":0},
             {"title":"Explicit","quality":"Q720p"},{"title":"Case","Quality":3}]
            """, "2160p");
        Assert.Equal(new[] { QualityProfile.Q2160p, QualityProfile.Any, QualityProfile.Q720p, QualityProfile.Q1080p },
            items.Select(i => i.Quality));
        Assert.Equal(QualityProfile.Q1080p, Assert.Single(WishlistQuality.ReadImport("[{}]", "bad")).Quality);
    }

    [Fact]
    public async Task Mcp_new_item_uses_default_and_patch_preserves_existing_or_explicit_quality()
    {
        var config = new AppConfig { Downloads = new() { QualityDefault = "2160p" } };
        var service = new AutomationService(config, null!, new NotificationStore(persistToDisk: false));
        var id = Guid.NewGuid().ToString("N");
        async Task<JsonElement> Upsert(object item) => (JsonElement)(await service.ExecuteAsync("wishlist.upsert",
            JsonSerializer.SerializeToElement(new { item }), default))!;
        try
        {
            Assert.Equal("Q2160p", (await Upsert(new { id, title = "Synthetic title" })).GetProperty("quality").GetString());
            config.Downloads.QualityDefault = "720p";
            Assert.Equal("Q2160p", (await Upsert(new { id, title = "Updated title" })).GetProperty("quality").GetString());
            Assert.Equal("Any", (await Upsert(new { id, quality = "Any" })).GetProperty("quality").GetString());
        }
        finally
        {
            await service.ExecuteAsync("wishlist.remove", JsonSerializer.SerializeToElement(new { id }), default);
        }
    }
}
