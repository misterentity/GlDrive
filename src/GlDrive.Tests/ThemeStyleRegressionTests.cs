using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace GlDrive.Tests;

public sealed class ThemeStyleRegressionTests
{
    [Fact]
    public void A_theme_cannot_replace_the_global_ComboBox_template_with_setters_only()
    {
        var root = FindRepoRoot();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(root, "src", "GlDrive", "UI", "Themes"), "*Theme.xaml"))
        {
            var document = XDocument.Load(path);
            var implicitComboStyles = document
                .Descendants(presentation + "Style")
                .Where(style => (string?)style.Attribute("TargetType") == "ComboBox"
                                && style.Attributes().All(attribute =>
                                    attribute.Name.LocalName is not "Key"));

            foreach (var style in implicitComboStyles)
            {
                Assert.Contains(style.Elements(presentation + "Setter"), setter =>
                    (string?)setter.Attribute("Property") == "Template");
            }
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "GlDrive", "GlDrive.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found from test output path.");
    }
}
