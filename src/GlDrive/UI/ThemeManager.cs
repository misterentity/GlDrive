using System.Collections.Generic;
using System.Windows;
using Microsoft.Win32;
using Serilog;

namespace GlDrive.UI;

public static class ThemeManager
{
    private static ResourceDictionary? _currentTheme;
    private static readonly Dictionary<object, object?> _originalMainDictValues = new();

    public static void ApplyTheme(string theme)
    {
        var resolved = theme;
        if (string.Equals(theme, "System", StringComparison.OrdinalIgnoreCase))
            resolved = IsWindowsDarkMode() ? "Dark" : "Light";

        var uri = resolved switch
        {
            "Light" => new Uri("pack://application:,,,/UI/Themes/LightTheme.xaml"),
            "Cyberpunk" => new Uri("pack://application:,,,/UI/Themes/CyberpunkTheme.xaml"),
            _ => new Uri("pack://application:,,,/UI/Themes/DarkTheme.xaml"),
        };

        var newTheme = new ResourceDictionary { Source = uri };
        var mergedDicts = Application.Current.Resources.MergedDictionaries;

        if (_currentTheme != null)
            mergedDicts.Remove(_currentTheme);

        // Append (not Insert(0)) — WPF: later merged dictionaries win for duplicate
        // keys. App.xaml has DarkTheme.xaml hardcoded as a merged dict; if we insert
        // at index 0 it stays *behind* DarkTheme and loses every brush conflict.
        mergedDicts.Add(newTheme);
        _currentTheme = newTheme;

        // Restore any previously-overridden main-dict styles before applying new ones.
        // App.xaml defines implicit Style + keyed styles directly in the main resource
        // dictionary, which beats every merged dict in WPF resolution order. To let a
        // theme actually override Button/TabItem/PrimaryButton/etc., we copy the
        // theme's keys *into* the main dict at runtime and restore originals on swap.
        RestoreMainDictOverrides();
        ApplyMainDictOverrides(newTheme);

        if (resolved == "Cyberpunk")
            CyberpunkChrome.Attach();
        else
            CyberpunkChrome.Detach();
    }

    private static void ApplyMainDictOverrides(ResourceDictionary themeDict)
    {
        var mainDict = Application.Current.Resources;
        foreach (var key in themeDict.Keys)
        {
            // Only override keys that already exist in the main dict — those are
            // the ones being shadowed by App.xaml. Theme-only keys (like our new
            // Cyberpunk-specific HudFrameStyle) flow through MergedDictionaries
            // normally and don't need a main-dict copy.
            if (mainDict.Contains(key))
            {
                if (!_originalMainDictValues.ContainsKey(key))
                    _originalMainDictValues[key] = mainDict[key];
                mainDict[key] = themeDict[key];
            }
        }
    }

    private static void RestoreMainDictOverrides()
    {
        if (_originalMainDictValues.Count == 0) return;
        var mainDict = Application.Current.Resources;
        foreach (var kvp in _originalMainDictValues)
        {
            if (kvp.Value != null)
                mainDict[kvp.Key] = kvp.Value;
        }
        _originalMainDictValues.Clear();
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
