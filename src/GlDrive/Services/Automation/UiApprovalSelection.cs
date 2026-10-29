using System.Collections;
using System.Text.Json;

namespace GlDrive.Services.Automation;

/// <summary>Deletion approvals bind row identity, never a mutable index or selection flag.</summary>
internal sealed record UiApprovalSelection(string Command, string[] Identities)
{
    internal static UiApprovalSelection? Capture(object model, string command)
    {
        var (many, single, identity) = command switch
        {
            "RemoveWishlistCommand" => ("SelectedWishlistItems", "SelectedWishlistItem", "Id"),
            "RemoveSelectedNotificationsCommand" => ("SelectedNotificationItems", "SelectedNotificationItem", "Id"),
            "RemoveSelectedDownloadsCommand" => ("SelectedDownloadItems", "SelectedDownloadItem", "Id"),
            "RemoveSelectedQueuedRacesCommand" => ("SelectedQueuedRaces", "", "RequestId"),
            _ => ("", "", "")
        };
        if (many.Length == 0) return null;
        var selected = model.GetType().GetProperty(many)?.GetValue(model) as IEnumerable;
        var rows = selected?.Cast<object>().Take(201).ToArray() ?? [];
        if (rows.Length == 0 && single.Length > 0 && model.GetType().GetProperty(single)?.GetValue(model) is { } row) rows = [row];
        if (rows.Length > 200) throw new AutomationDeniedException("Approve at most 200 explicit rows per call.");
        var ids = rows.Select(item =>
        {
            var id = item.GetType().GetProperty(identity)?.GetValue(item) as string;
            var server = command == "RemoveSelectedDownloadsCommand" ? item.GetType().GetProperty("ServerId")?.GetValue(item) as string : "";
            if (string.IsNullOrEmpty(id) || id.Length > 256 || server == null || server.Length > 256)
                throw new AutomationDeniedException("The selected row has no stable identity.");
            return JsonSerializer.Serialize(new[] { server, id });
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new(command, ids);
    }

    internal void Validate(object model)
    {
        var current = Capture(model, Command);
        if (current == null || !Identities.SequenceEqual(current.Identities, StringComparer.Ordinal))
            throw new AutomationDeniedException("The selected rows changed after approval was requested. Inspect and request again.");
    }

    internal string Description => $"Selection at request: {Identities.Length} persisted row(s). " +
        "Review the selected dashboard rows; execution requires the exact same selection. Identities: " +
        string.Join(", ", Identities.Take(20)) + (Identities.Length > 20 ? "; further identities omitted from this summary." : "");
}
