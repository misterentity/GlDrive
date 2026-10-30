using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Services.Alerts;
using Serilog;

namespace GlDrive.Services.Hooks;

/// <summary>Optional post-event work. Admission only enqueues; failures never change engine state.</summary>
public sealed class HookRunner : IDisposable, IAsyncDisposable
{
    private readonly HookCommandConfig[] _commands;
    private readonly Channel<HookEvent> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Func<IEnumerable<string>> _roots;
    private readonly Func<HookCommandConfig, HookEvent, CancellationToken, Task<HookResult>> _execute;
    private readonly Func<IEnumerable<string>> _secrets;
    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _now;
    private long _dropped;
    private long _lastDropWarningTicks;
    private int _disposed;
    public AlertRouter? Alerts { get; set; }
    public long Dropped => Interlocked.Read(ref _dropped);
    public sealed record HookResult(string Outcome, int? ExitCode, string Output, string Error, string Reason = "");
    public sealed record HookRun(string HookId, string Event, string Outcome, int? ExitCode, DateTimeOffset AtUtc, string Reason = "");
    private readonly Queue<HookRun> _recent = new();
    public HookRun[] Recent() { lock (_recent) return _recent.ToArray(); }

    public HookRunner(AppConfig config, Func<IEnumerable<string>>? extraRoots = null)
        : this(config.Hooks ?? new(), () => ContentRoots(config).Concat(extraRoots?.Invoke() ?? [])) { }

    internal HookRunner(HookConfig config, Func<IEnumerable<string>> roots,
        Func<HookCommandConfig, HookEvent, CancellationToken, Task<HookResult>>? execute = null,
        Func<IEnumerable<string>>? secrets = null, ILogger? logger = null, Func<DateTimeOffset>? now = null)
    {
        _secrets = secrets ?? (() => OutboundSecretInventory.Collect());
        _log = logger ?? Log.Logger;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        // Trust is established by direct config edit + restart. A later automated edit cannot
        // substitute a different executable into an already accepted queue item.
        var commands = new List<HookCommandConfig>();
        foreach (var command in config.Commands ?? [])
        {
            try
            {
                if (command == null || !command.Enabled) continue;
                HookCommand.Validate(command);
                commands.Add(JsonSerializer.Deserialize<HookCommandConfig>(JsonSerializer.Serialize(command))!);
            }
            catch (Exception ex)
            {
                var id = command?.Id is { Length: > 0 and <= 64 } value && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
                    ? value : "invalid";
                _log.Warning("Invalid hook {HookId} ignored: {Reason}; normal activity continues", id, FailureReason(ex));
            }
        }
        _commands = commands.ToArray();
        _roots = roots;
        _execute = execute ?? ExecuteAsync;
        _queue = Channel.CreateBounded<HookEvent>(new BoundedChannelOptions(Math.Clamp(config.QueueCapacity, 1, 4096))
        { SingleReader = true, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.DropOldest },
            _ => RecordDrop());
        if (ExecutionContext.IsFlowSuppressed()) _worker = Task.Run(WorkAsync);
        else { using (ExecutionContext.SuppressFlow()) _worker = Task.Run(WorkAsync); }
    }

    public void Publish(HookEvent data)
    {
        if (Volatile.Read(ref _disposed) != 0 || !HookEvent.Names.Contains(data.Event)) return;
        // The alert router has independent per-target queues; slow local commands
        // must not delay webhook admission or consume their capacity.
        try { Alerts?.PublishHook(data.Event, data.Release, data.Status); }
        catch { _log.Warning("Hook webhook admission failed; engine activity is unaffected"); }
        if (_commands.Any(command => command.Events.Contains(data.Event)))
            _queue.Writer.TryWrite(data);
    }

    private void RecordDrop()
    {
        var count = Interlocked.Increment(ref _dropped);
        var now = _now().UtcTicks;
        var previous = Interlocked.Read(ref _lastDropWarningTicks);
        if (previous != 0 && now - previous < TimeSpan.TicksPerMinute) return;
        if (Interlocked.CompareExchange(ref _lastDropWarningTicks, now, previous) == previous)
            _log.Warning("Hook command queue full; oldest subscribed event dropped ({Dropped} total)", count);
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var data in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                foreach (var command in _commands.Where(c => c.Enabled && c.Events.Contains(data.Event)))
                {
                    HookResult result;
                    try
                    {
                        var roots = _roots().ToArray();
                        ValidatePaths(command, roots);
                        var local = data.LocalPath;
                        if (local.Length > 0 && (!Path.IsPathFullyQualified(local) ||
                            (!File.Exists(local) && !Directory.Exists(local)) || HookCommand.UnsafeReparseReason(local) != null)) local = "";
                        result = await _execute(command, data with { LocalPath = local }, _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { result = new("cancelled", null, "", ""); }
                    catch (Exception ex) { result = new("failed", null, "", "", FailureReason(ex)); }
                    lock (_recent)
                    {
                        while (_recent.Count >= 100) _recent.Dequeue();
                        _recent.Enqueue(new(command.Id, data.Event, result.Outcome, result.ExitCode, _now(), result.Reason));
                    }
                    // Output is bounded before redaction and never alters a download's verdict.
                    string output = "", error = "";
                    try
                    {
                        var secrets = _secrets().ToArray();
                        output = Tail(OutboundPayloadRedactor.PrepareText(Tail(result.Output, command.OutputLimitCharacters), secrets), command.OutputLimitCharacters);
                        error = Tail(OutboundPayloadRedactor.PrepareText(Tail(result.Error, command.OutputLimitCharacters), secrets), command.OutputLimitCharacters);
                    }
                    catch { /* Missing redaction evidence withholds diagnostic output only. */ }
                    _log.Information("Hook {HookId} {HookEvent}: {Outcome}, exit {ExitCode}; reason {Reason}; stdout {Output}; stderr {Error}",
                        command.Id, data.Event, result.Outcome, result.ExitCode, result.Reason, output, error);
                    if (_stop.IsCancellationRequested) return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static string Tail(string value, int limit) => value.Length <= limit ? value : value[^limit..];

    private static string FailureReason(Exception ex) => ex switch
    {
        HookCommand.PathRefusalException or ArgumentException => ex.Message,
        FileNotFoundException => "Configured hook program or script is missing",
        UnauthorizedAccessException => "Hook program or script access was denied",
        IOException => "Hook program or script could not be read",
        _ => "Hook execution failed (" + ex.GetType().Name + ")"
    };

    internal static void ValidatePaths(HookCommandConfig command, IEnumerable<string> roots)
    {
        HookCommand.Validate(command);
        var paths = new[] { command.Executable, command.ScriptPath }.Where(p => p.Length > 0).ToArray();
        foreach (var path in paths)
        {
            if (!HookCommand.IsOutsideRoots(path, roots))
                throw new HookCommand.PathRefusalException("Hook program and script paths must be outside content folders");
            if (HookCommand.UnsafeReparseReason(path) is { } reason)
                throw new HookCommand.PathRefusalException("Hook program or script refused: " + reason);
        }
        if (!File.Exists(command.Executable) || command.ScriptPath.Length > 0 && !File.Exists(command.ScriptPath))
            throw new FileNotFoundException("Configured hook program is missing");
    }

    internal static IEnumerable<string> ContentRoots(AppConfig config)
    {
        var roots = new List<string> { config.Downloads.LocalPath, config.Torrent.LastDownloadFolder, config.Library.StagingRoot,
            config.Library.MovieRoot, config.Library.TvRoot };
        roots.Add(Path.Combine(ConfigManager.AppDataPath, "Player"));
        roots.Add(Path.Combine(ConfigManager.AppDataPath, "torrent-downloads"));
        roots.AddRange(config.Downloads.CategoryPaths.Values);
        var path = Path.Combine(ConfigManager.AppDataPath, "extractor-settings.json");
        if (File.Exists(path))
        {
            // This file identifies executable-content boundaries, not engine admission state.
            using var data = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var field in data.RootElement.EnumerateObject())
                if (field.Name.Equals("watchFolders", StringComparison.OrdinalIgnoreCase))
                    roots.AddRange(field.Value.EnumerateArray().Select(v => v.GetString() ?? ""));
                else if (field.Name.Equals("customOutputPath", StringComparison.OrdinalIgnoreCase)) roots.Add(field.Value.GetString() ?? "");
        }
        return roots;
    }

    internal static async Task<HookResult> ExecuteAsync(HookCommandConfig command, HookEvent data, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(command.TimeoutSeconds));
        using var process = new Process { StartInfo = HookCommand.Build(command, data) };
        using var job = new HookProcessJob();
        process.Start();
        try { job.Attach(process); }
        catch { try { process.Kill(entireProcessTree: true); } catch { } throw; }
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        var stdout = HookCommand.CaptureAsync(process.StandardOutput, command.OutputLimitCharacters, deadline.Token);
        var stderr = HookCommand.CaptureAsync(process.StandardError, command.OutputLimitCharacters, deadline.Token);
        async Task Input()
        {
            try { await process.StandardInput.WriteAsync(JsonSerializer.Serialize(data).AsMemory(), deadline.Token).ConfigureAwait(false); }
            catch (IOException) { /* Hooks may choose environment variables and close stdin. */ }
            finally { process.StandardInput.Close(); }
        }
        var input = Input();
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr, input).WaitAsync(deadline.Token).ConfigureAwait(false);
            return new(process.ExitCode == 0 ? "completed" : "nonzero", process.ExitCode, stdout.Result, stderr.Result);
        }
        catch
        {
            var timedOut = deadline.IsCancellationRequested;
            job.Dispose(); // Also kills descendants whose parent has already exited.
            try { process.Kill(entireProcessTree: true); } catch { }
            // Kill only requests termination. Until the parent has exited it still holds its
            // working directory (the script folder) and any files it opened.
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { }
            deadline.Cancel();
            try { await Task.WhenAll(stdout, stderr, input).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch { }
            // A misbehaving child must never pin the serial worker during cancellation.
            _ = Task.WhenAll(stdout, stderr, input).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            stop.ThrowIfCancellationRequested();
            return new(timedOut ? "timeout" : "failed", null,
                stdout.IsCompletedSuccessfully ? stdout.Result : "", stderr.IsCompletedSuccessfully ? stderr.Result : "");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        _stop.Cancel();
        _ = _worker.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
    public async ValueTask DisposeAsync() { Dispose(); await _worker.ConfigureAwait(false); }
}
