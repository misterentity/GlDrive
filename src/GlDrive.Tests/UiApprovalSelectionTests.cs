using GlDrive.Services.Automation;
using Xunit;

namespace GlDrive.Tests;

public sealed class UiApprovalSelectionTests
{
    public sealed class Row { public string Id { get; set; } = ""; public string ServerId { get; set; } = ""; public string RequestId { get; set; } = ""; }
    public sealed class Model
    {
        public Row[] SelectedWishlistItems { get; set; } = [];
        public Row? SelectedWishlistItem { get; set; }
        public Row[] SelectedNotificationItems { get; set; } = [];
        public Row? SelectedNotificationItem { get; set; }
        public Row[] SelectedDownloadItems { get; set; } = [];
        public Row? SelectedDownloadItem { get; set; }
        public Row[] SelectedQueuedRaces { get; set; } = [];
    }

    [Fact]
    public void Changed_selection_cannot_invoke_the_approved_removal()
    {
        var model = new Model { SelectedWishlistItems = [new() { Id = "first" }] };
        var approval = UiApprovalSelection.Capture(model, "RemoveWishlistCommand")!;
        model.SelectedWishlistItems = [new() { Id = "different" }];
        var invoked = false;
        Assert.Throws<AutomationDeniedException>(() => { approval.Validate(model); invoked = true; });
        Assert.False(invoked);
    }

    [Fact]
    public void Same_id_reordering_and_row_replacement_preserves_approved_target()
    {
        var model = new Model { SelectedWishlistItems = [new() { Id = "one" }, new() { Id = "two" }] };
        var approval = UiApprovalSelection.Capture(model, "RemoveWishlistCommand")!;
        model.SelectedWishlistItems = [new() { Id = "two" }, new() { Id = "one" }];
        approval.Validate(model);
        Assert.Equal(2, approval.Identities.Length);
    }

    [Fact]
    public void Download_identity_includes_queue_and_single_selection_fallback()
    {
        var model = new Model { SelectedDownloadItem = new() { Id = "same-id", ServerId = "queue-one" } };
        var approval = UiApprovalSelection.Capture(model, "RemoveSelectedDownloadsCommand")!;
        model.SelectedDownloadItem.ServerId = "queue-two";
        Assert.Throws<AutomationDeniedException>(() => approval.Validate(model));
    }

    [Fact]
    public void Race_identity_uses_request_and_feed_removal_uses_single_fallback()
    {
        var model = new Model { SelectedQueuedRaces = [new() { RequestId = "request-one" }], SelectedNotificationItem = new() { Id = "feed-one" } };
        var race = UiApprovalSelection.Capture(model, "RemoveSelectedQueuedRacesCommand")!;
        var feed = UiApprovalSelection.Capture(model, "RemoveSelectedNotificationsCommand")!;
        Assert.Single(race.Identities); Assert.Single(feed.Identities);
        model.SelectedQueuedRaces[0].RequestId = "request-two";
        Assert.Throws<AutomationDeniedException>(() => race.Validate(model));
    }

    [Fact]
    public void View_actions_require_no_selection_binding()
    {
        Assert.Null(UiApprovalSelection.Capture(new Model(), "ClearFinishedCommand"));
        Assert.Null(UiApprovalSelection.Capture(new Model(), "DismissJobCommand"));
    }
}
