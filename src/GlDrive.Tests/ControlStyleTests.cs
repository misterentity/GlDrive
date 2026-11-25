using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace GlDrive.Tests;

public class ControlStyleTests
{
    private static string Src(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "src", "GlDrive", "App.xaml.cs")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!, "src", "GlDrive" }.Concat(parts).ToArray()));
    }

    private static string StyleBody(string xaml, string target)
    {
        var m = Regex.Match(xaml, $"<Style TargetType=\"{target}\"[^>]*>(.*?)</Style>", RegexOptions.Singleline);
        Assert.True(m.Success, $"no implicit {target} style");
        return m.Groups[1].Value;
    }

    // DisplayMemberPath is carried by ItemTemplateSelector. Without this binding the closed
    // combo box prints the item's type name instead of the chosen item's text.
    [Fact]
    public void ComboBox_selection_box_honours_DisplayMemberPath()
    {
        var combo = StyleBody(Src("App.xaml"), "ComboBox");
        Assert.Contains("ContentTemplateSelector=\"{TemplateBinding ItemTemplateSelector}\"", combo);
    }

    // Item-unit scrolling moves whole rows per wheel notch and re-estimates the thumb as row
    // heights vary, so lists jumped while plain ScrollViewers glided.
    [Theory]
    [InlineData("App.xaml")]
    [InlineData("UI", "Themes", "CyberpunkTheme.xaml")]
    [InlineData("UI", "DashboardWindow.xaml")]
    public void Lists_and_grids_scroll_by_pixel(params string[] path)
    {
        var xaml = Src(path);
        foreach (var target in new[] { "ListBox", "DataGrid" })
            Assert.Contains("<Setter Property=\"VirtualizingPanel.ScrollUnit\" Value=\"Pixel\"/>", StyleBody(xaml, target));
    }
}
