using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using Serilog;

namespace GlDrive.Downloads.Segmented;

/// <summary>
/// Protocol-agnostic multi-lane download engine. Lanes pull ranges from a shared
/// <see cref="RangeScheduler"/> and write them into <see cref="StagedFile"/>s; a failed lane's
/// remainder goes back to the scheduler for the others. Owns (and disposes) every lane it runs.
/// </summary>
public sealed class SegmentedTransfer
{
    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(250);

    private readonly IReadOnlyList<TransferFile> _files;
    private readonly SegmentedTransferOptions _options;

    public SegmentedTransfer(IReadOnlyList<TransferFile> files, SegmentedTransferOptions options)
    {
        _files = files;
        _options = options;
    }

    /// <summary>Test seam: called for every staged file right after it is opened.</summary>
    internal Action<StagedFile>? StagedFileOpened { get; set; }

    // Wraps a write/commit failure so lane-failure handling can't mistake it for a lane fault.
    private sealed class DiskFailure(Exception inner) : Exception(inner.Message, inner);

    private sealed class LaneRun(ISegmentLane lane)
    {
        public ISegmentLane Lane { get; } = lane;
        public long Bytes;
        public long BytesAtLastReport;
        public double Speed;
        public volatile LaneState State = LaneState.Connecting;
    }

    private enum RangeOutcome { Finished, Yielded }

    private sealed class Run
    {
        public required StagedFile[] Staged;
        public required RangeScheduler Scheduler;
        public required CancellationTokenSource Cts;
        public readonly int[] Committed;
        // Readers open on each file. A file is only committed once none are, so a lane whose
        // completion check (226, EOF) is still pending can still invalidate it.
        public readonly int[] OpenReaders;
        public readonly List<LaneRun> Lanes = new();
        public readonly object Gate = new();
        public ExceptionDispatchInfo? LastLaneError;
        public ExceptionDispatchInfo? Fatal;
        private TaskCompletionSource _workSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Run(int fileCount)
        {
            Committed = new int[fileCount];
            OpenReaders = new int[fileCount];
        }

        public void PulseWork()
        {
            TaskCompletionSource old;
            lock (Gate)
            {
                old = _workSignal;
                _workSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            old.TrySetResult();
        }

        public Task WorkSignal { get { lock (Gate) return _workSignal.Task; } }

        public bool AnyLiveStopEarlyLane()
        {
            lock (Gate)
                return Lanes.Any(l => l.Lane.CanStopEarly && l.State is not (LaneState.Failed or LaneState.Yielded or LaneState.Done));
        }
    }

    public async Task RunAsync(IReadOnlyList<ISegmentLane> lanes, ILaneSource? extraLanes,
                               IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var staged = new StagedFile[_files.Count];
        var laneTasks = new List<Task>();
        Run? run = null;
        var pendingLanes = new List<ISegmentLane>(lanes);
        try
        {
            for (int i = 0; i < _files.Count; i++)
            {
                staged[i] = StagedFile.Open(_files[i]);
                StagedFileOpened?.Invoke(staged[i]);
            }

            var scheduler = new RangeScheduler(
                _files.Select(f => f.Size).ToList(),
                staged.Select(s => s.Done).ToList(),
                _options.MinSplitBytes, _options.MaxTakeBytes);

            run = new Run(_files.Count)
            {
                Staged = staged,
                Scheduler = scheduler,
                Cts = CancellationTokenSource.CreateLinkedTokenSource(ct),
            };
            for (int i = 0; i < staged.Length; i++)
                if (staged[i].IsComplete) run.Committed[i] = 1;

            // Zero-length files are complete from the start and only need committing.
            await CommitReadyFilesAsync(run);

            foreach (var lane in lanes)
            {
                pendingLanes.Remove(lane);
                laneTasks.Add(StartLane(run, lane));
            }

            await SuperviseAsync(run, laneTasks, extraLanes, progress, ct);
        }
        finally
        {
            if (run != null)
            {
                run.Cts.Cancel();
                await Task.WhenAll(laneTasks);
                run.Cts.Dispose();
            }
            foreach (var lane in pendingLanes) await DisposeLaneAsync(lane);
            foreach (var s in staged)
                if (s != null) await s.DisposeAsync();
        }
    }

    private async Task SuperviseAsync(Run run, List<Task> laneTasks, ILaneSource? extraLanes,
                                      IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var nextExtraPoll = TimeSpan.Zero;
        var lastReport = TimeSpan.Zero;
        long lastDone = run.Scheduler.DoneBytes;
        double overallSpeed = 0;
        var tick = _options.ProgressInterval;
        if (extraLanes != null && _options.ExtraLanePollInterval < tick) tick = _options.ExtraLanePollInterval;
        if (tick <= TimeSpan.Zero) tick = TimeSpan.FromMilliseconds(50);

        void Report()
        {
            var now = clock.Elapsed;
            double secs = Math.Max((now - lastReport).TotalSeconds, 1e-3);
            long done = run.Scheduler.DoneBytes;
            overallSpeed = Smooth(overallSpeed, (done - lastDone) / secs, lastReport == TimeSpan.Zero);
            lastDone = done;
            List<LaneStats> stats;
            lock (run.Gate)
            {
                stats = run.Lanes.Select(l =>
                {
                    long b = Interlocked.Read(ref l.Bytes);
                    l.Speed = Smooth(l.Speed, (b - l.BytesAtLastReport) / secs, lastReport == TimeSpan.Zero);
                    l.BytesAtLastReport = b;
                    return new LaneStats(l.Lane.Label, l.Lane.NetworkId, b, l.Speed, l.State);
                }).ToList();
            }
            lastReport = now;
            int current = run.Scheduler.LowestActiveFile();
            progress!.Report(new TransferProgress(
                done,
                _files.Sum(f => Math.Max(0, f.Size)),
                overallSpeed,
                current >= 0 ? Path.GetFileName(_files[current].LocalPath) : null,
                stats));
        }

        while (true)
        {
            laneTasks.RemoveAll(t => t.IsCompleted);
            if (run.Fatal != null) break;
            ct.ThrowIfCancellationRequested();

            bool workRemains = !run.Scheduler.AllComplete;
            if (!workRemains && laneTasks.Count == 0) break;

            if (workRemains && extraLanes != null && laneTasks.Count < _options.MaxLanes
                && (clock.Elapsed >= nextExtraPoll || laneTasks.Count == 0))
            {
                nextExtraPoll = clock.Elapsed + _options.ExtraLanePollInterval;
                ISegmentLane? extra = null;
                try { extra = await extraLanes.TryAcquireAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Log.Warning(ex, "Extra download lane source failed");
                }
                if (extra != null)
                {
                    Log.Information("Download lane joined: {Label}", extra.Label);
                    laneTasks.Add(StartLane(run, extra));
                    continue;
                }
            }

            if (workRemains && laneTasks.Count == 0)
            {
                // Check the scheduler once more: the last lane may have finished the work as it exited.
                await CommitReadyFilesAsync(run);
                if (run.Fatal != null) break;
                if (run.Scheduler.AllComplete) break;
                if (progress != null) Report();
                run.LastLaneError?.Throw();
                throw new InvalidOperationException("No download lanes available");
            }

            if (progress != null && clock.Elapsed - lastReport >= _options.ProgressInterval) Report();

            var delay = Task.Delay(tick, ct);
            await Task.WhenAny(laneTasks.Append(delay));
            if (delay.IsCanceled) ct.ThrowIfCancellationRequested();
        }

        if (run.Fatal == null) await CommitReadyFilesAsync(run);
        if (progress != null) Report();
        run.Fatal?.Throw();
    }

    private static double Smooth(double previous, double sample, bool first) =>
        first ? sample : previous * 0.7 + sample * 0.3;

    private Task StartLane(Run run, ISegmentLane lane)
    {
        var lr = new LaneRun(lane);
        lock (run.Gate) run.Lanes.Add(lr);
        return Task.Run(() => LaneLoopAsync(run, lr));
    }

    private async Task LaneLoopAsync(Run run, LaneRun lr)
    {
        var lane = lr.Lane;
        var token = run.Cts.Token;
        var buffer = new byte[Math.Max(4096, _options.BufferSize)];
        int failures = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (run.Scheduler.AllComplete) { lr.State = LaneState.Done; return; }
                if (lane.ShouldYield) { lr.State = LaneState.Yielded; return; }

                var signal = run.WorkSignal;
                var a = run.Scheduler.Take(lane.CanStopEarly);
                if (a == null && !lane.CanStopEarly && run.Scheduler.InFlightCount == 0 && !run.AnyLiveStopEarlyLane())
                    a = run.Scheduler.Take(false, allowRedownload: true);
                if (a == null)
                {
                    lr.State = LaneState.Idle;
                    await Task.WhenAny(signal, Task.Delay(IdleWait, token));
                    continue;
                }

                try
                {
                    var outcome = await RunRangeAsync(run, lr, a, buffer, token);
                    failures = 0;
                    if (outcome == RangeOutcome.Yielded) { lr.State = LaneState.Yielded; return; }
                }
                catch (DiskFailure df)
                {
                    lock (run.Gate) run.Fatal ??= ExceptionDispatchInfo.Capture(df.InnerException!);
                    run.Cts.Cancel();
                    lr.State = LaneState.Failed;
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    lock (run.Gate) run.LastLaneError = ExceptionDispatchInfo.Capture(ex);
                    if (ex is RangeNotSupportedException)
                    {
                        Log.Warning("Download lane {Label}: {Message} — refetching {File} from the start on one lane",
                            lane.Label, ex.Message, _files[a.FileIndex].LocalPath);
                        InvalidateFile(run, a.FileIndex, sequential: true);
                    }
                    if (ex is LaneBackoffException backoff)
                    {
                        Log.Information("Download lane {Label} backing off {Delay}: {Message}", lane.Label, backoff.Delay, ex.Message);
                        lr.State = LaneState.Idle;
                        await Task.Delay(backoff.Delay, token);
                        continue;
                    }

                    failures++;
                    if (failures >= _options.MaxLaneFailures)
                    {
                        Log.Warning("Download lane {Label} retired after {Failures} consecutive failures: {Message}",
                            lane.Label, failures, ex.Message);
                        lr.State = LaneState.Failed;
                        return;
                    }
                    Log.Information("Download lane {Label} failed ({Failures}/{Max}), retrying: {Message}",
                        lane.Label, failures, _options.MaxLaneFailures, ex.Message);
                    lr.State = LaneState.Idle;
                    await Task.Delay(_options.LaneRetryDelay * failures, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            if (lr.State is not (LaneState.Failed or LaneState.Yielded or LaneState.Done))
                lr.State = run.Scheduler.AllComplete ? LaneState.Done : LaneState.Failed;
            run.PulseWork();
            await DisposeLaneAsync(lane);
        }
    }

    private async Task<RangeOutcome> RunRangeAsync(Run run, LaneRun lr, RangeScheduler.Assignment a, byte[] buffer, CancellationToken token)
    {
        var lane = lr.Lane;
        var file = _files[a.FileIndex];
        var staged = run.Staged[a.FileIndex];
        bool unknownSize = file.Size < 0;
        ISegmentReader? reader = null;
        bool finished = false;

        // Reads and pacing waits on a stoppable lane run under readCts, which the watcher cancels
        // when the lane must yield (the race wants its login back) or the data stops flowing —
        // a blocked read would otherwise hold a borrowed race login indefinitely. Only managed
        // streams (CPSV SslStream, HTTP) are interrupted this way; a GnuTLS data read must never
        // be cancelled, so non-stoppable lanes read with the plain token as before.
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var readToken = lane.CanStopEarly ? readCts.Token : token;
        int interrupt = 0; // 1 = yield, 2 = stall
        int pacing = 0;    // a pacer wait is deliberate silence, not a stall
        long lastActivity = Environment.TickCount64;
        Task? watcher = null;
        try
        {
            lr.State = LaneState.Connecting;
            try
            {
                reader = await lane.OpenAsync(file, a.Position, a.End, token);
            }
            catch (Exception ex)
            {
                // Any failed open may have left a control reply unread — cancellation included.
                lane.Fault(token.IsCancellationRequested ? new OperationCanceledException(token) : ex);
                throw;
            }
            Interlocked.Increment(ref run.OpenReaders[a.FileIndex]);
            lr.State = LaneState.Active;

            if (lane.CanStopEarly)
                watcher = WatchAsync();

            while (true)
            {
                if (Volatile.Read(ref interrupt) == 1 || lane.ShouldYield)
                    return await YieldAsync();

                long remaining = a.End - a.Position;
                if (remaining <= 0)
                {
                    bool eof = !unknownSize && a.Position >= file.Size;
                    run.Scheduler.Release(a);
                    await FinishAsync(eof);
                    return RangeOutcome.Finished;
                }

                int want = (int)Math.Min(buffer.Length, remaining);
                int n;
                try
                {
                    n = await reader.ReadAsync(buffer.AsMemory(0, want), readToken);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && Volatile.Read(ref interrupt) != 0)
                {
                    if (Volatile.Read(ref interrupt) == 1) return await YieldAsync();
                    throw new IOException($"No data for {_options.StallTimeout.TotalSeconds:0}s on {file.Key}");
                }
                if (n == 0)
                {
                    if (!unknownSize)
                        throw new IOException($"Premature end of data at {a.Position} of {file.Key} (expected {a.End})");
                    run.Scheduler.CompleteUnknown(a);
                    run.Scheduler.Release(a);
                    await FinishAsync(true);
                    return RangeOutcome.Finished;
                }
                Volatile.Write(ref lastActivity, Environment.TickCount64);

                long allowed = run.Scheduler.Reserve(a, n);
                if (allowed > 0)
                {
                    long offset = a.Position;
                    try
                    {
                        await staged.WriteAsync(offset, buffer.AsMemory(0, (int)allowed), token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        throw new DiskFailure(ex);
                    }
                    run.Scheduler.Advance(a, allowed);
                    try { staged.MarkDone(new ByteRange(offset, offset + allowed)); }
                    catch (Exception ex) { throw new DiskFailure(ex); }
                }

                Interlocked.Add(ref lr.Bytes, n);
                _options.OnBytes?.Invoke(lane.NetworkId, n);
                if (_options.Pacer != null)
                {
                    Volatile.Write(ref pacing, 1);
                    try
                    {
                        await _options.Pacer.WaitAsync(lane.NetworkId, n, readToken);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested && Volatile.Read(ref interrupt) != 0)
                    {
                        if (Volatile.Read(ref interrupt) == 1) return await YieldAsync();
                    }
                    finally { Volatile.Write(ref pacing, 0); }
                }
                Volatile.Write(ref lastActivity, Environment.TickCount64);
            }
        }
        catch (Exception ex) when (reader != null && !finished)
        {
            lane.Fault(ex is DiskFailure df ? df.InnerException!
                : token.IsCancellationRequested ? new OperationCanceledException(token) : ex);
            throw;
        }
        finally
        {
            readCts.Cancel();
            if (watcher != null) await watcher;
            run.Scheduler.Release(a);
            run.PulseWork();
            if (reader != null)
            {
                try { await reader.DisposeAsync(); }
                catch (Exception ex) { Log.Debug(ex, "Download reader dispose failed on lane {Label}", lane.Label); }
                Interlocked.Decrement(ref run.OpenReaders[a.FileIndex]);
            }
            await CommitReadyFilesAsync(run);
        }

        async Task<RangeOutcome> YieldAsync()
        {
            run.Scheduler.Release(a);
            run.PulseWork();
            await reader!.FinishAsync(false, token);
            finished = true;
            Log.Information("Download lane {Label} yielded at {Position} of {File}", lane.Label, a.Position, file.LocalPath);
            return RangeOutcome.Yielded;
        }

        async Task FinishAsync(bool eof)
        {
            try
            {
                await reader!.FinishAsync(eof, token);
            }
            catch (Exception) when (eof && !token.IsCancellationRequested)
            {
                // Every byte arrived but the server would not confirm the file (extra bytes, no
                // 226): don't publish it — fetch it again.
                Log.Warning("Download of {File} was not confirmed by the server — refetching it", file.LocalPath);
                InvalidateFile(run, a.FileIndex, sequential: false);
                throw;
            }
            finished = true;
        }

        async Task WatchAsync()
        {
            try
            {
                while (!readCts.IsCancellationRequested)
                {
                    await Task.Delay(WatchInterval, readCts.Token);
                    if (lane.ShouldYield)
                    {
                        Volatile.Write(ref interrupt, 1);
                        readCts.Cancel();
                        return;
                    }
                    if (Volatile.Read(ref pacing) == 1) Volatile.Write(ref lastActivity, Environment.TickCount64);
                    else if (_options.StallTimeout > TimeSpan.Zero &&
                        Environment.TickCount64 - Volatile.Read(ref lastActivity) > _options.StallTimeout.TotalMilliseconds)
                    {
                        Volatile.Write(ref interrupt, 2);
                        readCts.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
    }

    private static void InvalidateFile(Run run, int fileIndex, bool sequential)
    {
        run.Scheduler.Invalidate(fileIndex, sequential);
        run.Staged[fileIndex].ResetDone();
        run.PulseWork();
    }

    private async Task CommitReadyFilesAsync(Run run)
    {
        for (int i = 0; i < _files.Count; i++)
        {
            if (Volatile.Read(ref run.Committed[i]) != 0 || Volatile.Read(ref run.OpenReaders[i]) != 0
                || !run.Scheduler.IsFileComplete(i)) continue;
            if (Interlocked.Exchange(ref run.Committed[i], 1) != 0) continue;
            try
            {
                await run.Staged[i].CommitAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not commit download {Path}", _files[i].LocalPath);
                lock (run.Gate) run.Fatal ??= ExceptionDispatchInfo.Capture(ex);
                run.Cts.Cancel();
                return;
            }
            Log.Information("Download complete: {Path}", _files[i].LocalPath);
            try { _options.OnFileCompleted?.Invoke(_files[i]); }
            catch (Exception ex) { Log.Warning(ex, "OnFileCompleted handler failed for {Path}", _files[i].LocalPath); }
        }
    }

    private static async Task DisposeLaneAsync(ISegmentLane lane)
    {
        try { await lane.DisposeAsync(); }
        catch (Exception ex) { Log.Debug(ex, "Download lane {Label} dispose failed", lane.Label); }
    }
}
