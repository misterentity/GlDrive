using System.Text.Json;
using GlDrive.Services.Statistics;

namespace GlDrive.Services.Automation;

/// <summary>Local telemetry analytics; never creates an operational action.</summary>
public sealed class StatisticsAutomation(Func<StatisticsService> service) : IAutomationModule
{
    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("stats.summary", "Read local statistics for 1–90 days: estimated endpoint payload bytes including relay, routes, clean-history evidence, queue wait, matched-announce lag, download outcomes and site-health observation coverage. Bounded background JSONL/gzip reads; partial coverage is explicit. Historical transport and uptime may be unknown; historical unmarked duplicate skips can overstate payload. No polling, FTP, configuration or allowance changes.",
            new { type = "object", properties = new { days = new { type = "integer", minimum = 1, maximum = 90 } }, additionalProperties = false }, readOnly: true)
    ];

    public async Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operation != "stats.summary") throw new ArgumentException("Unknown statistics operation");
        var days = 7;
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Statistics arguments must be an object");
        if (arguments.TryGetProperty("days", out var value) &&
            (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out days) || days is < 1 or > 90))
            throw new ArgumentException("days must be an integer from 1 to 90");
        return await service().ReadAsync(days, cancellationToken).ConfigureAwait(false);
    }
}
