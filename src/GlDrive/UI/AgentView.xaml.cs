using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace GlDrive.UI;

public partial class AgentView : UserControl
{
    public AgentView() : this(demoMode: false) { }

    internal AgentView(bool demoMode)
    {
        InitializeComponent();
        if (!demoMode) DataContext = new AgentViewModel();
    }
}

public sealed class AgentTimestampConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        DateTimeOffset.TryParse(value?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
            ? timestamp.ToLocalTime().ToString("MMM d HH:mm", culture)
            : value?.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
