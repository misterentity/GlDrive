using System;
using System.IO;
using GlDrive.Config;
using Xunit;

namespace GlDrive.Tests;

public sealed class ScreenshotModeIsolationTests
{
    private static string ReadSource(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException($"Could not locate {relativePath}");
    }

    [Fact]
    public void Screenshot_mode_exits_before_touching_production_lifecycle_state()
    {
        var source = ReadSource("src/GlDrive/App.xaml.cs");
        var screenshot = source.IndexOf("e.Args.Contains(\"--screenshots\"", StringComparison.Ordinal);
        var disableCredentials = source.IndexOf("CredentialStore.AccessDisabled = true", screenshot, StringComparison.Ordinal);
        var loadConfig = source.IndexOf("ConfigManager.Load()", screenshot, StringComparison.Ordinal);
        var restartRegistration = source.IndexOf("RegisterApplicationRestart(null, 0)", StringComparison.Ordinal);
        var crashMarker = source.IndexOf("var crashMarker =", StringComparison.Ordinal);
        var updateCleanup = source.IndexOf("UpdateChecker.CleanupOldUpdateFiles()", StringComparison.Ordinal);

        Assert.True(screenshot >= 0);
        Assert.True(disableCredentials > screenshot && disableCredentials < loadConfig);
        Assert.True(screenshot < restartRegistration);
        Assert.True(screenshot < crashMarker);
        Assert.True(screenshot < updateCleanup);
        Assert.Contains("if (_ownsCrashMarker)", source);
        Assert.Contains("_ownsCrashMarker = true", source);
    }

    [Fact]
    public void Screenshot_mode_blocks_every_credential_manager_operation()
    {
        var source = ReadSource("src/GlDrive/Config/CredentialStore.cs");

        Assert.Equal(13, source.Split("if (AccessDisabled)", StringSplitOptions.None).Length - 1);

        CredentialStore.AccessDisabled = true;
        try
        {
            Assert.Null(CredentialStore.GetProxyPassword("unused.invalid", 1, "unused"));
            Assert.Null(CredentialStore.GetPassword("unused.invalid", 1, "unused"));
            Assert.Null(CredentialStore.GetIrcPassword("unused.invalid", 1, "unused"));
            Assert.Null(CredentialStore.GetSshPassword("unused.invalid", 1, "unused"));
            Assert.Null(CredentialStore.GetApiKey("unused-screenshot-test"));
        }
        finally
        {
            CredentialStore.AccessDisabled = false;
        }
    }

    [Fact]
    public void Screenshot_settings_use_only_demo_state()
    {
        var source = ReadSource("src/GlDrive/UI/ScreenshotCapture.cs");

        Assert.Contains("config.Downloads.LocalPath = @\"C:\\Downloads\\GlDrive\"", source);
        Assert.Contains("new SettingsWindow(config, serverManager, readLocalState: false)", source);
        Assert.Contains("new NotificationStore(persistToDisk: false)", source);
    }

    /// <summary>
    /// Updater modes must be dispatched by the custom entry point before WPF is constructed.
    /// SYSTEM scheduled tasks run without an interactive desktop, so putting this in OnStartup
    /// makes the hand-off depend on WPF startup succeeding in session 0.
    /// </summary>
    [Fact]
    public void Updater_modes_run_before_WPF_initialization()
    {
        var program = ReadSource("src/GlDrive/Program.cs");
        var taskDispatch = program.IndexOf("UpdateChecker.ApplyUpdateFromTask()", StringComparison.Ordinal);
        var interactiveDispatch = program.IndexOf("UpdateChecker.ApplyUpdate(updatePid", StringComparison.Ordinal);
        var wpfConstruction = program.IndexOf("var app = new App()", StringComparison.Ordinal);

        Assert.True(taskDispatch >= 0 && taskDispatch < wpfConstruction);
        Assert.True(interactiveDispatch >= 0 && interactiveDispatch < wpfConstruction);
        Assert.Contains("Array.IndexOf(args, \"--screenshots\") < 0", program);

        var app = ReadSource("src/GlDrive/App.xaml.cs");
        Assert.DoesNotContain("ApplyUpdateFromTask", app);
        Assert.DoesNotContain("--apply-update", app);
    }

    [Fact]
    public void System_task_consumes_handoff_before_nonreturning_update_dispatch()
    {
        var source = ReadSource("src/GlDrive/Services/UpdateChecker.cs");
        var entry = source.IndexOf("public static void ApplyUpdateFromTask()", StringComparison.Ordinal);
        var clear = source.IndexOf("UpdateTaskHandoff.Clear(path)", entry, StringComparison.Ordinal);
        var dispatch = source.IndexOf("ApplyUpdate(handoff.Pid", entry, StringComparison.Ordinal);

        Assert.True(entry >= 0);
        Assert.True(clear > entry && clear < dispatch);
        Assert.Contains("if (File.Exists(path))", source[clear..dispatch]);
    }

    [Fact]
    public void Interactive_watchdog_owns_post_SYSTEM_update_relaunch()
    {
        var program = ReadSource("src/GlDrive/Program.cs");
        Assert.Contains("update-complete-{targetPid}.signal", program);
        Assert.Contains("Update completed; relaunched GlDrive", program);

        var checker = ReadSource("src/GlDrive/Services/UpdateChecker.cs");
        var signal = checker.IndexOf("SignalUpdateCompletion(pid)", StringComparison.Ordinal);
        var kill = checker.IndexOf("Process.GetCurrentProcess().Kill()", signal, StringComparison.Ordinal);
        Assert.True(signal >= 0 && signal < kill);

        var tray = ReadSource("src/GlDrive/UI/TrayViewModel.cs");
        Assert.DoesNotContain("File.WriteAllText(Path.Combine(appData, \".updating\")", tray);
    }
}
