using System.Collections.Concurrent;
using System.Text.Json;
using GlDrive.Services.Control;

namespace GlDrive.Services.Automation;

/// <summary>Domain catalog and bounded, cancellable operation lifetime independent of HTTP requests.</summary>
public sealed class AutomationRegistry : IDisposable
{
    private readonly Dictionary<string, (AutomationToolDefinition Tool, IAutomationModule Module)> _tools = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly object _jobLock = new();
    private readonly SemaphoreSlim _runningSlots = new(16, 16);
    private readonly IAutomationModule[] _modules;
    private bool _disposed;
    internal TimeSpan InlineWait { get; set; } = TimeSpan.FromSeconds(8);
    private const int MaxJobs = 64;
    private const int MaxResultBytes = 4 * 1024 * 1024;
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
    public AutomationAccessController? Access { get; set; }
    public Action<AutomationCallAudit>? Audit { get; set; }

    private sealed class CallState(AutomationPrincipal principal, string name, JsonElement arguments)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public AutomationPrincipal Principal { get; } = principal;
        public string Name { get; } = name;
        public JsonElement Arguments { get; } = arguments;
        public System.Diagnostics.Stopwatch Clock { get; } = System.Diagnostics.Stopwatch.StartNew();
        public bool StartedJob;
    }

    private sealed class Job(string name, CancellationTokenSource cancellation, AutomationPrincipal principal,
        AutomationToolDefinition definition, JsonElement arguments)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Name { get; } = name;
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<JsonElement> Work { get; set; } = null!;
        public string OwnerId { get; } = principal.Id;
        public AutomationToolDefinition Definition { get; } = definition;
        public JsonElement Arguments { get; } = arguments;
        public volatile bool AwaitingApproval;
        public volatile bool Denied;
        public volatile bool Running;
        public string? DenialCode;
    }

    public AutomationRegistry(params IAutomationModule[] modules)
    {
        _modules = modules;
        foreach (var module in modules)
            foreach (var tool in module.Tools)
            {
                if (tool.Name.StartsWith("app.jobs", StringComparison.Ordinal) || !_tools.TryAdd(tool.Name, (tool, module)))
                    throw new InvalidOperationException($"Duplicate or reserved automation tool: {tool.Name}");
            }
    }

    public IReadOnlyList<AutomationToolDefinition> Tools => _tools.Values.Select(t => t.Tool).Concat(JobTools).OrderBy(t => t.Name).ToArray();
    private static readonly AutomationToolDefinition[] JobTools =
    [
        AutomationToolDefinition.Create("app.jobs", "List recent MCP operations. Long operations return a jobId; inspect app.jobs_get until complete. Jobs expire after 30 minutes or when the app exits.", new { type = "object", properties = new { }, additionalProperties = false }, true),
        AutomationToolDefinition.Create("app.jobs_get", "Read a long-running operation's status and final result. A failed operation has status failed and an error; do not interpret it as success.", new { type = "object", properties = new { jobId = new { type = "string" } }, required = new[] { "jobId" }, additionalProperties = false }, true),
        AutomationToolDefinition.Create("app.jobs_cancel", "Request cancellation of a running operation. Already committed effects are not rolled back.", new { type = "object", properties = new { jobId = new { type = "string" } }, required = new[] { "jobId" }, additionalProperties = false })
    ];

    public async Task<object?> CallAsync(string name, JsonElement arguments, CancellationToken requestCancellation,
        AutomationPrincipal? principal = null)
    {
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) arguments = Empty;
        var state = new CallState(principal ?? AutomationPrincipal.Legacy, name, arguments.Clone());
        Record(state, "accepted");
        try
        {
            var result = await CallCoreAsync(state, requestCancellation).ConfigureAwait(false);
            if (!state.StartedJob) Record(state, "completed");
            return result;
        }
        catch (Exception ex)
        {
            if (!state.StartedJob) Record(state, ex is AutomationDeniedException ? "denied" : ex is OperationCanceledException ? "cancelled" : "failed");
            throw;
        }
    }

    private async Task<object?> CallCoreAsync(CallState state, CancellationToken requestCancellation)
    {
        var name = state.Name; var arguments = state.Arguments; var principal = state.Principal;
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("arguments must be an object");
        var definition = JobTools.FirstOrDefault(tool => tool.Name == name) ??
            (_tools.TryGetValue(name, out var known) ? known.Tool : throw new KeyNotFoundException("Unknown tool"));
        AutomationSchema.Validate(definition.InputSchema, arguments);
        var risk = Access?.Authorize(principal, definition, arguments);
        if (name == "app.jobs") return new { jobs = _jobs.Values.Where(j => j.OwnerId == principal.Id)
            .OrderByDescending(j => j.CreatedAt).Select(j => Snapshot(j, false)).ToArray() };
        if (name is "app.jobs_get" or "app.jobs_cancel")
        {
            var id = arguments.GetProperty("jobId").GetString() ?? "";
            if (!_jobs.TryGetValue(id, out var existing) || existing.OwnerId != principal.Id) throw new KeyNotFoundException("Unknown or expired jobId");
            // A reduced scope cannot retrieve an earlier secret-bearing result under its old permissions.
            if (name == "app.jobs_get") Access?.Authorize(principal, existing.Definition, existing.Arguments);
            if (name == "app.jobs_cancel" && !existing.Work.IsCompleted)
                try { existing.Cancellation.Cancel(); } catch (ObjectDisposedException) { /* Completed and expired concurrently. */ }
            return Snapshot(existing, name == "app.jobs_get");
        }
        var entry = _tools[name];
        var needsApproval = risk != null && Access?.RequiresApproval(principal, risk) == true;
        Job job;
        lock (_jobLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var stale in _jobs.Values.Where(j => j.Work.IsCompleted && DateTime.UtcNow - j.CreatedAt > TimeSpan.FromMinutes(30)).ToArray())
                Remove(stale);
            if (_jobs.Count >= MaxJobs)
            {
                var oldest = _jobs.Values.Where(j => j.Work.IsCompleted).MinBy(j => j.CreatedAt);
                if (oldest != null) Remove(oldest);
            }
            // Approval waits have their own budget; they never consume execution slots.
            // Evict the oldest pending decision rather than preventing status/read calls.
            if (needsApproval && _jobs.Values.Count(j => j.AwaitingApproval) >= 64)
            {
                var oldest = _jobs.Values.Where(j => j.AwaitingApproval).MinBy(j => j.CreatedAt)!;
                oldest.Denied = true; oldest.DenialCode = "approval_denied";
                oldest.AwaitingApproval = false;
                oldest.Cancellation.Cancel();
            }
            if (!needsApproval && !_runningSlots.Wait(0))
                throw new InvalidOperationException("Too many running automation operations; wait or cancel a job.");
            var copy = arguments.Clone();
            using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
            job = new Job(name, new CancellationTokenSource(TimeSpan.FromMinutes(30)), principal, definition, copy)
            { AwaitingApproval = needsApproval, Running = !needsApproval };
            state.StartedJob = true;
            job.Work = Task.Run(async () =>
            {
                try
                {
                    IAutomationApprovedCall? boundCall = null;
                    if (risk?.ApprovalReason is { } reason && Access is { } access && needsApproval)
                    {
                        if (entry.Module is IAutomationApprovalBinding binding)
                            boundCall = await binding.BindApprovalAsync(name, copy, job.Cancellation.Token).ConfigureAwait(false);
                        if ((name == "ui.invoke" && risk.Destructive || name is "installer.start" or "extractor.start" or "extractor.settings" or "config_patch") && boundCall == null)
                            throw new AutomationDeniedException("The mutable operation target could not be bound for local approval.", "approval_denied");
                        bool approved;
                        var targetName = (entry.Module as IAutomationTargetDescription)?.ApprovalTargetName(name, copy);
                        var description = access.DescribeApproval(name, copy, reason, targetName) + (boundCall == null ? "" : "\n" + boundCall.Description);
                        try { approved = await access.Approvals.RequestAsync(principal, name, description, access.ApprovalTimeout, job.Cancellation.Token).ConfigureAwait(false); }
                        finally { job.AwaitingApproval = false; }
                        job.Cancellation.Token.ThrowIfCancellationRequested();
                        if (!approved) throw new AutomationDeniedException("Local approval was denied, unavailable or timed out.", "approval_denied");
                        if (!_runningSlots.Wait(0))
                            throw new InvalidOperationException("Too many running automation operations; wait or cancel a job.");
                        job.Running = true;
                    }
                    Access?.Authorize(principal, definition, copy); // Revocation/expiry while awaiting approval takes effect.
                    // B11 recognizes this stable human actor. Client/call correlation belongs
                    // in the call ledger, not AuditRef (which identifies an AI revert).
                    using var attribution = GlDrive.Config.ConfigWriteScope.Begin("mcp:" + name);
                    var result = boundCall == null ? await entry.Module.ExecuteAsync(name, copy, job.Cancellation.Token)
                        : await boundCall.ExecuteAsync(job.Cancellation.Token);
                    var json = JsonSerializer.SerializeToUtf8Bytes(result, ControlRequest.Json);
                    if (json.Length > MaxResultBytes) throw new InvalidOperationException("Result exceeds 4 MiB; narrow the query or use pagination.");
                    using var document = JsonDocument.Parse(json);
                    var snapshot = document.RootElement.Clone();
                    Record(state, "completed", job.Id);
                    return snapshot;
                }
                catch (Exception ex)
                {
                    job.Denied |= ex is AutomationDeniedException;
                    if (ex is AutomationDeniedException denial) job.DenialCode = denial.Code;
                    Record(state, job.Denied ? "denied" : ex is OperationCanceledException ? "cancelled" : "failed", job.Id);
                    Serilog.Log.Debug(ex, "Automation operation {Operation} failed", name);
                    throw;
                }
                finally
                {
                    job.AwaitingApproval = false;
                    if (job.Running) { job.Running = false; _runningSlots.Release(); }
                }
            });
            _jobs[job.Id] = job;
            _ = job.Work.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        Task inlineDelay;
        using (ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow())
            inlineDelay = Task.Delay(InlineWait, requestCancellation);
        var winner = await Task.WhenAny(job.Work, inlineDelay);
        requestCancellation.ThrowIfCancellationRequested();
        if (winner == job.Work)
        {
            if (job.Denied) return Snapshot(job, false);
            return await job.Work;
        }
        return Snapshot(job, false);
    }

    private static object Snapshot(Job job, bool includeResult) => new
    {
        jobId = job.Id, name = job.Name, createdAt = job.CreatedAt,
        status = job.Denied ? "denied" : job.Work.IsCanceled ? "cancelled" : job.Work.IsFaulted ? "failed" : job.Work.IsCompletedSuccessfully ? "completed" : job.AwaitingApproval ? "awaiting-approval" : "running",
        cancellationRequested = job.Cancellation.IsCancellationRequested,
        result = includeResult && job.Work.IsCompletedSuccessfully ? (JsonElement?)job.Work.Result : null,
        code = job.Denied ? job.DenialCode ?? "approval_denied" : null,
        error = job.Denied ? "Operation denied by client scope or local approval; the connection remains authenticated."
            : job.Work.IsFaulted ? "Operation failed. Inspect local diagnostics; sensitive provider error text is not returned." : null
    };

    private void Record(CallState state, string outcome, string? jobId = null)
    {
        try { Audit?.Invoke(new(state.Id, state.Principal.Id, state.Principal.Name, state.Name,
            state.Arguments, outcome, state.Clock.ElapsedMilliseconds, jobId)); }
        catch { /* Bookkeeping never changes admission or a completed operation. */ }
    }

    private void Remove(Job job)
    {
        if (_jobs.TryRemove(job.Id, out _)) job.Cancellation.Dispose();
    }

    public void Dispose()
    {
        lock (_jobLock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var job in _jobs.Values) job.Cancellation.Cancel();
        }
        // Work may still be unwinding in service code. Dispose cancellation and semaphore
        // only after it finishes; never block the WPF dispatcher during shutdown.
        _ = Task.WhenAll(_jobs.Values.Select(j => j.Work)).ContinueWith(_ =>
        {
            foreach (var job in _jobs.Values) job.Cancellation.Dispose();
            _runningSlots.Dispose();
            foreach (var module in _modules.OfType<IDisposable>()) module.Dispose();
        }, TaskScheduler.Default);
    }
}
