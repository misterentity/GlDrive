using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GlDrive.UI;

/// <summary>Disables selection actions until a list has a selected item.</summary>
public sealed class HasSelectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null && value != DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
