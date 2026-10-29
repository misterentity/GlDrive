using System.Text.Json;
using GlDrive.Irc;
using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public class FishKeyCommandTests
{
    [Theory]
    [InlineData("", "#selected", false)]
    [InlineData(" ", "#selected", false)]
    [InlineData(" newkey", "#selected", false)]
    [InlineData("#target ", "#selected", false)]
    [InlineData("newkey", null, false)]
    [InlineData("newkey", "#selected", true)]
    [InlineData("#target newkey", "#selected", true)]
    public void SlashKeyRejectsEmptyArguments(string args, string? selected, bool valid)
    {
        Assert.Equal(valid, FishKeyCommand.TryParse(args, selected, out var target, out var key));
        if (valid) { Assert.Equal("newkey", key); Assert.Equal(args.StartsWith('#') ? "#target" : selected, target); }
    }

    [Theory]
    [InlineData("target", "")]
    [InlineData("target", " ")]
    [InlineData("key", "")]
    [InlineData("key", " ")]
    public void McpKeySetRequiredArgumentRejectsEmptyValues(string name, string value)
    {
        var arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string> { [name] = value });
        Assert.Throws<ArgumentException>(() => AutomationService.Required(arguments, name));
    }
}
