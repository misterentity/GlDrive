using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using GlDrive.Services;

namespace GlDrive.UI;

public static class CyberpunkIconGenerator
{
    private const int S = 256;

    public static Icon Generate(MountState state)
    {
        var accent = GetAccentColor(state);
        var glow = Color.FromArgb(0x40, accent.R, accent.G, accent.B);
        var dimAccent = Color.FromArgb(0x18, accent.R, accent.G, accent.B);
        var midAccent = Color.FromArgb(0x80, accent.R, accent.G, accent.B);

        using var bmp = new Bitmap(S, S);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        // Dark background
        using (var bgBrush = new SolidBrush(Color.FromArgb(0x06, 0x00, 0x10)))
            g.FillRectangle(bgBrush, 0, 0, S, S);

        // Subtle grid
        using (var gridPen = new Pen(dimAccent, 1))
        {
            for (var i = 0; i < S; i += 32)
            {
                g.DrawLine(gridPen, i, 0, i, S);
                g.DrawLine(gridPen, 0, i, S, i);
            }
        }

        // Scanlines
        using (var scanPen = new Pen(Color.FromArgb(0x12, 0, 0, 0), 2))
        {
            for (var y = 0; y < S; y += 6)
                g.DrawLine(scanPen, 0, y, S, y);
        }

        // Glyph points
        var glyphPoints = new PointF[]
        {
            new(200, 50), new(72, 50), new(50, 72), new(50, 184),
            new(72, 206), new(184, 206), new(206, 184), new(206, 122), new(138, 122)
        };

        // Glow
        using (var glowPen = new Pen(glow, 20) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(glowPen, glyphPoints);

        // Main stroke
        using (var mainPen = new Pen(accent, 7) { StartCap = LineCap.Square, EndCap = LineCap.Square, LineJoin = LineJoin.Miter })
            g.DrawLines(mainPen, glyphPoints);

        // Endpoint dots
        using (var dotBrush = new SolidBrush(accent))
        {
            g.FillEllipse(dotBrush, 194, 44, 12, 12);
            g.FillEllipse(dotBrush, 132, 116, 12, 12);

            // Circuit accents — top-right
            using (var tracePen = new Pen(midAccent, 2))
            {
                g.DrawLine(tracePen, 212, 38, 238, 38);
                g.DrawLine(tracePen, 238, 38, 244, 28);
            }
            g.FillEllipse(dotBrush, 242, 21, 8, 8);

            // Circuit accents — bottom-left
            using (var tracePen = new Pen(midAccent, 2))
            {
                g.DrawLine(tracePen, 38, 218, 18, 218);
                g.DrawLine(tracePen, 18, 218, 12, 228);
            }
            g.FillEllipse(dotBrush, 6, 228, 8, 8);
        }

        // Thin outer border
        using (var borderPen = new Pen(Color.FromArgb(0x30, accent.R, accent.G, accent.B), 2))
            g.DrawRectangle(borderPen, 1, 1, S - 3, S - 3);

        return Icon.FromHandle(bmp.GetHicon());
    }

    private static Color GetAccentColor(MountState state) => state switch
    {
        MountState.Connected => Color.FromArgb(0x00, 0xE5, 0xFF),
        MountState.Connecting => Color.FromArgb(0xFF, 0xD6, 0x00),
        MountState.Reconnecting => Color.FromArgb(0xFF, 0x6D, 0x00),
        MountState.Error => Color.FromArgb(0xFF, 0x17, 0x44),
        _ => Color.FromArgb(0x55, 0x55, 0x55)
    };
}
