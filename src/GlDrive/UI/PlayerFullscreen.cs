using System.Windows;
using System.Windows.Controls;

namespace GlDrive.UI;

internal static class PlayerFullscreen
{
    internal static void Exit(ref Window? fullscreenWindow, Panel? playerArea, UIElement playerView)
    {
        var window = fullscreenWindow;
        if (window == null) return;

        // Close raises Closed synchronously, and that handler calls Exit again.
        // Relinquish this window before restoring the view so the callback cannot
        // insert the same visual into its original parent a second time.
        fullscreenWindow = null;

        window.Content = null;
        if (playerArea != null)
            playerArea.Children.Insert(0, playerView);

        window.Close();
    }
}
