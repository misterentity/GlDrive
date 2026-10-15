using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GlDrive.UI;

/// <summary>
/// The stream matrix's sparkline look (grid, filled area, crisp line, live head, PEAK label) for
/// the existing <see cref="PointCollection"/> charts: Overview throughput and Spread race cards.
/// Points use the view models' fixed chart space (Y = 0 top .. <see cref="YRange"/> bottom).
/// Redraws only when the collection is replaced or mutated, or when a palette resource changes
/// (theme switch); no timer.
/// </summary>
public sealed class TelemetrySparkline : FrameworkElement
{
    private static readonly Brush FallbackLine = Freeze(new SolidColorBrush(Color.FromRgb(0x65, 0xE6, 0xC1)));
    private static readonly Brush FallbackGrid = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)));
    private static readonly FontFamily FallbackFont = new("Consolas");
    // Pens are cached only for brushes that were already frozen: freezing a Pen freezes its
    // brush, and the palette brushes belong to the theme dictionaries (or the caller).
    private readonly Dictionary<(Brush, double), Pen> _pens = new();

    // Palette: resource references by default, so a theme swap updates these values and the
    // AffectsRender metadata repaints an open chart even when no data arrives.
    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(TelemetrySparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(TelemetrySparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(TelemetrySparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(TelemetrySparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelFontFamilyProperty = DependencyProperty.Register(
        nameof(LabelFontFamily), typeof(FontFamily), typeof(TelemetrySparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? LineBrush { get => (Brush?)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush? FillBrush { get => (Brush?)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush? LabelBrush { get => (Brush?)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public FontFamily? LabelFontFamily { get => (FontFamily?)GetValue(LabelFontFamilyProperty); set => SetValue(LabelFontFamilyProperty, value); }

    public TelemetrySparkline()
    {
        SetResourceReference(LineBrushProperty, "StreamSparkBrush");
        SetResourceReference(FillBrushProperty, "StreamSparkFillBrush");
        SetResourceReference(GridBrushProperty, "StreamGridLineBrush");
        SetResourceReference(LabelBrushProperty, "StreamTextDimBrush");
        SetResourceReference(LabelFontFamilyProperty, "StreamMonoFontFamily");
    }

    // A Freezable value with AffectsRender redraws on in-place edits too (Spread mutates its collection).
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(PointCollection), typeof(TelemetrySparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty YRangeProperty = DependencyProperty.Register(
        nameof(YRange), typeof(double), typeof(TelemetrySparkline),
        new FrameworkPropertyMetadata(40.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Value represented by the top of the chart; 0 hides the PEAK label (relative charts).</summary>
    public static readonly DependencyProperty ValueAtTopProperty = DependencyProperty.Register(
        nameof(ValueAtTop), typeof(double), typeof(TelemetrySparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(TelemetrySparkline),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpanLabelProperty = DependencyProperty.Register(
        nameof(SpanLabel), typeof(string), typeof(TelemetrySparkline),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public PointCollection? Points { get => (PointCollection?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public double YRange { get => (double)GetValue(YRangeProperty); set => SetValue(YRangeProperty, value); }
    public double ValueAtTop { get => (double)GetValue(ValueAtTopProperty); set => SetValue(ValueAtTopProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string SpanLabel { get => (string)GetValue(SpanLabelProperty); set => SetValue(SpanLabelProperty, value); }

    /// <summary>Highest plotted value as a 0..1 fraction of the chart height.</summary>
    internal static double PeakFraction(IEnumerable<Point>? points, double yRange)
    {
        if (points == null || yRange <= 0) return 0;
        double minY = double.MaxValue;
        foreach (var p in points) minY = Math.Min(minY, p.Y);
        return minY == double.MaxValue ? 0 : Math.Clamp((yRange - minY) / yRange, 0, 1);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 40 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var gridBrush = GridBrush ?? FallbackGrid;
        var line = LineBrush ?? FallbackLine;
        var fill = FillBrush;
        var text = LabelBrush ?? gridBrush;
        var font = LabelFontFamily ?? FallbackFont;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var gridPen = PenFor(gridBrush, 1);
        for (int i = 1; i < 4; i++)
        {
            double y = Math.Round(h * i / 4) + 0.5;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }
        for (int i = 1; i < 8; i++)
        {
            double x = Math.Round(w * i / 8) + 0.5;
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
        }

        var points = Points;
        double range = YRange > 0 ? YRange : 40;
        if (points == null || points.Count < 2)
        {
            if (h >= 24) DrawLabel(dc, "AWAITING SIGNAL…", text, font, dpi, 4, h / 2 - 6);
            return;
        }

        double minX = double.MaxValue, maxX = double.MinValue;
        foreach (var p in points) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); }
        double spanX = Math.Max(maxX - minX, 1);
        Point Map(Point p) => new(
            (p.X - minX) / spanX * (w - 1),
            2 + Math.Clamp(p.Y / range, 0, 1) * (h - 4));

        var lineGeo = Series(points, Map, closed: false, h);
        if (fill != null) dc.DrawGeometry(fill, null, Series(points, Map, closed: true, h));

        var last = Map(points[points.Count - 1]);
        bool live = points[points.Count - 1].Y < range;
        if (live)
        {
            // The only glow on this chart, and only while the latest sample is non-zero.
            dc.PushOpacity(0.22);
            dc.DrawGeometry(null, PenFor(line, 5), lineGeo);
            dc.Pop();
        }
        dc.DrawGeometry(null, PenFor(line, 1.6), lineGeo);
        dc.DrawEllipse(live ? line : gridBrush, null, last, 2.5, 2.5);

        if (h < 30) return;
        if (ValueAtTop > 0)
        {
            var peak = PeakFraction(points, range) * ValueAtTop;
            DrawLabel(dc, $"PEAK {peak.ToString("0.0", CultureInfo.InvariantCulture)} {Unit}".TrimEnd(), text, font, dpi, 4, 2);
        }
        if (!string.IsNullOrEmpty(SpanLabel)) DrawLabel(dc, SpanLabel, text, font, dpi, 4, h - 13);
    }

    private static StreamGeometry Series(PointCollection points, Func<Point, Point> map, bool closed, double h)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var first = map(points[0]);
            ctx.BeginFigure(closed ? new Point(first.X, h) : first, closed, closed);
            if (closed) ctx.LineTo(first, false, false);
            for (int i = 1; i < points.Count; i++) ctx.LineTo(map(points[i]), true, true);
            if (closed) ctx.LineTo(new Point(map(points[points.Count - 1]).X, h), false, false);
        }
        geo.Freeze();
        return geo;
    }

    private static void DrawLabel(DrawingContext dc, string s, Brush brush, FontFamily font, double dpi, double x, double y)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 9, brush, dpi);
        dc.DrawText(ft, new Point(x, y));
    }

    private Pen PenFor(Brush brush, double thickness)
    {
        // A mutable brush gets a fresh, unfrozen pen; freezing it would freeze the brush too.
        if (!brush.IsFrozen) return new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
        if (_pens.Count > 32) _pens.Clear();
        if (!_pens.TryGetValue((brush, thickness), out var pen))
        {
            pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            _pens[(brush, thickness)] = pen;
        }
        return pen;
    }

    private static Brush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
