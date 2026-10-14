using GlDrive.Downloads.Segmented;

namespace GlDrive.UI.StreamMatrix;

/// <summary>
/// Assigns each lane label a palette slot (LaneBrush0..5) in first-seen order, so the grid,
/// the lane table and the row chips agree on a lane's color for the whole download.
/// </summary>
public sealed class LanePalette
{
    public const int Size = 6;
    private readonly List<string> _order = new();

    public int IndexOf(string label)
    {
        int i = _order.IndexOf(label);
        if (i < 0)
        {
            _order.Add(label);
            i = _order.Count - 1;
        }
        return i % Size;
    }

    public IReadOnlyList<string> Labels => _order;
}

public enum CellKind : byte { Pending, Partial, Done, Cached }

/// <summary>One grid cell. <see cref="Owner"/> is a palette slot, or -1 when nobody fetched it this session.</summary>
public struct SegmentCell
{
    public CellKind Kind;
    public sbyte Owner;
    public float Fill;
}

/// <summary>A lane's write head: the cell its next byte lands in.</summary>
public readonly record struct HeadMarker(int Cell, int Owner);

/// <summary>
/// Pure model behind the stream-matrix grid: maps a <see cref="TransferMap"/> onto a fixed
/// number of cells spread across the transfer's bytes (file after file) and remembers which
/// lane completed each cell. Owner attribution: a cell that becomes done while it lies inside
/// lane X's written span [Start, Position) belongs to X; cells already done on the first map
/// (resume) are "cached". Reuses its arrays between updates.
/// </summary>
public sealed class SegmentGridModel
{
    private readonly List<ByteRange> _globalDone = new();
    private readonly List<(long Start, long Position, long End, int Owner)> _spans = new();
    private readonly List<(long Start, long End, int Owner)> _previousSpans = new();
    private readonly List<HeadMarker> _heads = new();
    private readonly List<double> _fileBoundaries = new();
    private long _total = -1;
    private bool _hasState;

    public SegmentGridModel(int cellCount, LanePalette? palette = null)
    {
        if (cellCount <= 0) throw new ArgumentOutOfRangeException(nameof(cellCount));
        Cells = new SegmentCell[cellCount];
        Palette = palette ?? new LanePalette();
        Reset();
    }

    public SegmentCell[] Cells { get; }
    public int CellCount => Cells.Length;
    public LanePalette Palette { get; }
    public long TotalBytes => Math.Max(0, _total);
    public IReadOnlyList<HeadMarker> Heads => _heads;

    /// <summary>Fractional cell positions where a new file starts (the first file is omitted).</summary>
    public IReadOnlyList<double> FileBoundaries => _fileBoundaries;

    public void Update(TransferMap map)
    {
        int n = Cells.Length;
        var files = map.Files;
        Span<long> starts = files.Count <= 256 ? stackalloc long[files.Count] : new long[files.Count];
        long total = 0;
        for (int i = 0; i < files.Count; i++)
        {
            starts[i] = total;
            total += Math.Max(0, files[i].Size);
        }

        if (total != _total)
        {
            // Sizes changed (unknown size resolved) — positions no longer line up with old owners.
            _total = total;
            Reset();
        }

        _fileBoundaries.Clear();
        if (total > 0)
            for (int i = 1; i < files.Count; i++)
                if (files[i].Size > 0 && starts[i] > 0)
                    _fileBoundaries.Add((double)starts[i] * n / total);

        _globalDone.Clear();
        for (int i = 0; i < files.Count; i++)
        {
            long size = files[i].Size;
            if (size <= 0) continue;
            foreach (var r in files[i].Done)
            {
                long s = Math.Clamp(r.Start, 0, size), e = Math.Clamp(r.End, 0, size);
                if (e > s) _globalDone.Add(new ByteRange(starts[i] + s, starts[i] + e));
            }
        }

        _spans.Clear();
        _heads.Clear();
        foreach (var seg in map.InFlight)
        {
            if (seg.FileIndex < 0 || seg.FileIndex >= files.Count) continue;
            long size = files[seg.FileIndex].Size;
            if (size <= 0) continue;
            long b = starts[seg.FileIndex];
            long start = b + Math.Clamp(seg.Start, 0, size);
            long pos = b + Math.Clamp(seg.Position, 0, size);
            long end = b + Math.Clamp(seg.End, 0, size);
            int owner = Palette.IndexOf(seg.Lane);
            _spans.Add((start, pos, end, owner));
            if (pos < end && total > 0)
                _heads.Add(new HeadMarker(CellOf(pos, total, n), owner));
        }

        if (total <= 0)
        {
            for (int c = 0; c < n; c++) Cells[c] = new SegmentCell { Kind = CellKind.Cached, Owner = -1, Fill = 1 };
            _hasState = true;
            SavePreviousSpans();
            return;
        }

        int j = 0;
        for (int c = 0; c < n; c++)
        {
            long cs = c * total / n;
            long ce = Math.Max(cs + 1, (c + 1) * total / n);
            while (j < _globalDone.Count && _globalDone[j].End <= cs) j++;
            long covered = 0;
            for (int k = j; k < _globalDone.Count && _globalDone[k].Start < ce; k++)
                covered += Math.Min(ce, _globalDone[k].End) - Math.Max(cs, _globalDone[k].Start);

            ref var cell = ref Cells[c];
            float fill = (float)covered / (ce - cs);
            var previous = cell.Kind;

            if (covered >= ce - cs)
            {
                if (previous is CellKind.Done or CellKind.Cached)
                {
                    cell.Fill = 1;
                    continue;
                }
                int owner = OwnerOf(cs, ce);
                if (owner < 0 && _hasState) owner = PreviousOwnerOf(cs, ce);
                if (owner < 0 && _hasState && previous == CellKind.Partial) owner = cell.Owner;
                if (owner < 0 && _hasState && c > 0 && Cells[c - 1].Owner >= 0) owner = Cells[c - 1].Owner;
                cell.Kind = owner >= 0 ? CellKind.Done : CellKind.Cached;
                cell.Owner = (sbyte)owner;
                cell.Fill = 1;
            }
            else if (covered > 0)
            {
                int owner = OwnerOf(cs, ce);
                cell.Kind = CellKind.Partial;
                if (owner >= 0) cell.Owner = (sbyte)owner;
                else if (previous != CellKind.Partial) cell.Owner = -1;
                cell.Fill = fill;
            }
            else
            {
                cell.Kind = CellKind.Pending;
                cell.Owner = -1;
                cell.Fill = 0;
            }
        }

        _hasState = true;
        SavePreviousSpans();
    }

    private static int CellOf(long globalOffset, long total, int n) =>
        (int)Math.Clamp(globalOffset * n / total, 0, n - 1);

    // Lane whose written span [Start, Position) overlaps the cell most.
    private int OwnerOf(long cs, long ce)
    {
        int best = -1;
        long bestOverlap = 0;
        foreach (var s in _spans)
        {
            long overlap = Math.Min(ce, s.Position) - Math.Max(cs, s.Start);
            if (overlap > bestOverlap) { bestOverlap = overlap; best = s.Owner; }
        }
        return best;
    }

    // A lane that finished its range between ticks is gone from the map; its last known claim decides.
    private int PreviousOwnerOf(long cs, long ce)
    {
        int best = -1;
        long bestOverlap = 0;
        foreach (var s in _previousSpans)
        {
            long overlap = Math.Min(ce, s.End) - Math.Max(cs, s.Start);
            if (overlap > bestOverlap) { bestOverlap = overlap; best = s.Owner; }
        }
        return best;
    }

    private void SavePreviousSpans()
    {
        _previousSpans.Clear();
        foreach (var s in _spans) _previousSpans.Add((s.Start, s.End, s.Owner));
    }

    private void Reset()
    {
        for (int c = 0; c < Cells.Length; c++) Cells[c] = new SegmentCell { Kind = CellKind.Pending, Owner = -1 };
        _previousSpans.Clear();
        _hasState = false;
    }
}
