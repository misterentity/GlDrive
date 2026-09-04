using System.Security.Principal;
using Serilog;

namespace GlDrive.Services;

public class SingleInstanceGuard : IDisposable
{
    private static string DefaultMutexName =>
        $@"Local\GlDrive_{WindowsIdentity.GetCurrent().User?.Value ?? "unknown"}";

    private readonly string _name;
    private readonly TimeSpan _retryDelay;
    private Mutex? _mutex;

    /// <summary>
    /// Production uses the per-user default name. Tests pass a unique name so they neither
    /// collide with a live GlDrive on the same box nor with each other, and a zero retry delay
    /// so the three-attempt loop does not cost four seconds per assertion.
    /// </summary>
    public SingleInstanceGuard(string? mutexName = null, TimeSpan? retryDelay = null)
    {
        _name = mutexName ?? DefaultMutexName;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
    }

    public bool TryAcquire()
    {
        // Retry a few times — after a crash, the OS may take a moment to release the mutex
        for (var attempt = 0; attempt < 3; attempt++)
        {
            _mutex = new Mutex(true, _name, out var createdNew);
            if (createdNew) return true;

            _mutex.Dispose();
            _mutex = null;

            if (attempt < 2 && _retryDelay > TimeSpan.Zero)
                Thread.Sleep(_retryDelay);
        }

        Log.Information("Another instance of GlDrive is already running");
        return false;
    }

    public void Dispose()
    {
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
            _mutex = null;
        }
        GC.SuppressFinalize(this);
    }
}
