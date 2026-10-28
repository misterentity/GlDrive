using System.IO;
using GlDrive.Config;
using GlDrive.Services;
using GlDrive.Util;
using Xunit;

namespace GlDrive.Tests;

public sealed class TestProcessIsolationTests
{
    [Fact]
    public void DefaultAppStateAndConfigPathsUseSuiteTemporaryRoot()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(TestProcessIsolation.AppDataRoot), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TestProcessIsolation.AppDataRoot, ConfigManager.AppDataPath);
        Assert.Equal(TestProcessIsolation.AppDataRoot, AppDataPaths.Root);
        Assert.Equal(Path.Combine(TestProcessIsolation.AppDataRoot, "appsettings.json"), ConfigManager.ConfigPath);
        Assert.True(Directory.Exists(TestProcessIsolation.AppDataRoot));
    }

    [Fact]
    public void TestCredentialDisableCannotBeReversedByUtilityModeCleanup()
    {
        var previous = CredentialStore.AccessDisabled;
        try
        {
            CredentialStore.AccessDisabled = false;
            Assert.True(CredentialStore.AccessDisabled);
            Assert.Null(CredentialStore.GetApiKey("isolated-test-service"));
        }
        finally { CredentialStore.AccessDisabled = previous; }
    }

    [Fact]
    public void DefaultUpdateMarkerKeyStaysInsideSuiteTemporaryRoot()
    {
        var marker = Path.Combine(TestProcessIsolation.AppDataRoot, "marker-" + Guid.NewGuid().ToString("N"));
        try
        {
            UpdateMarkerHmac.Write(marker, 4242);
            Assert.True(UpdateMarkerHmac.IsValid(marker));
            Assert.True(File.Exists(Path.Combine(TestProcessIsolation.AppDataRoot, ".updating-key")));
        }
        finally { File.Delete(marker); }
    }
}
