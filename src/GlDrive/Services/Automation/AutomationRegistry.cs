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
    private readonly IAutomationModule[] _modules;
    private bool _disposed;
    internal TimeSpan InlineWait { get; set; } = TimeSpan.FromSeconds(8);
    private const int MaxJobs = 64;
    private const int MaxResultBytes = 4 * 1024 * 1024;
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    private sealed class Job(string name, CancellationTokenSource cancellation)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Name { get; } = name;
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<JsonElement> Work { get; set; } = null!;
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

    public async Task<object?> CallAsync(string name, JsonElement arguments, CancellationToken requestCancellation)
    {
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) arguments = Empty;
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("arguments must be an object");
        if (name == "app.jobs") return new { jobs = _jobs.Values.OrderByDescending(j => j.CreatedAt).Select(j => Snapshot(j, false)).ToArray() };
        if (name is "app.jobs_get" or "app.jobs_cancel")
        {
            var id = arguments.GetProperty("jobId").GetString() ?? "";
            if (!_jobs.TryGetValue(id, out var existing)) throw new KeyNotFoundException("Unknown or expired jobId");
            if (name == "app.jobs_cancel" && !existing.Work.IsCompleted)
                try { existing.Cancellation.Cancel(); } catch (ObjectDisposedException) { /* Completed and expired concurrently. */ }
            return Snapshot(existing, true);
        }
        if (!_tools.TryGetValue(name, out var entry)) throw new KeyNotFoundException($"Unknown tool: {name}");
        AutomationSchema.Validate(entry.Tool.InputSchema, arguments);
        Job job;
        lock (_jobLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_jobs.Values.Count(j => !j.Work.IsCompleted) >= 16)
                throw new InvalidOperationException("Too many running automation operations; wait or cancel a job.");
            foreach (var stale in _jobs.Values.Where(j => j.Work.IsCompleted && DateTime.UtcNow - j.CreatedAt > TimeSpan.FromMinutes(30)).ToArray())
                Remove(stale);
            if (_jobs.Count >= MaxJobs)
            {
                var oldest = _jobs.Values.Where(j => j.Work.IsCompleted).MinBy(j => j.CreatedAt);
                if (oldest == null) throw new InvalidOperationException("Too many running automation operations; wait or cancel a job.");
                Remove(oldest);
            }
            job = new Job(name, new CancellationTokenSource(TimeSpan.FromMinutes(30)));
            var copy = arguments.Clone();
            job.Work = Task.Run(async () =>
            {
                try
                {
                    var result = await entry.Module.ExecuteAsync(name, copy, job.Cancellation.Token);
                    var json = JsonSerializer.SerializeToUtf8Bytes(result, ControlRequest.Json);
                    if (json.Length > MaxResultBytes) throw new InvalidOperationException("Result exceeds 4 MiB; narrow the query or use pagination.");
                    using var document = JsonDocument.Parse(json);
                    return document.RootElement.Clone();
                }
                catch (Exception ex)
                {
                    Serilog.Log.Debug(ex, "Automation operation {Operation} failed", name);
                    throw;
                }
            });
            _jobs[job.Id] = job;
            _ = job.Work.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        var winner = await Task.WhenAny(job.Work, Task.Delay(InlineWait, requestCancellation));
        requestCancellation.ThrowIfCancellationRequested();
        if (winner == job.Work) return await job.Work;
        return Snapshot(job, false);
    }

    private static object Snapshot(Job job, bool includeResult) => new
    {
        jobId = job.Id, name = job.Name, createdAt = job.CreatedAt,
        status = job.Work.IsCanceled ? "cancelled" : job.Work.IsFaulted ? "failed" : job.Work.IsCompletedSuccessfully ? "completed" : "running",
        cancellationRequested = job.Cancellation.IsCancellationRequested,
        result = includeResult && job.Work.IsCompletedSuccessfully ? (JsonElement?)job.Work.Result : null,
        error = job.Work.IsFaulted ? "Operation failed. Inspect local diagnostics; sensitive provider error text is not returned." : null
    };

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
            foreach (var module in _modules.OfType<IDisposable>()) module.Dispose();
        }, TaskScheduler.Default);
    }
}
