using System.IO;
using GlDrive.Config;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

[CollectionDefinition("Config read-only mode", DisableParallelization = true)]
public sealed class ConfigReadOnlyCollection;

[Collection("Config read-only mode")]
public sealed class SpreadManagerReadOnlyTests
{
    [Fact]
    public void ReadOnlyManager_DisposePreservesConcurrentSpeedHistoryUpdate()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gldrive-readonly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "spread-speed-history.json");
        const string original = "{\"source\\tdestination\":[100]}";
        const string updated = "{\"source\\tdestination\":[100,200]}";
        File.WriteAllText(path, original);
        var wasReadOnly = ConfigManager.ReadOnly;
        try
        {
            ConfigManager.ReadOnly = true;
            using (var manager = new SpreadManager(new AppConfig(), directory))
            {
                // A running application records another transfer while its demo
                // renderer is open. Disposing the renderer must not erase it.
                File.WriteAllText(path, updated);
            }
            Assert.Equal(updated, File.ReadAllText(path));
        }
        finally
        {
            ConfigManager.ReadOnly = wasReadOnly;
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SpeedHistoryPersistenceIsEnabledOnlyForWritableManagers(bool readOnly, bool expectFile)
    {
        var directory = Path.Combine(Path.GetTempPath(), "gldrive-readonly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "spread-speed-history.json");
        var wasReadOnly = ConfigManager.ReadOnly;
        try
        {
            ConfigManager.ReadOnly = readOnly;
            using (var manager = new SpreadManager(new AppConfig(), directory)) { }
            Assert.Equal(expectFile, File.Exists(path));
        }
        finally
        {
            ConfigManager.ReadOnly = wasReadOnly;
            Directory.Delete(directory, recursive: true);
        }
    }
}
