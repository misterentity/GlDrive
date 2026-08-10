using System.Collections;
using System.Globalization;
using System.Windows.Data;

namespace GlDrive.UI;

/// <summary>
/// Renders a list as a comma-separated string for single-line DataGrid cells.
/// Used by the Spread tab's per-race expander (owners / missing-from / failed routes).
/// An empty list shows an em dash rather than a blank cell, so "nothing here" reads as
/// deliberate instead of looking like a binding that failed.
/// </summary>
public class JoinListConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable items || value is string) return value?.ToString() ?? "—";
        var parts = items.Cast<object?>().Select(o => o?.ToString()).Where(s => !string.IsNullOrEmpty(s)).ToList();
        return parts.Count == 0 ? "—" : string.Join(", ", parts);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
