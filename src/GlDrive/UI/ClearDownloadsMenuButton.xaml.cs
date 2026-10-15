using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace GlDrive.UI;

public partial class ClearDownloadsMenuButton : UserControl
{
    public ClearDownloadsMenuButton() => InitializeComponent();

    /// <summary>The clear menu (exposed for the render tests).</summary>
    internal ContextMenu Menu => MenuButton.ContextMenu;

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (MenuButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = MenuButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
