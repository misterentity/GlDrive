using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using GlDrive.Services;
using Xunit;

namespace GlDrive.Tests;

public sealed class UpdaterExitStatusTests : IClassFixture<UpdaterExitHost>
{
    private readonly UpdaterExitHost _host;
    public UpdaterExitStatusTests(UpdaterExitHost host) => _host = host;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Native_termination_reports_the_result_without_running_teardown(int exitCode)
    {
        // The real production helper runs in a disposable process with a foreground thread,
        // a ProcessExit handler, and a finally block. No updater/install state is touched.
        var marker = Path.Combine(_host.Root, Guid.NewGuid().ToString("N"));
        var result = await UpdaterExitHost.Run("dotnet", _host.Assembly,
            typeof(UpdateChecker).Assembly.Location, exitCode.ToString(), marker);

        Assert.True(result.Code == exitCode, result.Output);
        Assert.False(File.Exists(marker), "Forced updater termination must not run managed teardown");
    }

    [Fact]
    public void Rejected_handoffs_report_failure_without_constructing_the_application()
    {
        var program = Source("Program.cs");
        Assert.Contains("return UpdateChecker.ApplyUpdateFromTask();", program);
        var checker = Source("Services/UpdateChecker.cs");
        var start = checker.IndexOf("public static int ApplyUpdateFromTask()", StringComparison.Ordinal);
        var dispatch = checker.IndexOf("ApplyUpdate(handoff.Pid", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && dispatch > start);
        var beforeDispatch = checker[start..dispatch];
        Assert.Equal(2, Regex.Matches(beforeDispatch, @"return 1;").Count);
        Assert.DoesNotContain("return 0;", beforeDispatch);
    }

    [Fact]
    public void Only_a_completed_install_sets_success_and_rollback_keeps_failure()
    {
        var checker = Source("Services/UpdateChecker.cs");
        var start = checker.IndexOf("public static void ApplyUpdate(int pid", StringComparison.Ordinal);
        var end = checker.IndexOf("internal static string UpdateCompletionPath", start, StringComparison.Ordinal);
        var apply = checker[start..end];
        var launch = apply.IndexOf("var child = LaunchViaDesktopShell(newExe);", StringComparison.Ordinal);
        var success = apply.IndexOf("exitCode = 0;", StringComparison.Ordinal);
        var rollback = apply.IndexOf("RollBackUpdateFiles(copiedDestinations", StringComparison.Ordinal);
        var signal = apply.IndexOf("SignalUpdateCompletion(pid);", StringComparison.Ordinal);
        var terminate = apply.IndexOf("TerminateUpdaterProcess(exitCode);", StringComparison.Ordinal);
        Assert.Contains("var exitCode = 1;", apply);
        Assert.Single(Regex.Matches(apply, @"exitCode = 0;"));
        Assert.True(launch >= 0 && success > launch && rollback > success);
        Assert.True(signal > rollback && terminate > signal);
        Assert.DoesNotContain("Environment.Exit(", apply);
    }

    private static string Source(string path)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "src", "GlDrive", path);
            if (File.Exists(file)) return File.ReadAllText(file);
        }
        throw new FileNotFoundException(path);
    }
}

// Compile a tiny independent entry point with the already-installed SDK compiler. It loads
// the actual tested GlDrive assembly, so the test cannot accidentally terminate testhost.
// This needs no NuGet restore, project reference build, or production launch mode.
public sealed class UpdaterExitHost : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "gldrive-exit-" + Guid.NewGuid().ToString("N"));
    public string Assembly => Path.Combine(Root, "ExitHost.dll");

    public UpdaterExitHost()
    {
        Directory.CreateDirectory(Root);
        var sdks = Run("dotnet", "--list-sdks").GetAwaiter().GetResult();
        Assert.True(sdks.Code == 0, sdks.Output);
        var sdk = Regex.Matches(sdks.Output, @"(?m)^(\S+) \[([^\r\n]+)\]").Last();
        var compiler = Path.Combine(sdk.Groups[2].Value, sdk.Groups[1].Value, "Roslyn", "bincore", "csc.dll");
        var source = Path.Combine(Root, "ExitHost.cs");
        File.WriteAllText(source, """
            using System;
            using System.IO;
            using System.Reflection;
            using System.Runtime.Loader;
            using System.Threading;
            class ExitHost {
                static int Main(string[] args) {
                    var directory = Path.GetDirectoryName(args[0]);
                    AssemblyLoadContext.Default.Resolving += (_, name) => {
                        var path = Path.Combine(directory, name.Name + ".dll");
                        return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
                    };
                    var method = Assembly.LoadFrom(args[0]).GetType("GlDrive.Services.UpdateChecker")
                        .GetMethod("TerminateUpdaterProcess", BindingFlags.Static | BindingFlags.NonPublic);
                    AppDomain.CurrentDomain.ProcessExit += (_, __) => File.WriteAllText(args[2], "ProcessExit ran");
                    new Thread(() => Thread.Sleep(Timeout.Infinite)) { IsBackground = false }.Start();
                    try { method.Invoke(null, new object[] { int.Parse(args[1]) }); }
                    finally { File.WriteAllText(args[2], "finally ran"); }
                    return 77;
                }
            }
            """);
        var response = Path.Combine(Root, "compile.rsp");
        var refPacks = Path.Combine(Directory.GetParent(sdk.Groups[2].Value)!.FullName,
            "packs", "Microsoft.NETCore.App.Ref");
        var refDirectory = Directory.GetDirectories(refPacks)
            .Select(path => Path.Combine(path, "ref", $"net{Environment.Version.Major}.0"))
            .Last(Directory.Exists);
        var references = Directory.GetFiles(refDirectory, "*.dll");
        File.WriteAllLines(response, new[] { "/nologo", "/nostdlib+", "/target:exe", $"/out:\"{Assembly}\"" }
            .Concat(references.Select(path => $"/reference:\"{path}\""))
            .Append($"\"{source}\""));
        var compilation = Run("dotnet", "exec", compiler, "@" + response).GetAwaiter().GetResult();
        Assert.True(compilation.Code == 0, compilation.Output);
        File.Copy(Path.ChangeExtension(typeof(UpdateChecker).Assembly.Location, ".runtimeconfig.json"),
            Path.ChangeExtension(Assembly, ".runtimeconfig.json"));
    }

    internal static async Task<(int Code, string Output)> Run(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        return (process.ExitCode, await stdout + await stderr);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
