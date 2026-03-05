using System.Windows;
using Microsoft.Win32;
using Serilog;

namespace GlDrive.UI;

public static class ThemeManager
{
    private static ResourceDictionary? _currentTheme;

    public static void ApplyTheme(string theme)
    {
        var resolved = theme;
        if (string.Equals(theme, "System", StringComparison.OrdinalIgnoreCase))
            resolved = IsWindowsDarkMode() ? "Dark" : "Light";

        var uri = resolved == "Light"
            ? new Uri("pack://application:,,,/UI/Themes/LightTheme.xaml")
            : new Uri("pack://application:,,,/UI/Themes/DarkTheme.xaml");

        var newTheme = new ResourceDictionary { Source = uri };

        var mergedDicts = Application.Current.Resources.MergedDictionaries;

        if (_currentTheme != null)
            mergedDicts.Remove(_currentTheme);

        mergedDicts.Insert(0, newTheme);
        _currentTheme = newTheme;
    }

    private static bool IsWindowsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            // 0 = dark mode, 1 = light mode
            return value is int intVal && intVal == 0;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read Windows theme preference, defaulting to Dark");
            return true;
        }
    }
}
