using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Serilog;

namespace GlDrive.Downloads.Segmented;

/// <summary>
/// Keeps Windows from sleeping while any transfer runs. Ref-counted: the first
/// <see cref="Acquire"/> sets ES_SYSTEM_REQUIRED, the last release clears it.
///
/// SetThreadExecutionState is per-thread and lapses when the calling thread exits, and
/// transfers run on thread-pool threads that come and go. So every call is made from one
/// dedicated background thread that lives for the process, which then owns the state.
/// </summary>
public static class KeepAwake
{
    private static readonly KeepAwakeCounter Counter = new(StateThread.Post);

    public static IDisposable Acquire() => Counter.Acquire();

    private static class StateThread
    {
        private const uint EsContinuous = 0x80000000;
        private const uint EsSystemRequired = 0x00000001;

        private static readonly BlockingCollection<bool> Queue = new();
        private static readonly Lazy<Thread> Worker = new(() =>
        {
            var t = new Thread(Run) { IsBackground = true, Name = "GlDrive KeepAwake" };
            t.Start();
            return t;
        });

        public static void Post(bool awake)
        {
            _ = Worker.Value;
            Queue.Add(awake);
        }

        private static void Run()
        {
            foreach (var awake in Queue.GetConsumingEnumerable())
            {
                var flags = awake ? EsContinuous | EsSystemRequired : EsContinuous;
                if (SetThreadExecutionState(flags) == 0)
                    Log.Warning("KeepAwake: SetThreadExecutionState({Flags:X}) failed", flags);
                else
                    Log.Debug("KeepAwake: system sleep {State}", awake ? "blocked" : "allowed");
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);
    }
}

/// <summary>The ref counting behind <see cref="KeepAwake"/>, with the OS call injected.</summary>
internal sealed class KeepAwakeCounter(Action<bool> apply)
{
    private readonly object _lock = new();
    private int _count;

    public int Count { get { lock (_lock) return _count; } }

    public IDisposable Acquire()
    {
        lock (_lock)
        {
            if (_count++ == 0) apply(true);
        }
        return new Handle(this);
    }

    private void Release()
    {
        lock (_lock)
        {
            if (--_count == 0) apply(false);
        }
    }

    private sealed class Handle(KeepAwakeCounter owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release();
        }
    }
}
