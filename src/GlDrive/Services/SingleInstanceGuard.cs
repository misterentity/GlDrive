using Serilog;

namespace GlDrive.Services;

public class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Global\GlDriveInstance";
    private Mutex? _mutex;

    public bool TryAcquire()
    {
        // Retry a few times — after a crash, the OS may take a moment to release the mutex
        for (var attempt = 0; attempt < 3; attempt++)
        {
            _mutex = new Mutex(true, MutexName, out var createdNew);
            if (createdNew) return true;

            _mutex.Dispose();
            _mutex = null;

            if (attempt < 2)
                Thread.Sleep(2000);
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
