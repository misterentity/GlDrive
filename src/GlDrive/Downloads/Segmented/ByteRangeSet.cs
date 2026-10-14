namespace GlDrive.Downloads.Segmented;

/// <summary>
/// Sorted, merged set of half-open byte ranges. Adjacent and overlapping ranges coalesce on
/// add. Not thread-safe — callers lock.
/// </summary>
public sealed class ByteRangeSet
{
    private readonly List<ByteRange> _ranges = new();

    public ByteRangeSet() { }

    public ByteRangeSet(IEnumerable<ByteRange> ranges)
    {
        foreach (var r in ranges) Add(r);
    }

    public IReadOnlyList<ByteRange> Ranges => _ranges;

    public long TotalLength { get; private set; }

    public ByteRangeSet Clone() => new(_ranges);

    /// <summary>Adds a range; returns how many bytes were newly covered.</summary>
    public long Add(ByteRange range)
    {
        if (range.IsEmpty) return 0;

        // First range whose End reaches range.Start (touching counts as mergeable).
        int i = 0;
        while (i < _ranges.Count && _ranges[i].End < range.Start) i++;

        long start = range.Start, end = range.End, absorbed = 0;
        int j = i;
        while (j < _ranges.Count && _ranges[j].Start <= end)
        {
            start = Math.Min(start, _ranges[j].Start);
            end = Math.Max(end, _ranges[j].End);
            absorbed += _ranges[j].Length;
            j++;
        }

        _ranges.RemoveRange(i, j - i);
        _ranges.Insert(i, new ByteRange(start, end));
        long added = (end - start) - absorbed;
        TotalLength += added;
        return added;
    }

    public bool Contains(long offset)
    {
        foreach (var r in _ranges)
        {
            if (offset < r.Start) return false;
            if (offset < r.End) return true;
        }
        return false;
    }

    public bool Covers(ByteRange range)
    {
        if (range.IsEmpty) return true;
        foreach (var r in _ranges)
        {
            if (r.Start > range.Start) return false;
            if (r.End >= range.End) return true;
            if (r.End > range.Start) return false; // covers the start but stops short
        }
        return false;
    }

    /// <summary>The missing ranges within [0, size).</summary>
    public List<ByteRange> Gaps(long size)
    {
        var gaps = new List<ByteRange>();
        long cursor = 0;
        foreach (var r in _ranges)
        {
            if (cursor >= size) break;
            if (r.Start > cursor) gaps.Add(new ByteRange(cursor, Math.Min(r.Start, size)));
            cursor = Math.Max(cursor, r.End);
        }
        if (cursor < size) gaps.Add(new ByteRange(cursor, size));
        return gaps;
    }
}
