using System.IO;
using GlDrive.Config;
using GlDrive.Library;
using Xunit;

namespace GlDrive.Tests;

public sealed class NukeWatchOrderingTests
{
    [Fact]
    public void Repeated_newer_site_nuke_prevents_an_older_unnuke_clearing_it_after_restart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gldrive-nuke-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var now = DateTime.UtcNow;
            var settings = new NukeWatchConfig();
            var path = Path.Combine(folder, "history.json");
            const string release = "Sample.Movie.2026.1080p.WEB.H264-GROUP";
            var store = new NukeWatchStore(path, () => settings, () => now);
            store.ObserveOwned(new(release, Path.Combine(folder, "owned"), now.AddMinutes(-5)));
            store.ObserveNuke(new(release, "site:synthetic", "corrupt", now.AddMinutes(-4)));
            store.ObserveNuke(new(release, "site:synthetic", "corrupt", now.AddMinutes(-2)));
            var restarted = new NukeWatchStore(path, () => settings, () => now);
            restarted.ObserveNuke(new(release, "site:synthetic", "cleared", now.AddMinutes(-3), false));
            Assert.True(Assert.Single(restarted.Snapshot()).Flagged);
        }
        finally { Directory.Delete(folder, true); }
    }
}
