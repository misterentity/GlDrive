using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Serilog;

namespace GlDrive.UI;

public static class CyberpunkChrome
{
    private const string OverlayNameTag = "__CyberpunkScanlineOverlay__";
    private static bool _isActive;
    private static EventHandler? _windowActivatedHandler;

    public static void Attach()
    {
        if (_isActive) return;
        _isActive = true;

        // Attach to all currently-open windows
        foreach (Window window in Application.Current.Windows)
        {
            TryAttachToWindow(window);
        }

        // Hook to attach to future windows when they activate
        _windowActivatedHandler = (_, __) =>
        {
            foreach (Window window in Application.Current.Windows)
            {
                TryAttachToWindow(window);
            }
        };
        Application.Current.Activated += _windowActivatedHandler;
    }

    public static void Detach()
    {
        if (!_isActive) return;
        _isActive = false;

        if (_windowActivatedHandler != null)
        {
            Application.Current.Activated -= _windowActivatedHandler;
            _windowActivatedHandler = null;
        }

        foreach (Window window in Application.Current.Windows)
        {
            TryDetachFromWindow(window);
        }
    }

    private static void TryAttachToWindow(Window window)
    {
        try
        {
            // Skip if already attached
            if (FindOverlay(window) != null) return;

            var originalContent = window.Content as UIElement;
            if (originalContent == null) return;

            // Remove original content from window
            window.Content = null;

            // Build wrapper: Grid with the original content at z=0 and overlay at z=9999
            var wrapper = new Grid();

            // Re-add original content
            wrapper.Children.Add(originalContent);

            // Build the scanline overlay rectangle
            var overlay = new Rectangle
            {
                IsHitTestVisible = false,
                Tag = OverlayNameTag,
            };
            overlay.SetValue(Panel.ZIndexProperty, 9999);

            // Look up the keyed ScanlineOverlayBrush from app resources
            var brush = Application.Current.TryFindResource("ScanlineOverlayBrush");
            if (brush is Brush b)
            {
                overlay.Fill = b;
            }
            else
            {
                // Brush not loaded yet — build a minimal one inline as fallback
                var fallback = new DrawingBrush
                {
                    TileMode = TileMode.Tile,
                    Viewport = new Rect(0, 0, 1, 4),
                    ViewportUnits = BrushMappingMode.Absolute,
                    Stretch = Stretch.None,
                };
                var dg = new DrawingGroup();
                dg.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 1, 2))));
                dg.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), null, new RectangleGeometry(new Rect(0, 2, 1, 1))));
                fallback.Drawing = dg;
                overlay.Fill = fallback;
            }

            wrapper.Children.Add(overlay);

            // Set wrapper as new window content
            window.Content = wrapper;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "CyberpunkChrome: TryAttachToWindow failed");
        }
    }

    private static void TryDetachFromWindow(Window window)
    {
        try
        {
            if (window.Content is not Grid wrapper) return;
            var overlay = FindOverlay(window);
            if (overlay == null) return;

            // Find the original content (not the overlay)
            UIElement? originalContent = null;
            foreach (UIElement child in wrapper.Children)
            {
                if (!ReferenceEquals(child, overlay))
                {
                    originalContent = child;
                    break;
                }
            }

            if (originalContent == null) return;

            // Unwrap: remove from wrapper, set as window content directly
            wrapper.Children.Clear();
            window.Content = originalContent;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "CyberpunkChrome: TryDetachFromWindow failed");
        }
    }

    private static Rectangle? FindOverlay(Window window)
    {
        if (window.Content is not Grid wrapper) return null;
        foreach (UIElement child in wrapper.Children)
        {
            if (child is Rectangle rect && string.Equals(rect.Tag as string, OverlayNameTag, StringComparison.Ordinal))
                return rect;
        }
        return null;
    }
}
