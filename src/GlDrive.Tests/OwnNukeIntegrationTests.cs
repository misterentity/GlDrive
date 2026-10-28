using System.Text.Json;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class OwnNukeIntegrationTests
{
    [Fact]
    public void Passive_exact_account_alerts_default_on_and_are_in_schema()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("{}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(config.Spread.OwnNukeAlertsEnabled);
        Assert.Contains("ownNukeAlertsEnabled", JsonSerializer.Serialize(new ConfigurationAutomation(config, _ => {}).Schema()));
    }

    [Fact]
    public void Truncated_name_resolves_only_one_long_known_prefix()
    {
        const string release = "Synthetic.Show.S01E01.1080p.WEB.H264-TEST";
        Assert.Equal(release, NukeReleaseResolver.ResolveUnique("Synthetic.Show.S01E01...", [release, release]));
        Assert.Null(NukeReleaseResolver.ResolveUnique("Synthetic.Show...", [release, "Synthetic.Show.S02E01.1080p.WEB.H264-TEST"]));
        Assert.Null(NukeReleaseResolver.ResolveUnique("Short...", ["Short.Show.S01E01.1080p.WEB.H264-TEST"]));
        Assert.Null(NukeReleaseResolver.ResolveUnique("../Synthetic.Show...", [release]));
        Assert.Null(NukeReleaseResolver.ResolveUnique("Unknown.Show...", [release]));
    }
}
