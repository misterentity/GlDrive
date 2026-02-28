using Serilog;

namespace GlDrive.Services;

public class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Global\GlDriveInstance";
    private Mutex? _mutex;

    public bool TryAcquire()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            Log.Information("Another instance of GlDrive is already running");
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        return true;
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
