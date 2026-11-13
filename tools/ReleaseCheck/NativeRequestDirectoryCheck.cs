using System.IO;
using System.Reflection;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Spread;

/// <summary>Real FTPS request-directory ownership boundary, isolated from production.</summary>
internal static class NativeRequestDirectoryCheck
{
    internal static async Task Run(FtpClientFactory factory, string fixtureRoot, string remoteRoot,
        CancellationToken ct)
    {
        const string serverId = "request-directory-fixture";
        const string release = "Fixture.Release.2026-GRP";
        var parent = remoteRoot + "/REQUEST-fixture";
        var destination = parent + "/" + release;
        var dataRoot = Path.GetFullPath(Path.Combine(fixtureRoot, "data"));
        var localParent = Path.GetFullPath(Path.Combine(dataRoot, parent.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        Check(localParent.StartsWith(dataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "native request fixture directory remains inside its disposable data root");
        var captureMarker = Path.Combine(fixtureRoot, "capture-request-directory-commands");
        var commandLog = Path.Combine(fixtureRoot, "request-directory-commands");
        string[] Commands() => File.Exists(commandLog) ? File.ReadAllLines(commandLog) : [];
        var hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var ensure = typeof(SpreadJob).GetMethod("EnsureDirectoryExists", hidden)!;
        var partial = typeof(SpreadJob).GetMethod("CapturePartialState", hidden)!;
        using var job = new SpreadJob("TEST", release, SpreadMode.Race, new SpreadConfig(),
            new(), new(), new(), new SpeedTracker(), new SkiplistEvaluator())
        {
            DestinationPaths = new Dictionary<string, string> { [serverId] = destination }
        };
        await using var pool = new FtpConnectionPool(factory, 1);
        await pool.Initialize(ct);
        AsyncFtpClient original;
        try
        {
            await using (var connection = await pool.Borrow(ct))
            {
                original = connection.Client;
                Directory.CreateDirectory(localParent);
                Check((await original.Execute("CWD " + parent, ct)).Success,
                    "native request parent exists at initial discovery");
                Check((await original.Execute("CWD /", ct)).Success,
                    "native request fixture leaves parent before server-side removal");
                Directory.Delete(localParent); // Empty, freshly created fixture directory only.
                File.WriteAllText(captureMarker, "enabled");
                FtpDirectoryRejectedException? rejected = null;
                try { await (Task)ensure.Invoke(job, [original, serverId, destination, "CD1/payload.bin", ct])!; }
                catch (FtpDirectoryRejectedException ex) { rejected = ex; }
                Check(rejected is { Code: "550", ControlChannelInSync: true },
                    "vanished request parent fails with its actual synchronized native FTP reply");
                Check(Commands().SequenceEqual(["CWD " + parent]) && !Directory.Exists(localParent),
                    "vanished request parent sends one CWD and no MKD; no ancestor is recreated");
                Check(((RacePartialState)partial.Invoke(job, null)!).Directories.Count == 0,
                    "missing request parent creates no owned release directory");
                Check(!connection.Poisoned && (await original.Execute("NOOP", ct)).Code == "200",
                    "clean native request-parent refusal preserves the control session");

                // Only the synthetic server/operator restores this server-owned parent.
                Directory.CreateDirectory(localParent);
                var beforeCreate = Commands().Length;
                await (Task)ensure.Invoke(job, [original, serverId, destination, "CD1/payload.bin", ct])!;
                var created = Commands().Skip(beforeCreate).Where(line => line.StartsWith("MKD ")).ToArray();
                Check(created.SequenceEqual(["MKD " + destination, "MKD " + destination + "/CD1"])
                    && Directory.Exists(Path.Combine(localParent, release, "CD1")),
                    "existing request parent permits only release and nested directory creation over native FTPS");
                var ownership = ((RacePartialState)partial.Invoke(job, null)!).Directories;
                Check(ownership.Count == 1 && ownership[0].ServerId == serverId && ownership[0].RemotePath == destination,
                    "request creation records only the release root as owned, never its request ancestor");
                var beforeRepeat = Commands().Length;
                await (Task)ensure.Invoke(job, [original, serverId, destination, "CD1/payload.bin", ct])!;
                Check(!Commands().Skip(beforeRepeat).Any(line => line.StartsWith("MKD ")),
                    "existing request release and nested directory are reused without another MKD");
            }
            await using (var reused = await pool.Borrow(ct))
            {
                Check(ReferenceEquals(original, reused.Client) && !reused.Poisoned
                    && (await reused.Client.Execute("NOOP", ct)).Code == "200",
                    "request-parent failure and recovery reborrow the same native session");
            }
            Check(pool.TotalCreated == 1 && pool.ActiveCount == 0 && pool.QuarantineSize == 0,
                "native request boundary checks leak no lease and require no replacement login");
        }
        finally { File.Delete(captureMarker); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: request-directory: " + message);
        Console.WriteLine("PASS: request-directory: " + message);
    }
}
