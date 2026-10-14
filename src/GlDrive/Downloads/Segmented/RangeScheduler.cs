namespace GlDrive.Downloads.Segmented;

/// <summary>
/// Work-stealing range assignment over the files of one transfer. Hands out untouched gaps in
/// file order; once none are left, splits the largest in-flight remainder at its midpoint.
/// All state is guarded by one lock.
/// </summary>
public sealed class RangeScheduler
{
    public const long SplitAlignment = 64 * 1024;

    /// <summary>
    /// One lane's claim on a range. <see cref="End"/> can shrink when another lane steals the
    /// tail; read it through <see cref="RangeScheduler.CurrentEnd"/> or the property (volatile).
    /// </summary>
    public sealed class Assignment
    {
        private long _position;
        private long _end;

        internal Assignment(int fileIndex, long start, long end, bool canStopEarly, bool splittable)
        {
            FileIndex = fileIndex;
            Start = start;
            _position = start;
            _end = end;
            CanStopEarly = canStopEarly;
            Splittable = splittable;
        }

        public int FileIndex { get; }
        public long Start { get; }
        public long Position { get => Volatile.Read(ref _position); internal set => Volatile.Write(ref _position, value); }
        public long End { get => Volatile.Read(ref _end); internal set => Volatile.Write(ref _end, value); }
        public bool CanStopEarly { get; }
        internal bool Splittable { get; }
        internal bool Released { get; set; }

        // Bytes past Position that the holder has claimed for an in-progress write; a steal
        // never splits below this.
        internal long Reserved { get; set; }

        public override string ToString() => $"file {FileIndex} [{Start},{End}) @ {Position}";
    }

    private readonly object _lock = new();
    private readonly long[] _sizes;          // long.MaxValue while a file's size is unknown
    private readonly bool[] _sizeUnknown;
    private readonly bool[] _unknownDone;
    private readonly bool[] _sequential;     // resume refused mid-file: one lane, from the start
    private readonly ByteRangeSet[] _done;
    private readonly List<Assignment> _inFlight = new();
    private readonly long _minSplitBytes;
    private readonly long _maxTakeBytes;
    private long _doneBytes;

    public RangeScheduler(IReadOnlyList<long> fileSizes, IReadOnlyList<ByteRangeSet> done, long minSplitBytes, long maxTakeBytes)
    {
        if (fileSizes.Count != done.Count) throw new ArgumentException("fileSizes and done differ in length");
        int n = fileSizes.Count;
        _sizes = new long[n];
        _sizeUnknown = new bool[n];
        _unknownDone = new bool[n];
        _sequential = new bool[n];
        _done = new ByteRangeSet[n];
        for (int i = 0; i < n; i++)
        {
            _sizeUnknown[i] = fileSizes[i] < 0;
            _sizes[i] = _sizeUnknown[i] ? long.MaxValue : fileSizes[i];
            _done[i] = _sizeUnknown[i] ? new ByteRangeSet() : done[i].Clone();
            _doneBytes += _done[i].TotalLength;
        }
        _minSplitBytes = Math.Max(0, minSplitBytes);
        _maxTakeBytes = Math.Max(0, maxTakeBytes);
    }

    public int FileCount => _sizes.Length;

    public long DoneBytes { get { lock (_lock) return _doneBytes; } }

    public bool AllComplete
    {
        get
        {
            lock (_lock)
            {
                for (int i = 0; i < _sizes.Length; i++)
                    if (!IsFileCompleteLocked(i)) return false;
                return true;
            }
        }
    }

    public int InFlightCount { get { lock (_lock) return _inFlight.Count; } }

    public bool IsFileComplete(int fileIndex)
    {
        lock (_lock) return IsFileCompleteLocked(fileIndex);
    }

    private bool IsFileCompleteLocked(int i) =>
        _sizeUnknown[i] ? _unknownDone[i] : _done[i].Covers(new ByteRange(0, _sizes[i]));

    public long CurrentEnd(Assignment a) => a.End;

    /// <summary>Lowest-index incomplete file that a lane is working on, or -1.</summary>
    public int LowestActiveFile()
    {
        lock (_lock)
        {
            int best = -1;
            foreach (var a in _inFlight)
                if (!IsFileCompleteLocked(a.FileIndex) && (best < 0 || a.FileIndex < best)) best = a.FileIndex;
            return best;
        }
    }

    /// <summary>
    /// Next range for a lane, or null when nothing is eligible right now.
    /// <paramref name="allowRedownload"/> (only meaningful for a lane that cannot stop early):
    /// when no gap runs to EOF, accept a range from a mid-file gap to EOF that re-reads bytes
    /// already done. The engine sets it only when no lane that can stop early is alive —
    /// otherwise such a hole would strand forever.
    /// </summary>
    public Assignment? Take(bool canStopEarly, bool allowRedownload = false)
    {
        lock (_lock)
        {
            return TakeGap(canStopEarly)
                ?? Steal(canStopEarly)
                ?? (allowRedownload && !canStopEarly ? TakeRedownload() : null);
        }
    }

    private Assignment? TakeGap(bool canStopEarly)
    {
        for (int i = 0; i < _sizes.Length; i++)
        {
            if (IsFileCompleteLocked(i)) continue;

            if (_sequential[i] && !_sizeUnknown[i])
            {
                if (_inFlight.Any(a => a.FileIndex == i)) continue;
                long seqFrom = _done[i].Ranges.Count > 0 && _done[i].Ranges[0].Start == 0 ? _done[i].Ranges[0].End : 0;
                return Add(new Assignment(i, seqFrom, _sizes[i], canStopEarly, splittable: false));
            }

            if (_sizeUnknown[i])
            {
                // Sequential only: one lane at a time, from the end of what's done to EOF.
                if (_inFlight.Any(a => a.FileIndex == i)) continue;
                long from = _done[i].Ranges.Count > 0 && _done[i].Ranges[0].Start == 0 ? _done[i].Ranges[0].End : 0;
                return Add(new Assignment(i, from, long.MaxValue, canStopEarly, splittable: false));
            }

            var occupied = _done[i].Clone();
            foreach (var a in _inFlight)
                if (a.FileIndex == i) occupied.Add(new ByteRange(a.Position, a.End));

            foreach (var gap in occupied.Gaps(_sizes[i]))
            {
                if (canStopEarly)
                {
                    long end = _maxTakeBytes > 0 ? Math.Min(gap.End, gap.Start + _maxTakeBytes) : gap.End;
                    return Add(new Assignment(i, gap.Start, end, true, splittable: true));
                }
                if (gap.End == _sizes[i])
                    return Add(new Assignment(i, gap.Start, gap.End, false, splittable: false));
            }
        }
        return null;
    }

    private Assignment? Steal(bool canStopEarly)
    {
        Assignment? victim = null;
        long victimRemaining = 0, victimMid = 0;
        foreach (var a in _inFlight)
        {
            if (!a.Splittable) continue;
            if (!canStopEarly && a.End != _sizes[a.FileIndex]) continue;
            long from = Math.Max(a.Position, a.Reserved);
            long remaining = a.End - from;
            if (remaining < 2 * _minSplitBytes || remaining <= victimRemaining) continue;
            long mid = (from + remaining / 2) / SplitAlignment * SplitAlignment;
            if (mid <= from || mid >= a.End) continue;
            victim = a;
            victimRemaining = remaining;
            victimMid = mid;
        }
        if (victim == null) return null;

        long oldEnd = victim.End;
        victim.End = victimMid;
        return Add(new Assignment(victim.FileIndex, victimMid, oldEnd, canStopEarly, splittable: canStopEarly));
    }

    private Assignment? TakeRedownload()
    {
        for (int i = 0; i < _sizes.Length; i++)
        {
            if (_sizeUnknown[i] || IsFileCompleteLocked(i)) continue;
            var gaps = _done[i].Gaps(_sizes[i]);
            if (gaps.Count == 0) continue;
            var range = new ByteRange(gaps[0].Start, _sizes[i]);
            bool clash = _inFlight.Any(a => a.FileIndex == i && a.End > range.Start && a.Position < range.End);
            if (!clash) return Add(new Assignment(i, range.Start, range.End, false, splittable: false));
        }
        return null;
    }

    private Assignment Add(Assignment a)
    {
        _inFlight.Add(a);
        return a;
    }

    /// <summary>
    /// Claim up to <paramref name="bytes"/> past Position for a write about to happen; returns
    /// how many bytes the lane may write (0 when its End shrank to Position). Call before
    /// writing so a concurrent steal can't hand those bytes to another lane.
    /// </summary>
    public long Reserve(Assignment a, long bytes)
    {
        lock (_lock)
        {
            long allowed = Math.Max(0, Math.Min(bytes, a.End - a.Position));
            a.Reserved = a.Position + allowed;
            return allowed;
        }
    }

    /// <summary>Marks [Position, Position+bytes) done (clamped to End) and moves Position. Returns bytes newly done.</summary>
    public long Advance(Assignment a, long bytes)
    {
        lock (_lock)
        {
            long n = Math.Max(0, Math.Min(bytes, a.End - a.Position));
            if (n == 0) return 0;
            long added = _done[a.FileIndex].Add(new ByteRange(a.Position, a.Position + n));
            _doneBytes += added;
            a.Position += n;
            a.Reserved = a.Position;
            return added;
        }
    }

    /// <summary>The lane stopped; whatever is left of [Position, End) becomes available again. Idempotent.</summary>
    public void Release(Assignment a)
    {
        lock (_lock)
        {
            if (a.Released) return;
            a.Released = true;
            _inFlight.Remove(a);
        }
    }

    /// <summary>An unknown-size file hit EOF at the assignment's Position: its size is now known and it's complete.</summary>
    public void CompleteUnknown(Assignment a)
    {
        lock (_lock)
        {
            int i = a.FileIndex;
            if (!_sizeUnknown[i]) return;
            _sizes[i] = a.Position;
            _unknownDone[i] = true;
        }
    }

    public bool IsSizeUnknown(int fileIndex) => _sizeUnknown[fileIndex];

    /// <summary>
    /// Forget a file's done bytes so it is fetched again. In-flight assignments keep running
    /// and re-add what they write. <paramref name="sequential"/>: from now on the file is only
    /// handed out whole-from-the-start to one lane (the server refused to resume mid-file).
    /// </summary>
    public void Invalidate(int fileIndex, bool sequential = false)
    {
        lock (_lock)
        {
            _doneBytes -= _done[fileIndex].TotalLength;
            _done[fileIndex] = new ByteRangeSet();
            _unknownDone[fileIndex] = false;
            if (sequential) _sequential[fileIndex] = true;
        }
    }
}
