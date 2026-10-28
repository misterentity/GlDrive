using GlDrive.Downloads;

namespace GlDrive.Services.Hooks;

public sealed record HookEvent(string Event, string Release, string Category, string Quality, string Status,
    string LocalPath = "")
{
    public static readonly string[] Names = ["download.complete", "download.failed", "race.finished", "wishlist.grab", "nuke"];

    public static HookEvent ForDownload(DownloadItem item) => new(
        item.Status == DownloadStatus.Completed ? "download.complete" : "download.failed",
        item.ReleaseName, item.Category, SceneNameParser.Parse(item.ReleaseName).Quality.ToString(), item.Status.ToString(),
        item.Status == DownloadStatus.Completed ? item.LocalPath : "");
}
