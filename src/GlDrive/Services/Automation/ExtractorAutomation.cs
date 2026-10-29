using System.Text.Json;
using System.Windows;
using GlDrive.UI;

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
            var window = GetWindow();
            return (IAutomationApprovedCall)new BoundCall(this, operation, arguments.Clone(), window, window.CaptureAutomationApproval(operation, arguments));
        }, System.Windows.Threading.DispatcherPriority.Normal, ct);
    }

    private static ExtractorWindow GetWindow()
    {
        var window = Application.Current.Windows.OfType<ExtractorWindow>().FirstOrDefault();
        if (window == null)
        {
            // Inspecting the saved state must never start automatic extraction/deletion.
            window = new ExtractorWindow(demoMode: false, startWatchers: false) { ShowInTaskbar = false };
            window.Show(); window.Hide();
        }
        return window;
    }

    private sealed class BoundCall(ExtractorAutomation owner, string operation, JsonElement arguments, ExtractorWindow window, AutomationStateApproval approval) : IAutomationApprovedCall
    {
        public string Description => approval.Description;
        public Task<object?> ExecuteAsync(CancellationToken ct) => owner.ExecuteCoreAsync(operation, arguments, ct, window, approval);
    }

    private async Task<object?> ExecuteCoreAsync(string operation, JsonElement arguments, CancellationToken ct, ExtractorWindow? expectedWindow, AutomationStateApproval? approval)
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("Desktop session unavailable");
        return await (await dispatcher.InvokeAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            if (expectedWindow != null && !Application.Current.Windows.OfType<ExtractorWindow>().Contains(expectedWindow))
                throw new AutomationDeniedException("The extractor window changed while approval was pending.");
            var window = expectedWindow ?? GetWindow();
            if (approval != null) window.ValidateAutomationApproval(approval);
            return await window.AutomationExecute(operation, arguments, ct);
        }, System.Windows.Threading.DispatcherPriority.Normal, ct));
    }
}
