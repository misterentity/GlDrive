using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlDrive.Services;

namespace GlDrive.UI;

public static class CyberpunkIconGenerator
{
    private const double S = 256.0;

    public static RenderTargetBitmap Generate(MountState state)
    {
        var accent = GetAccentColor(state);
        var glow = Color.FromArgb(0x40, accent.R, accent.G, accent.B);
        var dimAccent = Color.FromArgb(0x18, accent.R, accent.G, accent.B);
        var midAccent = Color.FromArgb(0x80, accent.R, accent.G, accent.B);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Dark background
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x06, 0x00, 0x10)), null,
                new Rect(0, 0, S, S));

            // Subtle grid
            var gridPen = new Pen(new SolidColorBrush(dimAccent), 1);
            gridPen.Freeze();
            for (var i = 0.0; i < S; i += 32)
            {
                dc.DrawLine(gridPen, new Point(i, 0), new Point(i, S));
                dc.DrawLine(gridPen, new Point(0, i), new Point(S, i));
            }

            // Scanlines
            var scanPen = new Pen(new SolidColorBrush(Color.FromArgb(0x12, 0, 0, 0)), 2);
            scanPen.Freeze();
            for (var y = 0.0; y < S; y += 6)
                dc.DrawLine(scanPen, new Point(0, y), new Point(S, y));

            // Glyph glow (thick, translucent)
            var geo = CreateGlyph();
            var glowPen = new Pen(new SolidColorBrush(glow), 20)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            glowPen.Freeze();
            dc.DrawGeometry(null, glowPen, geo);

            // Glyph main stroke
            var mainPen = new Pen(new SolidColorBrush(accent), 7)
            { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square, LineJoin = PenLineJoin.Miter };
            mainPen.Freeze();
            dc.DrawGeometry(null, mainPen, geo);

            // Endpoint dots
            var dotBrush = new SolidColorBrush(accent);
            dotBrush.Freeze();
            dc.DrawEllipse(dotBrush, null, new Point(200, 50), 6, 6);
            dc.DrawEllipse(dotBrush, null, new Point(138, 122), 6, 6);

            // Circuit accents — top-right
            var tracePen = new Pen(new SolidColorBrush(midAccent), 2);
            tracePen.Freeze();
            dc.DrawLine(tracePen, new Point(212, 38), new Point(238, 38));
            dc.DrawLine(tracePen, new Point(238, 38), new Point(244, 28));
            dc.DrawEllipse(dotBrush, null, new Point(246, 25), 4, 4);

            // Circuit accents — bottom-left
            dc.DrawLine(tracePen, new Point(38, 218), new Point(18, 218));
            dc.DrawLine(tracePen, new Point(18, 218), new Point(12, 228));
            dc.DrawEllipse(dotBrush, null, new Point(10, 232), 4, 4);

            // Thin outer border
            var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B)), 2);
            borderPen.Freeze();
            dc.DrawRectangle(null, borderPen, new Rect(1, 1, S - 2, S - 2));
        }

        var rtb = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static PathGeometry CreateGlyph()
    {
        // Angular cyberpunk "G" with beveled corners
        var figure = new PathFigure { StartPoint = new Point(200, 50), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new LineSegment(new Point(72, 50), true));      // top edge
        figure.Segments.Add(new LineSegment(new Point(50, 72), true));      // top-left bevel
        figure.Segments.Add(new LineSegment(new Point(50, 184), true));     // left edge
        figure.Segments.Add(new LineSegment(new Point(72, 206), true));     // bottom-left bevel
        figure.Segments.Add(new LineSegment(new Point(184, 206), true));    // bottom edge
        figure.Segments.Add(new LineSegment(new Point(206, 184), true));    // bottom-right bevel
        figure.Segments.Add(new LineSegment(new Point(206, 122), true));    // right edge to mid
        figure.Segments.Add(new LineSegment(new Point(138, 122), true));    // mid bar inward

        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        geo.Freeze();
        return geo;
    }

    private static Color GetAccentColor(MountState state) => state switch
    {
        MountState.Connected => Color.FromRgb(0x00, 0xE5, 0xFF),       // neon cyan
        MountState.Connecting => Color.FromRgb(0xFF, 0xD6, 0x00),      // neon yellow
        MountState.Reconnecting => Color.FromRgb(0xFF, 0x6D, 0x00),    // neon orange
        MountState.Error => Color.FromRgb(0xFF, 0x17, 0x44),           // neon red
        _ => Color.FromRgb(0x55, 0x55, 0x55)                           // dim gray (unmounted)
    };
}
