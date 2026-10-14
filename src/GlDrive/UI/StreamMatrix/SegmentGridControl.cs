using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlDrive.UI.StreamMatrix;

/// <summary>
/// Draws a <see cref="SegmentGridModel"/> as a block map in one OnRender pass (no element per
/// cell). Pending cells are dim, done cells take their lane's color, resumed bytes are
/// "cached", and each lane's write head pulses. Brushes come from the current theme on every
/// render, so a theme swap shows on the next frame.
/// </summary>
public sealed class SegmentGridControl : FrameworkElement
{
    private const double Gap = 2;
    private static readonly Brush HeadCore = Freeze(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush FallbackPending = Freeze(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)));
    private static readonly Brush FallbackCached = Freeze(new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)));

    private readonly DispatcherTimer _pulse;
    private readonly Brush[] _lanes = new Brush[LanePalette.Size];

    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(SegmentGridModel), typeof(SegmentGridControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(SegmentGridControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(SegmentGridControl),
        new FrameworkPropertyMetadata(StreamMatrixViewModel.Columns, FrameworkPropertyMetadataOptions.AffectsRender));

    public SegmentGridModel? Model { get => (SegmentGridModel?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

    /// <summary>0..1 head brightness; fixed in renders for screenshots, otherwise driven by the clock.</summary>
    public double? PulsePhase { get; set; }

    public SegmentGridControl()
    {
        SnapsToDevicePixels = true;
        // ~4 Hz pulse for the write heads; only ticks while the control is on screen.
        _pulse = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
        _pulse.Tick += (_, _) => { if (Model?.Heads.Count > 0) InvalidateVisual(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) _pulse.Start(); else _pulse.Stop(); };
        Unloaded += (_, _) => _pulse.Stop();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 80 : Math.Min(availableSize.Height, 80));

    protected override void OnRender(DrawingContext dc)
    {
        var model = Model;
        double w = ActualWidth, h = ActualHeight;
        if (model == null || w <= 0 || h <= 0) return;

        int n = model.CellCount;
        int cols = Math.Max(1, Columns);
        int rows = (n + cols - 1) / cols;
        double cw = (w - (cols - 1) * Gap) / cols;
        double ch = (h - (rows - 1) * Gap) / rows;
        if (cw <= 0 || ch <= 0) return;

        var pending = Res("StreamCellPendingBrush") ?? FallbackPending;
        var cached = Res("StreamCellCachedBrush") ?? FallbackCached;
        var lanes = _lanes;
        for (int i = 0; i < lanes.Length; i++)
            lanes[i] = Res(StreamMatrixViewModel.LaneBrushKeys[i]) ?? StreamMatrixViewModel.LaneBrush(i);

        var cells = model.Cells;
        for (int c = 0; c < n; c++)
        {
            var r = CellRect(c, cols, cw, ch);
            var cell = cells[c];
            switch (cell.Kind)
            {
                case CellKind.Done:
                    dc.DrawRectangle(cell.Owner >= 0 ? lanes[cell.Owner] : cached, null, r);
                    break;
                case CellKind.Cached:
                    dc.DrawRectangle(cached, null, r);
                    break;
                case CellKind.Partial:
                    dc.DrawRectangle(pending, null, r);
                    var part = new Rect(r.X, r.Y, Math.Max(1, r.Width * cell.Fill), r.Height);
                    dc.PushOpacity(0.8);
                    dc.DrawRectangle(cell.Owner >= 0 ? lanes[cell.Owner] : cached, null, part);
                    dc.Pop();
                    break;
                default:
                    dc.DrawRectangle(pending, null, r);
                    break;
            }
        }

        var separator = Res("StreamFileSeparatorBrush");
        if (separator != null)
        {
            foreach (var b in model.FileBoundaries)
            {
                int c = Math.Clamp((int)b, 0, n - 1);
                var r = CellRect(c, cols, cw, ch);
                double x = r.X + (b - c) * cw;
                if (b - c < 0.01) x = r.X - Gap / 2;
                dc.DrawRectangle(separator, null, new Rect(x - 0.75, r.Y - 1, 1.5, r.Height + 2));
            }
        }

        double phase = PulsePhase ?? (Environment.TickCount64 % 1000) / 1000.0;
        double glow = 0.55 + 0.45 * Math.Sin(phase * 2 * Math.PI);
        foreach (var head in model.Heads)
        {
            var r = CellRect(head.Cell, cols, cw, ch);
            var brush = lanes[head.Owner];
            dc.PushOpacity(0.3 * glow);
            dc.DrawRectangle(brush, null, Inflate(r, 5));
            dc.Pop();
            dc.PushOpacity(0.6 * glow);
            dc.DrawRectangle(brush, null, Inflate(r, 2));
            dc.Pop();
            dc.DrawRectangle(brush, null, r);
            dc.PushOpacity(0.35 + 0.65 * glow);
            dc.DrawRectangle(HeadCore, null, Inflate(r, -Math.Min(r.Width, r.Height) * 0.28));
            dc.Pop();
        }
    }

    private static Rect CellRect(int c, int cols, double cw, double ch) =>
        new((c % cols) * (cw + Gap), (c / cols) * (ch + Gap), cw, ch);

    private static Rect Inflate(Rect r, double by)
    {
        r.Inflate(by, by);
        return r;
    }

    private Brush? Res(string key) => TryFindResource(key) as Brush;

    private static Brush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
