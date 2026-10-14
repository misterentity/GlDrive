using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GlDrive.UI.StreamMatrix;

/// <summary>
/// About a minute of throughput drawn in one OnRender pass: thin per-lane lines in the lane
/// palette under a glowing aggregate line with a filled area. Redraws when <see cref="Version"/> changes.
/// </summary>
public sealed class ThroughputSparkline : FrameworkElement
{
    private static readonly Brush FallbackLine = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)));
    private static readonly Brush FallbackGrid = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)));
    private static readonly FontFamily FallbackFont = new("Consolas");

    // Pens are cached per brush so steady-state frames allocate only their geometry.
    private readonly Dictionary<(Brush, double), Pen> _pens = new();

    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(
        nameof(History), typeof(ThroughputHistory), typeof(ThroughputSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(ThroughputSparkline),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public ThroughputHistory? History { get => (ThroughputHistory?)GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 70 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var gridBrush = Res("StreamGridLineBrush") ?? FallbackGrid;
        var line = Res("StreamSparkBrush") ?? FallbackLine;
        var fill = Res("StreamSparkFillBrush");
        var text = Res("StreamTextDimBrush") ?? gridBrush;
        var font = TryFindResource("StreamMonoFontFamily") as FontFamily ?? FallbackFont;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var gridPen = PenFor(gridBrush, 1);
        for (int i = 1; i < 4; i++)
        {
            double y = Math.Round(h * i / 4) + 0.5;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }
        for (int i = 1; i < 6; i++)
        {
            double x = Math.Round(w * i / 6) + 0.5;
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
        }

        var history = History;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (history == null || history.Count < 2)
        {
            DrawLabel(dc, "AWAITING SIGNAL…", text, font, dpi, 4, h / 2 - 6);
            return;
        }

        double top = Math.Max(history.Peak, 1) * 1.15;
        double X(int i) => w * (i + history.Capacity - history.Count) / (history.Capacity - 1);
        double Y(double v) => h - 2 - (h - 6) * Math.Clamp(v / top, 0, 1);

        for (int slot = 0; slot < LanePalette.Size; slot++)
        {
            if (!history.HasLane(slot)) continue;
            var brush = Res(StreamMatrixViewModel.LaneBrushKeys[slot]) ?? StreamMatrixViewModel.LaneBrush(slot);
            var geo = Polyline(history.Count, i => new Point(X(i), Y(history.Lane(slot, i))), false, h);
            dc.PushOpacity(0.6);
            dc.DrawGeometry(null, PenFor(brush, 1), geo);
            dc.Pop();
        }

        var aggregate = Polyline(history.Count, i => new Point(X(i), Y(history.Aggregate(i))), false, h);
        if (fill != null)
            dc.DrawGeometry(fill, null, Polyline(history.Count, i => new Point(X(i), Y(history.Aggregate(i))), true, h));
        dc.PushOpacity(0.25);
        dc.DrawGeometry(null, PenFor(line, 6), aggregate);
        dc.Pop();
        dc.DrawGeometry(null, PenFor(line, 1.8), aggregate);

        var last = new Point(X(history.Count - 1), Y(history.Aggregate(history.Count - 1)));
        dc.DrawEllipse(line, null, last, 2.5, 2.5);

        DrawLabel(dc, "PEAK " + StreamMatrixViewModel.FormatRate(history.Peak), text, font, dpi, 4, 2);
        DrawLabel(dc, "-60s", text, font, dpi, 4, h - 14);
    }

    private static StreamGeometry Polyline(int count, Func<int, Point> point, bool closed, double h)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var first = point(0);
            ctx.BeginFigure(closed ? new Point(first.X, h) : first, closed, closed);
            if (closed) ctx.LineTo(first, false, false);
            for (int i = 1; i < count; i++) ctx.LineTo(point(i), true, true);
            if (closed) ctx.LineTo(new Point(point(count - 1).X, h), false, false);
        }
        geo.Freeze();
        return geo;
    }

    private void DrawLabel(DrawingContext dc, string s, Brush brush, FontFamily font, double dpi, double x, double y)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 9, brush, dpi);
        dc.DrawText(ft, new Point(x, y));
    }

    private Pen PenFor(Brush brush, double thickness)
    {
        if (_pens.Count > 64) _pens.Clear();
        if (!_pens.TryGetValue((brush, thickness), out var pen))
        {
            pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            _pens[(brush, thickness)] = pen;
        }
        return pen;
    }

    private Brush? Res(string key) => TryFindResource(key) as Brush;

    private static Brush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
