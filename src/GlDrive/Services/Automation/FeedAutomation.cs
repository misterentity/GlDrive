using System.Text.Json;
using GlDrive.Downloads;

namespace GlDrive.Services.Automation;

public sealed class FeedAutomation(NotificationStore notifications) : IAutomationModule
{
    private static object IdArguments => new { type = "object", properties = new { id = new { type = "string", minLength = 1, maxLength = 128 } }, required = new[] { "id" }, additionalProperties = false };
    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("feed.views", "Read named release-feed filters, unread counts over retained visible items, and feed-only category mutes. No network work or acquisition changes.",
            new { type = "object", properties = new { }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("feed.save", "Create a named text/category/server-display-name feed filter, or replace one by id. Empty or All category/serverName matches all. Unread markers are preserved on replacement; selecting a view never marks it read.",
            new { type = "object", properties = new { id = new { type = "string", maxLength = 128 }, name = new { type = "string", minLength = 1, maxLength = 80 }, text = new { type = "string", maxLength = 512 }, category = new { type = "string", maxLength = 128 }, serverName = new { type = "string", maxLength = 256 } }, required = new[] { "name" }, additionalProperties = false }),
        AutomationToolDefinition.Create("feed.remove", "Remove one saved view. Retained release notifications, wishlist, racing and alerts are unchanged.", IdArguments),
        AutomationToolDefinition.Create("feed.mark_read", "Mark the current retained visible items of one saved view read. Later arrivals, including older or equal timestamps, remain unread.", IdArguments),
        AutomationToolDefinition.Create("feed.mute", "Hide or unhide one category for one server ID in every feed view only. Never changes polling exclusions, wishlist, auto-race or alerts.",
            new { type = "object", properties = new { serverId = new { type = "string", minLength = 1, maxLength = 128 }, category = new { type = "string", minLength = 1, maxLength = 128 }, muted = new { type = "boolean" } }, required = new[] { "serverId", "category", "muted" }, additionalProperties = false })
    ];

    public Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = notifications.FeedViews;
        object result = operation switch
        {
            "feed.views" => new { views = store.Counts(notifications.Items), mutedCategories = store.MutedCategories },
            "feed.save" => store.Save(ConfigurationAutomation.String(arguments, "id"), AutomationService.Required(arguments, "name", 80),
                ConfigurationAutomation.String(arguments, "text") ?? "", ConfigurationAutomation.String(arguments, "category") ?? "All",
                ConfigurationAutomation.String(arguments, "serverName") ?? "All"),
            "feed.remove" => new { removed = store.Remove(AutomationService.Required(arguments, "id", 128)) },
            "feed.mark_read" => new { marked = store.MarkRead(AutomationService.Required(arguments, "id", 128), notifications.Items) },
            "feed.mute" => Mute(arguments),
            _ => throw new ArgumentException("Unknown feed operation")
        };
        return Task.FromResult<object?>(result);
    }

    private object Mute(JsonElement arguments)
    {
        notifications.FeedViews.SetMuted(AutomationService.Required(arguments, "serverId", 128),
            AutomationService.Required(arguments, "category", 128), ConfigurationAutomation.Boolean(arguments, "muted"));
        return new { mutedCategories = notifications.FeedViews.MutedCategories };
    }
}
