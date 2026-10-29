namespace GlDrive.Services.Automation;

public sealed record AutomationApprovalRequest(string Id, string ClientId, string ClientName, string Tool,
    string Reason, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc);

/// <summary>Only automation jobs await local decisions. No engine thread or dispatcher is blocked.</summary>
public sealed class AutomationApprovalBroker : IDisposable
{
    private sealed record Pending(AutomationApprovalRequest Request, TaskCompletionSource<bool> Completion);
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private bool _disposed;
    private bool _sessionLocked;
    public event Action<AutomationApprovalRequest>? Requested;
    public event Action? Changed;

    public IReadOnlyList<AutomationApprovalRequest> Snapshot()
    { lock (_gate) return _pending.Values.Select(p => p.Request).OrderBy(p => p.CreatedUtc).ToArray(); }

    internal async Task<bool> RequestAsync(AutomationPrincipal principal, string tool, string reason, TimeSpan timeout, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var request = new AutomationApprovalRequest(Guid.NewGuid().ToString("N"), principal.Id, principal.Name, tool, reason, now, now + timeout);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposed || _sessionLocked) return false;
            // Passive capacity: deny and evict the oldest pending decision, never refuse a new request.
            if (_pending.Count >= 64)
            {
                var oldest = _pending.Values.MinBy(p => p.Request.CreatedUtc)!;
                _pending.Remove(oldest.Request.Id); oldest.Completion.TrySetResult(false);
            }
            _pending.Add(request.Id, new(request, completion));
        }
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task delay;
        using (ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow())
            delay = Task.Delay(timeout, timeoutCancellation.Token);
        try
        {
            try { Requested?.Invoke(request); Changed?.Invoke(); }
            catch { completion.TrySetResult(false); }
            if (await Task.WhenAny(completion.Task, delay).ConfigureAwait(false) != completion.Task) return false;
            return !ct.IsCancellationRequested && await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            timeoutCancellation.Cancel();
            lock (_gate) _pending.Remove(request.Id);
            try { Changed?.Invoke(); } catch { }
        }
    }

    internal bool Decide(string id, bool approved)
    {
        lock (_gate)
            return _pending.TryGetValue(id, out var pending) && pending.Completion.TrySetResult(approved && DateTimeOffset.UtcNow < pending.Request.ExpiresUtc);
    }

    public void DenyAll()
    {
        lock (_gate) foreach (var pending in _pending.Values) pending.Completion.TrySetResult(false);
    }
    public void SetSessionLocked(bool locked)
    { lock (_gate) { _sessionLocked = locked; if (locked) DenyAll(); } }
    public void Dispose() { lock (_gate) { _disposed = true; DenyAll(); } }
}
