using GlDrive.Irc;
using Xunit;

namespace GlDrive.Tests;

public sealed class SiteAddlineTests
{
    private static string[] Commands(params string[] texts) => SiteAddline.Extract(texts).Select(c => c.Command).ToArray();

    [Theory]
    [InlineData("site adduser bob s3cret *@1.2.3.4", "SITE ADDUSER bob s3cret *@1.2.3.4")]
    [InlineData("SITE GADDUSER GRP bob s3cret ident@1.2.3.* *@5.6.7.8", "SITE GADDUSER GRP bob s3cret ident@1.2.3.* *@5.6.7.8")]
    [InlineData("site addip bob *@9.9.9.9", "SITE ADDIP bob *@9.9.9.9")]
    [InlineData("addline: site adduser bob pw *@1.2.3.4 thanks!", "SITE ADDUSER bob pw *@1.2.3.4")]
    [InlineData("addline: adduser bob pw *@1.2.3.4", "SITE ADDUSER bob pw *@1.2.3.4")]
    [InlineData("!adduser bob pw *@1.2.3.4", "SITE ADDUSER bob pw *@1.2.3.4")]
    [InlineData("/quote site adduser bob pw", "SITE ADDUSER bob pw")]
    [InlineData("adduser bob pw", "SITE ADDUSER bob pw")]
    public void Extracts_addlines_from_irc_text(string text, string expected) =>
        Assert.Equal([expected], Commands(text));

    [Theory]
    [InlineData("can you adduser me on your site")]
    [InlineData("site adduser bob")]
    [InlineData("site addip bob 1.2.3.4")]
    [InlineData("site gadduser GRP bob")]
    [InlineData("site adduser *@1.2.3.4 pw")]
    [InlineData("site who")]
    public void Prose_and_incomplete_lines_are_not_addlines(string text) => Assert.Empty(Commands(text));

    [Fact]
    public void Several_lines_and_messages_yield_each_addline_once()
    {
        Assert.Equal(["SITE ADDUSER bob pw *@1.2.3.4", "SITE ADDIP bob *@5.6.7.8"],
            Commands("site adduser bob pw *@1.2.3.4 then site addip bob *@5.6.7.8", "site adduser bob pw *@1.2.3.4"));
    }

    [Fact]
    public void Edited_line_must_be_exactly_one_addline()
    {
        Assert.True(SiteAddline.TryParseLine("site adduser bob pw *@1.2.3.4", out var c, out _));
        Assert.Equal("SITE ADDUSER bob pw *@1.2.3.4", c.Command);
        Assert.True(SiteAddline.TryParseLine("ADDIP bob *@1.2.3.4 *@4.3.2.1", out _, out _));
        Assert.False(SiteAddline.TryParseLine("site adduser bob pw *@1.2.3.4 extra", out _, out var error));
        Assert.Contains("ADDUSER", error);
        Assert.False(SiteAddline.TryParseLine("site deluser bob", out _, out _));
        Assert.False(SiteAddline.TryParseLine("site chpass bob pw", out _, out _));
        Assert.False(SiteAddline.TryParseLine("site adduser bob pw\r\nSITE DELUSER x", out _, out _));
        Assert.False(SiteAddline.TryParseLine("", out _, out _));
    }

    [Fact]
    public void Redacted_copy_hides_only_the_password()
    {
        Assert.True(SiteAddline.TryParseLine("site gadduser GRP bob s3cret *@1.2.3.4", out var g, out _));
        Assert.Equal("SITE GADDUSER GRP bob ******** *@1.2.3.4", g.Redacted);
        Assert.Equal("bob", g.User);
        Assert.True(SiteAddline.TryParseLine("site addip bob *@1.2.3.4", out var ip, out _));
        Assert.Equal("SITE ADDIP bob *@1.2.3.4", ip.Redacted);
    }
}
