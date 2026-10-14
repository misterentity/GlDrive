namespace GlDrive.UI.StreamMatrix;

/// <summary>
/// Fixed-size ring of throughput samples: the aggregate plus one series per palette slot.
/// Sized for about a minute of progress reports.
/// </summary>
public sealed class ThroughputHistory
{
    private readonly double[] _aggregate;
    private readonly double[][] _lanes;
    private readonly bool[] _laneSeen = new bool[LanePalette.Size];
    private int _next;

    public ThroughputHistory(int capacity = 120)
    {
        Capacity = capacity;
        _aggregate = new double[capacity];
        _lanes = new double[LanePalette.Size][];
        for (int i = 0; i < _lanes.Length; i++) _lanes[i] = new double[capacity];
    }

    public int Capacity { get; }
    public int Count { get; private set; }
    public double Peak { get; private set; }

    /// <summary>Appends one sample. <paramref name="perLane"/> is indexed by palette slot.</summary>
    public void Add(double aggregate, ReadOnlySpan<double> perLane)
    {
        _aggregate[_next] = aggregate;
        for (int i = 0; i < _lanes.Length; i++)
        {
            double v = i < perLane.Length ? perLane[i] : 0;
            _lanes[i][_next] = v;
            if (v > 0) _laneSeen[i] = true;
        }
        _next = (_next + 1) % Capacity;
        if (Count < Capacity) Count++;

        Peak = 0;
        for (int i = 0; i < Count; i++) Peak = Math.Max(Peak, _aggregate[i]);
    }

    /// <summary>Oldest-first aggregate sample <paramref name="i"/> (0..Count-1).</summary>
    public double Aggregate(int i) => _aggregate[Index(i)];

    public double Lane(int slot, int i) => _lanes[slot][Index(i)];

    public bool HasLane(int slot) => _laneSeen[slot];

    private int Index(int i) => (_next - Count + i + Capacity) % Capacity;
}
