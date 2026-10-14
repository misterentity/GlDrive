using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using GlDrive.Downloads;
using GlDrive.Downloads.Segmented;

namespace GlDrive.UI.StreamMatrix;

/// <summary>
/// Live state for one download's STREAM MATRIX panel and its lane chips. Fed from the
/// throttled progress reports (about 2 Hz); the grid and sparkline redraw when
/// <see cref="Version"/> changes.
/// </summary>
public sealed class StreamMatrixViewModel : INotifyPropertyChanged
{
    public const int CellCount = 384;
    public const int Columns = 64;
    private static readonly TimeSpan MinUpdateInterval = TimeSpan.FromMilliseconds(200);

    private readonly double[] _laneRates = new double[LanePalette.Size];
    private DateTime _lastUpdate = DateTime.MinValue;
    private int _version;
    private string _headerLinks = "";
    private string _headerAgg = "";
    private string _headerEta = "";
    private string _statsLine = "";
    private string _currentFile = "";
    private double _percent;

    private readonly Func<DateTime> _clock;

    public StreamMatrixViewModel(string releaseName = "", Func<DateTime>? clock = null)
    {
        ReleaseName = releaseName;
        _clock = clock ?? (() => DateTime.UtcNow);
        Grid = new SegmentGridModel(CellCount, Palette);
    }

    public string ReleaseName { get; }
    public LanePalette Palette { get; } = new();
    public SegmentGridModel Grid { get; }
    public ThroughputHistory History { get; } = new();
    public ObservableCollection<LaneRowVm> Lanes { get; } = new();
    public ObservableCollection<LaneChipVm> Chips { get; } = new();
    public bool HasMap { get; private set; }

    public int Version { get => _version; private set => Set(ref _version, value); }
    public string HeaderLinks { get => _headerLinks; private set => Set(ref _headerLinks, value); }
    public string HeaderAgg { get => _headerAgg; private set => Set(ref _headerAgg, value); }
    public string HeaderEta { get => _headerEta; private set => Set(ref _headerEta, value); }
    public string StatsLine { get => _statsLine; private set => Set(ref _statsLine, value); }
    public string CurrentFile { get => _currentFile; private set => Set(ref _currentFile, value); }
    public double Percent { get => _percent; private set => Set(ref _percent, value); }

    public string HeaderText => $"▚ STREAM MATRIX // {HeaderLinks} // AGG {HeaderAgg} // ETA {HeaderEta}";

    /// <summary>Apply one progress report. Returns false when it was dropped by the redraw throttle.</summary>
    public bool Update(DownloadProgress p, bool force = false)
    {
        var now = _clock();
        if (!force && now - _lastUpdate < MinUpdateInterval) return false;
        _lastUpdate = now;

        var lanes = p.Lanes ?? [];
        int links = lanes.Count(l => l.State is LaneState.Active or LaneState.Connecting);
        HeaderLinks = links == 1 ? "1 LINK" : $"{links} LINKS";
        HeaderAgg = FormatRate(p.BytesPerSecond);
        HeaderEta = p.BytesPerSecond > 0 && p.TotalBytes > p.DownloadedBytes
            ? FormatEta(TimeSpan.FromSeconds((p.TotalBytes - p.DownloadedBytes) / p.BytesPerSecond))
            : p.TotalBytes > 0 && p.DownloadedBytes >= p.TotalBytes ? "00:00:00" : "--:--:--";
        OnPropertyChanged(nameof(HeaderText));
        Percent = p.TotalBytes > 0 ? 100.0 * p.DownloadedBytes / p.TotalBytes : 0;
        CurrentFile = p.CurrentFileName ?? "";

        if (p.Map != null)
        {
            Grid.Update(p.Map);
            HasMap = true;
            int files = p.Map.Files.Count;
            long cached = 0;
            foreach (var c in Grid.Cells) if (c.Kind == CellKind.Cached) cached++;
            var cachedBytes = Grid.CellCount > 0 ? Grid.TotalBytes * cached / Grid.CellCount : 0;
            StatsLine = $"{files} {(files == 1 ? "FILE" : "FILES")} · {FormatBytes(p.TotalBytes)} · {Percent:0.0}% SYNCED"
                        + (cachedBytes > 0 ? $" · {FormatBytes(cachedBytes)} CACHED" : "");
        }

        Array.Clear(_laneRates);
        for (int i = 0; i < lanes.Count; i++)
        {
            var l = lanes[i];
            int slot = Palette.IndexOf(l.Label);
            if (l.State is LaneState.Active or LaneState.Connecting) _laneRates[slot] += l.BytesPerSecond;
            var row = i < Lanes.Count ? Lanes[i] : null;
            if (row == null) Lanes.Add(row = new LaneRowVm());
            row.Apply(i, slot, l);
        }
        while (Lanes.Count > lanes.Count) Lanes.RemoveAt(Lanes.Count - 1);
        History.Add(p.BytesPerSecond, _laneRates);

        UpdateChips(lanes);
        Version++;
        return true;
    }

    // One chip per label: live lanes sum their rates; a label with no live lane shows its last state.
    private void UpdateChips(IReadOnlyList<LaneStats> lanes)
    {
        int n = 0;
        foreach (var label in lanes.Select(l => l.Label).Distinct())
        {
            var group = lanes.Where(l => l.Label == label).ToList();
            var live = group.Where(l => l.State is LaneState.Active or LaneState.Connecting or LaneState.Idle).ToList();
            var chip = n < Chips.Count ? Chips[n] : null;
            if (chip == null) Chips.Add(chip = new LaneChipVm());
            int slot = Palette.IndexOf(label);
            if (live.Count > 0)
            {
                var rate = live.Sum(l => l.BytesPerSecond);
                var count = live.Count > 1 ? $" ×{live.Count}" : "";
                chip.Apply(slot, "◢", label.ToUpperInvariant() + count, rate > 0 ? FormatRate(rate) : "", true);
            }
            else
            {
                var state = group[^1].State;
                chip.Apply(slot, LaneRowVm.Glyph(state), label.ToUpperInvariant(), LaneRowVm.StateName(state), false);
            }
            n++;
        }
        while (Chips.Count > n) Chips.RemoveAt(Chips.Count - 1);
    }

    /// <summary>Resolve a palette slot against the current theme (LaneBrush0..5).</summary>
    public static Brush LaneBrush(int slot)
    {
        // Off the UI thread (tests, early startup) the app's resources aren't ours to touch.
        var app = Application.Current;
        var brush = app != null && app.Dispatcher.CheckAccess()
            ? app.TryFindResource(LaneBrushKeys[slot % LanePalette.Size]) as Brush
            : null;
        return brush ?? FallbackBrushes[slot % LanePalette.Size];
    }

    public static readonly string[] LaneBrushKeys = ["LaneBrush0", "LaneBrush1", "LaneBrush2", "LaneBrush3", "LaneBrush4", "LaneBrush5"];

    private static readonly Brush[] FallbackBrushes = new[] { "#00E5FF", "#FF2AB8", "#7CFF66", "#FFAB1F", "#B388FF", "#FF5E3A" }
        .Select(h => { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h)); b.Freeze(); return (Brush)b; })
        .ToArray();

    public static string FormatRate(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 * 1024 => $"{bytesPerSecond / (1024.0 * 1024 * 1024):0.00} GB/s",
        >= 1024 * 1024 => $"{bytesPerSecond / (1024.0 * 1024):0.0} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024.0:0} KB/s",
        _ => $"{bytesPerSecond:0} B/s",
    };

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.00} TiB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MiB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KiB",
        _ => $"{bytes} B",
    };

    public static string FormatEta(TimeSpan t) =>
        t.TotalHours >= 100 ? "99:59:59" : $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }
}

/// <summary>One row of the lane table.</summary>
public sealed class LaneRowVm : INotifyPropertyChanged
{
    private string _lane = "", _net = "", _stateText = "", _rate = "", _bytes = "";
    private LaneState _state;
    private Brush? _brush;
    private Color _color;

    public string Lane { get => _lane; private set => Set(ref _lane, value); }
    public string Net { get => _net; private set => Set(ref _net, value); }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public LaneState State { get => _state; private set => Set(ref _state, value); }
    public string Rate { get => _rate; private set => Set(ref _rate, value); }
    public string Bytes { get => _bytes; private set => Set(ref _bytes, value); }
    public Brush? Brush { get => _brush; private set => Set(ref _brush, value); }
    public Color Color { get => _color; private set => Set(ref _color, value); }

    internal void Apply(int index, int slot, LaneStats l)
    {
        Lane = $"{index:00} {l.Label.ToUpperInvariant()}";
        Net = FormatNet(l.NetworkId);
        State = l.State;
        StateText = $"{Glyph(l.State)} {StateName(l.State)}";
        Rate = l.State is LaneState.Active or LaneState.Connecting ? StreamMatrixViewModel.FormatRate(l.BytesPerSecond) : "—";
        Bytes = StreamMatrixViewModel.FormatBytes(l.Bytes);
        Brush = StreamMatrixViewModel.LaneBrush(slot);
        Color = (Brush as SolidColorBrush)?.Color ?? Colors.Cyan;
    }

    // Adapter ids are GUIDs; the first block is enough to tell uplinks apart at a glance.
    internal static string FormatNet(string? networkId)
    {
        if (string.IsNullOrEmpty(networkId) || networkId == NetworkPath.DefaultId) return "DEFAULT";
        var id = networkId.Trim('{', '}');
        int dash = id.IndexOf('-');
        return (dash > 0 ? id[..dash] : id.Length > 8 ? id[..8] : id).ToUpperInvariant();
    }

    public static string Glyph(LaneState s) => s switch
    {
        LaneState.Active => "▶",
        LaneState.Idle => "◌",
        LaneState.Yielded => "⇄",
        LaneState.Failed => "✕",
        LaneState.Done => "✓",
        _ => "…",
    };

    public static string StateName(LaneState s) => s switch
    {
        LaneState.Active => "ACTIVE",
        LaneState.Idle => "IDLE",
        LaneState.Yielded => "YIELDED",
        LaneState.Failed => "FAILED",
        LaneState.Done => "DONE",
        _ => "CONNECTING",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A neon lane chip on a downloads row, e.g. "◢ ETHERNET 42.1 MB/s".</summary>
public sealed class LaneChipVm : INotifyPropertyChanged
{
    private string _glyph = "", _label = "", _detail = "";
    private bool _isLive;
    private Brush? _brush;
    private Color _color;

    public string Glyph { get => _glyph; private set => Set(ref _glyph, value); }
    public string Label { get => _label; private set => Set(ref _label, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public bool IsLive { get => _isLive; private set => Set(ref _isLive, value); }
    public Brush? Brush { get => _brush; private set => Set(ref _brush, value); }
    public Color Color { get => _color; private set => Set(ref _color, value); }

    internal void Apply(int slot, string glyph, string label, string detail, bool live)
    {
        Glyph = glyph;
        Label = label;
        Detail = detail;
        IsLive = live;
        Brush = StreamMatrixViewModel.LaneBrush(slot);
        Color = (Brush as SolidColorBrush)?.Color ?? Colors.Cyan;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
