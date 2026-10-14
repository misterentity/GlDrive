using GlDrive.Services.Automation;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

public class InstallerAutomationTests
{
    [Theory]
    [InlineData("password\nadmin_username=other")]
    [InlineData("value\rnext")]
    [InlineData("embedded\0nul")]
    [InlineData("quote\"value")]
    public void CacheRejectsUnsupportedQuotesAndLineInjection(string input)
        => Assert.Throws<ArgumentException>(() => GlftpdInstallerWindow.QuoteInstallerValue(input));

    [Theory]
    [InlineData("#main n nopass")]
    [InlineData("a$b`c\\d'e")]
    [InlineData("")]
    public void CacheKeepsValuesCompatibleWithTheInstallersLiteralQuoteStrippingReader(string input)
    {
        var encoded = GlftpdInstallerWindow.QuoteInstallerValue(input);
        Assert.StartsWith("\"", encoded);
        Assert.EndsWith("\"", encoded);
        Assert.Equal(input, encoded.Replace("\"", ""));
    }

    [Fact]
    public void InstallerUsesExplicitSystemMutationToolsAndHostKeyPin()
    {
        var tools = new InstallerAutomation().Tools;
        Assert.True(tools.Single(t => t.Name == "installer.start").Destructive);
        Assert.True(tools.Single(t => t.Name == "installer.cancel").Destructive);
        var connect = tools.Single(t => t.Name == "installer.connect").InputSchema;
        Assert.True(connect.GetProperty("properties").TryGetProperty("expectedHostKey", out _));
        Assert.DoesNotContain(tools, t => t.Name.Contains("command") || t.Name.Contains("shell"));
        Assert.True(tools.Single(t => t.Name == "installer.profile_get").InputSchema.GetProperty("properties").TryGetProperty("includeSecrets", out _));
    }
}
