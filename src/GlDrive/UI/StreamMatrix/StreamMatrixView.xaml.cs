using System.Windows;
using System.Windows.Controls;

namespace GlDrive.UI.StreamMatrix;

/// <summary>Live per-download HUD: segment grid, lane table and throughput sparkline. DataContext = <see cref="StreamMatrixViewModel"/>.</summary>
public partial class StreamMatrixView : UserControl
{
    public StreamMatrixView() => InitializeComponent();

    /// <summary>The ⚙ CONFIG button was clicked — the host opens Settings on the streams tab.</summary>
    public event EventHandler? ConfigRequested;

    private void Config_Click(object sender, RoutedEventArgs e) => ConfigRequested?.Invoke(this, EventArgs.Empty);
}
