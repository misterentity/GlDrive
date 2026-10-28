using System.Text.Json;
using GlDrive.Config;
using GlDrive.Library;

namespace GlDrive.Services.Automation;

public sealed class NukeWatchAutomation(AppConfig config, Func<NukeWatch> watch) : IAutomationModule
{
    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("library.nuke_watch", "Read recent owned-release nuke flags and correction attempts. Uses existing completion/import records, PreDB responses and trusted sitebot facts only; no scans, polling, FTP or provider writes. Automatic corrections are separately opt-in.",
            new { type = "object", properties = new { offset = new { type = "integer", minimum = 0 }, limit = new { type = "integer", minimum = 1, maximum = 500 } }, additionalProperties = false }, readOnly: true)
    ];

    public Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operation != "library.nuke_watch") throw new ArgumentException("Unknown nuke watch operation");
        var current = watch();
        var offset = Math.Max(0, ConfigurationAutomation.Integer(arguments, "offset", 0));
        var limit = Math.Clamp(ConfigurationAutomation.Integer(arguments, "limit", 100), 1, 500);
        var entries = current.Store.Snapshot();
        return Task.FromResult<object?>(new { config.NukeWatch.Enabled, config.NukeWatch.AutoQueueFixes,
            config.NukeWatch.WatchDays, current.Dropped, current.Store.PersistenceHealthy,
            entries = entries.Skip(offset).Take(limit).ToArray(), total = entries.Count, offset });
    }
}
