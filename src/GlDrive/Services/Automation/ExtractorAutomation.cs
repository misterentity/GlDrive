using System.Text.Json;
using System.Windows;
using GlDrive.UI;

namespace GlDrive.Services.Automation;

public sealed class ExtractorAutomation : IAutomationModule
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

    public async Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("Desktop session unavailable");
        return await (await dispatcher.InvokeAsync(async () =>
        {
            var window = Application.Current.Windows.OfType<ExtractorWindow>().FirstOrDefault();
            if (window == null)
            {
                window = new ExtractorWindow { ShowInTaskbar = false };
                window.Show(); window.Hide();
            }
            return await window.AutomationExecute(operation, arguments, ct);
        }, System.Windows.Threading.DispatcherPriority.Normal, ct));
    }
}
