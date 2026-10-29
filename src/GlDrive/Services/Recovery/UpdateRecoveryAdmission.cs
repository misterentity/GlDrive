namespace GlDrive.Services.Recovery;

/// <summary>Local recovery and automatic installation must never own the profile together.</summary>
internal sealed class UpdateRecoveryAdmission
{
    private readonly object _gate = new();
    private bool _installing;
    private bool _restartPending;
    private int _recovering;

    internal bool Installing { get { lock (_gate) return _installing; } }

    internal IDisposable? TryBeginInstall()
    {
        lock (_gate)
        {
            if (_installing || _restartPending || _recovering != 0) return null;
            _installing = true;
            return new Lease(() => { lock (_gate) _installing = false; });
        }
    }

    internal IDisposable? TryBeginRecovery()
    {
        lock (_gate)
        {
            if (_installing || _restartPending) return null;
            _recovering++;
            return new Lease(() => { lock (_gate) _recovering--; });
        }
    }

    internal bool TryStopForRestart()
    {
        lock (_gate)
        {
            _restartPending = true;
            return !_installing && _recovering == 0;
        }
    }

    internal void RestartFailed() { lock (_gate) _restartPending = false; }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
