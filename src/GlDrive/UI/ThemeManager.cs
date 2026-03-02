using System.Windows;

namespace GlDrive.UI;

public static class ThemeManager
{
    private static ResourceDictionary? _currentTheme;

    public static void ApplyTheme(string theme)
    {
        var uri = theme == "Light"
            ? new Uri("pack://application:,,,/UI/Themes/LightTheme.xaml")
            : new Uri("pack://application:,,,/UI/Themes/DarkTheme.xaml");

        var newTheme = new ResourceDictionary { Source = uri };

        var mergedDicts = Application.Current.Resources.MergedDictionaries;

        if (_currentTheme != null)
            mergedDicts.Remove(_currentTheme);

        mergedDicts.Insert(0, newTheme);
        _currentTheme = newTheme;
    }
}
