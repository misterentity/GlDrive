using GlDrive.Downloads;
using Xunit;

namespace GlDrive.Tests;

public class PathSanitizerTests
{
    [Theory]
    [InlineData("normal.name", "normal.name")]
    [InlineData("bad:name?", "badname")]
    [InlineData("a/b\\c", "abc")]
    [InlineData("  trimmed  ", "trimmed")]
    public void Sanitizes_illegal_chars(string input, string expected)
        => Assert.Equal(expected, PathSanitizer.Sanitize(input));

    [Fact]
    public void Reserved_device_names_are_escaped()
    {
        var r = PathSanitizer.Sanitize("CON");
        Assert.NotEqual("CON", r);   // must not produce a raw reserved name
    }

    [Fact]
    public void Empty_becomes_placeholder()
        => Assert.Equal("_", PathSanitizer.Sanitize("   "));
}
