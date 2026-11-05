using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using GlDrive.Downloads.ArchiveExtraction;

namespace GlDrive.Services.Automation;

public sealed class ExtractorAutomation : IAutomationModule, IAutomationApprovalBinding
{
    private static object Schema(object properties, params string[] required) => new { type = "object", properties, required, additionalProperties = false };
    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("extractor.state", "Inspect archive queue, progress, watch folders and extractor settings. Initializes the existing desktop extractor if needed.", Schema(new { })),
        AutomationToolDefinition.Create("extractor.add", "Add existing absolute archive or directory paths to the extraction queue.", Schema(new { paths = new { type = "array", minItems = 1, maxItems = 100, items = new { type = "string", maxLength = 4096 } } }, "paths")),
        AutomationToolDefinition.Create("extractor.remove", "Remove one archive from the queue without deleting its file.", Schema(new { path = new { type = "string" } }, "path")),
        AutomationToolDefinition.Create("extractor.clear", "Clear the extraction queue without deleting files.", Schema(new { })),
        AutomationToolDefinition.Create("extractor.start", "Extract queued archives with the current output, overwrite and source-deletion settings. Read extractor.state first. Returns final archive statuses; check errors. Long work returns jobId.", Schema(new { password = new { type = "string", maxLength = 4096 } }), destructive: true),
        AutomationToolDefinition.Create("extractor.cancel", "Cancel the current extraction.", Schema(new { })),
        AutomationToolDefinition.Create("extractor.settings", "Replace extractor settings using the settings object from extractor.state. outputMode: 0=beside archive,1=archive subfolder,2=custom. overwriteMode: 0=overwrite,1=skip,2=rename. Enabling watch starts automatic extraction.", Schema(new { settings = new { type = "object" } }, "settings"), destructive: true)
    ];

    public Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct)
        => ExecuteCoreAsync(operation, arguments, ct, null, null);

    public async Task<IAutomationApprovedCall?> BindApprovalAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        if (operation is not ("extractor.start" or "extractor.settings")) return null;
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("Desktop session unavailable");
        return await dispatcher.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            var engine = GetEngine(dispatcher);
            return (IAutomationApprovedCall)new BoundCall(this, operation, arguments.Clone(), engine, CaptureApproval(engine, operation, arguments));
        }, DispatcherPriority.Normal, ct);
    }

    /// <summary>
    /// The live extraction engine — the same one the extractor window shows, if it is open.
    /// Inspecting the saved state must never start automatic extraction/deletion, so an
    /// engine created here loads saved settings without starting saved watchers.
    /// </summary>
    private static ArchiveExtractionEngine GetEngine(Dispatcher dispatcher) =>
        ExtractorEngineHost.Acquire(new DispatcherSynchronizationContext(dispatcher), startWatchers: false);

    private sealed class BoundCall(ExtractorAutomation owner, string operation, JsonElement arguments, ArchiveExtractionEngine engine, AutomationStateApproval approval) : IAutomationApprovedCall
    {
        public string Description => approval.Description;
        public Task<object?> ExecuteAsync(CancellationToken ct) => owner.ExecuteCoreAsync(operation, arguments, ct, engine, approval);
    }

    private async Task<object?> ExecuteCoreAsync(string operation, JsonElement arguments, CancellationToken ct, ArchiveExtractionEngine? expectedEngine, AutomationStateApproval? approval)
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("Desktop session unavailable");
        return await (await dispatcher.InvokeAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            if (expectedEngine != null && !ReferenceEquals(ExtractorEngineHost.Current, expectedEngine))
                throw new AutomationDeniedException("The extractor window changed while approval was pending.");
            var engine = expectedEngine ?? GetEngine(dispatcher);
            if (approval != null) ValidateApproval(engine, approval);
            return await Execute(engine, dispatcher, operation, arguments, ct);
        }, DispatcherPriority.Normal, ct));
    }

    private static object State(ArchiveExtractionEngine engine) => new
    {
        extracting = engine.IsExtracting,
        savedWatchersDeferred = engine.WatchResumeDeferred,
        archives = engine.Archives.ToArray(),
        settings = engine.CurrentSettings()
    };

    private static AutomationStateApproval CaptureApproval(ArchiveExtractionEngine engine, string operation, JsonElement arguments)
    {
        if (engine.IsExtracting) throw new InvalidOperationException("Wait for extraction to finish before requesting approval.");
        var settings = operation == "extractor.settings"
            ? arguments.GetProperty("settings").Deserialize<ExtractorSettings>(ArchiveExtractionEngine.JsonOptions) ?? throw new ArgumentException("settings required")
            : engine.CurrentSettings();
        var paths = engine.Archives.Where(a => a.Status != "Done").Select(a => a.FilePath).ToArray();
        var description = (operation == "extractor.start" ? $"Extract {paths.Length} queued archives." : "Change extractor settings.") +
            $"\nDelete source archives after success: {settings.DeleteAfterExtract}. Overwrite mode: {settings.OverwriteMode} (0 overwrite, 1 skip, 2 rename)." +
            $"\nOutput mode: {settings.OutputMode} (0 beside archive, 1 archive subfolder, 2 custom). Custom path: {settings.CustomOutputPath}." +
            $"\nCreate subfolder: {settings.CreateSubfolder}. Watch enabled: {settings.WatchEnabled}. Recursive scan: {settings.ScanSubfolders}." +
            "\nWatch folders:\n" + string.Join("\n", (settings.WatchFolders ?? []).Take(10)) +
            "\nQueued archives:\n" + string.Join("\n", paths.Take(10));
        if (paths.Length > 10 || settings.WatchFolders?.Count > 10) description += "\nAdditional paths omitted; inspect the local extractor or deny.";
        var secrets = new[] { engine.Password, arguments.TryGetProperty("password", out var password) ? password.GetString() ?? "" : "" };
        return new(ApprovalState(engine), description, secrets, engine);
    }

    private static void ValidateApproval(ArchiveExtractionEngine engine, AutomationStateApproval approval) =>
        approval.Validate(ApprovalState(engine), engine);

    private static object ApprovalState(ArchiveExtractionEngine engine) => new
    {
        state = State(engine), password = engine.Password
    };

    private static async Task<object> Execute(ArchiveExtractionEngine engine, Dispatcher dispatcher, string action, JsonElement arguments, CancellationToken ct)
    {
        dispatcher.VerifyAccess();
        ct.ThrowIfCancellationRequested();
        if (action == "extractor.state") return State(engine);
        if (action == "extractor.cancel") { engine.CancelExtraction(); return State(engine); }
        if (engine.IsExtracting) throw new InvalidOperationException("Wait for extraction to finish or cancel it first");
        if (action == "extractor.add")
        {
            var paths = arguments.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!).ToArray();
            if (paths.Any(p => !Path.IsPathFullyQualified(p) || (!File.Exists(p) && !Directory.Exists(p))))
                throw new ArgumentException("Each archive or directory must be an existing absolute path");
            engine.AddPaths(paths);
        }
        else if (action == "extractor.remove")
        {
            var path = arguments.GetProperty("path").GetString();
            var item = engine.Archives.FirstOrDefault(a => string.Equals(a.FilePath, path, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException("Archive not found");
            engine.Remove(item);
        }
        else if (action == "extractor.clear") engine.Clear();
        else if (action == "extractor.settings")
        {
            var settings = arguments.GetProperty("settings").Deserialize<ExtractorSettings>(new JsonSerializerOptions(ArchiveExtractionEngine.JsonOptions)
            { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                ?? throw new ArgumentException("settings required");
            if (settings.OutputMode is < 0 or > 2 || settings.OverwriteMode is < 0 or > 2)
                throw new ArgumentException("outputMode and overwriteMode must be 0, 1 or 2");
            if (settings.WatchFolders == null || settings.WatchFolders.Any(p => !Path.IsPathFullyQualified(p) || !Directory.Exists(p)))
                throw new ArgumentException("Watch folders must exist and be absolute paths");
            if (settings.WatchEnabled && settings.WatchFolders.Count == 0) throw new ArgumentException("Watch folders required");
            if (settings.OutputMode == 2 && !Path.IsPathFullyQualified(settings.CustomOutputPath)) throw new ArgumentException("Custom output must be absolute");
            engine.ApplySettings(settings);
        }
        else if (action == "extractor.start")
        {
            if (arguments.TryGetProperty("password", out var password)) engine.ReplacePassword(password.GetString() ?? "");
            using var cancellation = ct.Register(() => dispatcher.BeginInvoke(() => engine.CancelExtraction()));
            try { await engine.ExtractAllAsync(); }
            finally { engine.ReplacePassword(""); }
        }
        else throw new KeyNotFoundException("Unknown extractor action");
        return State(engine);
    }
}
