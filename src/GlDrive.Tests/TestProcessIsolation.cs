using System.IO;
using System.Runtime.CompilerServices;
using GlDrive.Config;

namespace GlDrive.Tests;

internal static class TestProcessIsolation
{
    internal static string AppDataRoot { get; private set; } = "";

    [ModuleInitializer]
    internal static void Initialize()
    {
        AppDataRoot = Path.Combine(Path.GetTempPath(), "gldrive-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(AppDataRoot);
        ConfigManager.AppDataPathOverride = AppDataRoot;
        CredentialStore.DisableAccessForTests();
    }
}
