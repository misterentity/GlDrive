using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GlDrive.UI;

/// <summary>
/// One glyph per transfer state so download rows and race cards never depend on hue alone.
/// Accepts the existing status strings (DownloadStatus and SpreadJobState names) unchanged.
/// </summary>
public static class TransferStatePresentation
{
    public static string Glyph(string? status) => status switch
    {
        "Downloading" => "▶",
        "Extracting" => "⟳",
        "Completed" => "✓",
        "Queued" or "Waiting" => "…",
        "Failed" => "✕",
        "Cancelled" => "⊘",
        "Running" or "Transferring" => "⇄",
        "Paused" => "‖",
        "Stopped" => "■",
        _ => "·"
    };
}

/// <summary>Status string → state glyph.</summary>
public sealed class StatusGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => TransferStatePresentation.Glyph(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>True when the bound value's text equals ConverterParameter (ordinal).</summary>
public sealed class ValueEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Joins status-strip segments with " // ", skipping empty ones so the strip never shows
/// blank or trailing separators.
/// </summary>
public sealed class StatusSegmentsConverter : IMultiValueConverter
{
    public const string Separator = "  //  ";

    public static string Join(IEnumerable<object?> values) =>
        string.Join(Separator, values
            .Select(v => v as string)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim()));

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => Join(values.Where(v => v != DependencyProperty.UnsetValue));

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => [];
}

/// <summary>
/// Collapses a secondary label when it only repeats the primary one (for example a server
/// card's status line saying OFFLINE under an OFFLINE chip) or is empty.
/// </summary>
public sealed class DistinctTextVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var secondary = values.Length > 0 ? values[0] as string : null;
        var primary = values.Length > 1 ? values[1] as string : null;
        return string.IsNullOrWhiteSpace(secondary)
               || string.Equals(secondary.Trim(), primary?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => [];
}
