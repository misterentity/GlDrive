using System.IO;
using GlDrive.Util;
using Xunit;

namespace GlDrive.Tests;

public class SecureFileSnapshotTests
{
    [Fact]
    public async Task Concurrent_saves_capture_state_in_commit_order()
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.json");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var state = "old";
        var first = Task.Run(() => SecureFile.WriteAllTextRestricted(path, () =>
        {
            var snapshot = state;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return snapshot;
        }));
        Task? second = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            state = "new";
            second = Task.Run(() => SecureFile.WriteAllTextRestricted(path, () => state));
            release.Set();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("new", File.ReadAllText(path));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(first, second ?? Task.CompletedTask);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Failed_snapshot_preserves_existing_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "gldrive-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.json");
        try
        {
            SecureFile.WriteAllTextRestricted(path, "original");
            Assert.Throws<IOException>(() => SecureFile.WriteAllTextRestricted(path,
                () => throw new IOException("Cannot capture state")));
            Assert.Equal("original", File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
