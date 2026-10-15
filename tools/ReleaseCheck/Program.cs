using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using Fsp;
using GlDrive.Config;
using GlDrive.Filesystem;
using GlDrive.Ftp;
using GlDrive.Tls;
using GlDrive.Player;
using GlDrive.Services;
using GlDrive.Downloads;
using GlDrive.Downloads.Segmented;
using GlDrive.Spread;
using Serilog;
using Microsoft.Win32.SafeHandles;

if (args.Length > 0 && args[0] == "--sharing-only")
{
    await CpsvSharingCheck.Run(args.Length > 1 ? args[1] : throw new ArgumentException("Supply an evidence directory"));
    return;
}
if (args.Length > 0 && args[0] == "--network-only")
{
    if (args.Length != 4) throw new ArgumentException("Supply an evidence directory and two local adapter names");
    await CpsvSharingCheck.RunMultiNetwork(args[1], args[2], args[3]);
    return;
}

// This harness only connects to the disposable loopback fixture, with an isolated trust store.
var state = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement;
// Debug log beside the state file: a failed step must show what the app did, not just that it failed.
Log.Logger = new LoggerConfiguration().MinimumLevel.Debug()
    .WriteTo.File(Path.ChangeExtension(Path.GetFullPath(args[0]), ".app.log")).CreateLogger();
var port = state.GetProperty("port").GetInt32();
var root = state.GetProperty("root").GetString()!;
var fingerprint = state.GetProperty("fingerprint").GetString()!;
var config = new ServerConfig { Name = "Release verification", Connection = new() { Host = "127.0.0.1", Port = port, Username = "release-check" } };
var certs = new CertificateManager(Path.Combine(root, "trusted.json"));
certs.TrustCertificate($"127.0.0.1:{port}", fingerprint);
certs.CertificatePrompt += (_, _) => Task.FromResult(false);
var factory = new FtpClientFactory(config, certs);
// Exercise the same certificate callback used by Settings > Test Connection.
// A changed pin must stop the handshake before any authentication command.
var rejectedPins = new CertificateManager(Path.Combine(root, "rejected-pins.json"));
rejectedPins.TrustCertificate($"127.0.0.1:{port}", new string('0', 64));
var authenticationLog = Path.Combine(root, "authentication-attempts");
var authenticationsBefore = File.Exists(authenticationLog) ? File.ReadAllLines(authenticationLog).Length : 0;
var probe = factory.Create();
try
{
    typeof(GlDrive.UI.ServerEditDialog).GetMethod("AttachTofuValidation", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [probe, "127.0.0.1", port, rejectedPins]);
    var rejected = false;
    using var certificateDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try { await probe.Connect(certificateDeadline.Token); }
    catch (Exception) when (!certificateDeadline.IsCancellationRequested) { rejected = true; }
    Check(rejected, "connection test rejects a changed certificate pin");
    Check((File.Exists(authenticationLog) ? File.ReadAllLines(authenticationLog).Length : 0) == authenticationsBefore,
        "rejected certificate prevents FTP authentication");
    Check(rejectedPins.GetTrustedCertificates()[$"127.0.0.1:{port}"].Fingerprint == new string('0', 64),
        "connection test preserves the existing certificate pin");
}
finally
{
    var detached = (IDisposable?)typeof(FtpConnectionPool)
        .GetMethod("NeutralizeGnuTls", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [probe]);
    try { probe.Dispose(); }
    finally { detached?.Dispose(); }
}
await using var pool = new FtpConnectionPool(factory, 2);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(240));
await pool.Initialize(deadline.Token);
var ftp = new FtpOperations(pool);
var remoteRoot = "/check-" + Guid.NewGuid().ToString("N");
await ftp.CreateDirectory(remoteRoot, deadline.Token);
if (args.Contains("--search-only", StringComparer.Ordinal))
{
    await CheckSearchRecovery(factory, root, remoteRoot, deadline.Token);
    Console.WriteLine("PASS: local native FTPS search verification complete");
    return;
}
await CheckSourceRelocation(factory, root, remoteRoot, deadline.Token);
if (args.Contains("--relocation-only", StringComparer.Ordinal))
{
    Console.WriteLine("PASS: local native FTPS source-relocation verification complete");
    return;
}
await CheckManualEnumerationAndDownloads(factory, root, remoteRoot, deadline.Token);
await CheckIncompleteDownloadRecovery(factory, root, remoteRoot, deadline.Token);
await CheckIncompleteRaceRecovery(factory, root, remoteRoot, deadline.Token);
if (args.Contains("--downloads-only", StringComparer.Ordinal))
{
    Console.WriteLine("PASS: local native FTPS enumeration and download verification complete");
    return;
}
await CheckSearchRecovery(factory, root, remoteRoot, deadline.Token);
// Stats must not turn an unreachable data endpoint into a six-hour ACL denial.
// Wire only the pool/state: no mount, downloads, timers or production configuration.
using (var statsService = new MountService(config, new DownloadConfig(), certs))
{
    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
    typeof(MountService).GetField("_pool", flags)!.SetValue(statsService, pool);
    typeof(MountService).GetField("_ftp", flags)!.SetValue(statsService, ftp);
    typeof(MountService).GetProperty(nameof(MountService.CurrentState))!.SetValue(statsService, MountState.Connected);
    File.WriteAllText(Path.Combine(root, "reject-passive"), "1");
    try { await statsService.RefreshStatsAsync(); }
    finally { File.Delete(Path.Combine(root, "reject-passive")); }
    var unavailableUntil = (DateTime)typeof(MountService).GetField("_statsUnavailableUntil", flags)!.GetValue(statsService)!;
    Check(unavailableUntil <= DateTime.UtcNow, "stats data-channel refusal does not cache a six-hour access denial");
    File.WriteAllText(Path.Combine(root, "stats-trailer"), "1");
    try
    {
        await statsService.RefreshStatsAsync();
        Check(statsService.Stats is { Credits: "12.3GB", Ratio: "1:3" },
            "stats retry immediately recovers credits and ratio from the LIST trailer");
    }
    finally { File.Delete(Path.Combine(root, "stats-trailer")); }
    await statsService.RefreshStatsAsync(); // invalidate the formerly working trailer
    await statsService.RefreshStatsAsync(); // all commands and LIST now answer cleanly without stats
    unavailableUntil = (DateTime)typeof(MountService).GetField("_statsUnavailableUntil", flags)!.GetValue(statsService)!;
    Check(unavailableUntil > DateTime.UtcNow && statsService.Stats is { Credits: null, Ratio: null },
        "clean command and trailer misses still use the bounded no-access cache");
}
// Cross FluentFTP's default TLS transaction limit on the SAME borrowed session.
// SelfConnectMode.Never with the default SslSessionLength rejects a healthy
// connection here, before the command ever reaches the fixture.
await using (var connection = await pool.Borrow(deadline.Token))
{
    var client = connection.Client;
    var connectCount = client.Status.ConnectCount;
    for (var i = 0; i < 800; i++)
    {
        var reply = await client.Execute("NOOP", deadline.Token);
        if (reply.Code != "200") throw new Exception($"NOOP {i} failed: {reply.Code}");
    }
    Check(client.Status.ConnectCount == connectCount,
        "800 native FTPS commands survive the TLS transaction limit without re-login");
}
// The sole upload-capable site already holds the release; the other site is
// download-only and does not have it. Exercise real discovery and terminal state.
const string existingRelease = "Already.Present.S01E01.1080p-GRP";
var receiver = new ServerConfig { Id = "receiver", Name = "Receiver" };
receiver.SpreadSite.Sections["TV"] = remoteRoot + "/receiver";
var leech = new ServerConfig { Id = "leech", Name = "Download only" };
leech.SpreadSite.Sections["TV"] = remoteRoot + "/leech";
leech.SpreadSite.DownloadOnly = true;
await ftp.CreateDirectory(receiver.SpreadSite.Sections["TV"] + "/" + existingRelease, deadline.Token);
await ftp.UploadFile(receiver.SpreadSite.Sections["TV"] + "/" + existingRelease + "/payload.bin", [1, 2, 3], deadline.Token);
await ftp.UploadFile(receiver.SpreadSite.Sections["TV"] + "/" + existingRelease + "/release.sfv",
    System.Text.Encoding.UTF8.GetBytes($"payload.bin {System.IO.Hashing.Crc32.HashToUInt32(new byte[] { 1, 2, 3 }):X8}\n"), deadline.Token);
await ftp.CreateDirectory(leech.SpreadSite.Sections["TV"], deadline.Token);
using (var job = new SpreadJob("TV", existingRelease, SpreadMode.Race, new SpreadConfig(),
    new() { [receiver.Id] = pool, [leech.Id] = pool }, new(),
    new() { [receiver.Id] = receiver, [leech.Id] = leech }, new SpeedTracker(), new SkiplistEvaluator()))
{
    var errors = 0;
    job.Error += (_, _) => errors++;
    await job.RunAsync().WaitAsync(deadline.Token);
    Check(job.State == SpreadJobState.Completed && job.LastError == null && errors == 0,
        "native race discovery completes without failure when the sole receiver already has the release");
    Check(!Directory.Exists(Path.Combine(root, "data", leech.SpreadSite.Sections["TV"].TrimStart('/'), existingRelease))
        && job.ActiveTransferList.Count == 0,
        "no transfer is attempted to a download-only peer");
}
var searchRoot = remoteRoot + "/search";
await ftp.CreateDirectory(searchRoot + "/[Z] - ( 1591M 17F - COMPLETE ) - [Z]", deadline.Token);
await ftp.CreateDirectory(searchRoot + "/[NUKED]-Show.S01E02.1080p-GRP", deadline.Token);
await ftp.CreateDirectory(searchRoot + "/Show.S01.COMPLETE.1080p-GRP", deadline.Token);
await ftp.CreateDirectory(searchRoot + "/[Group] Show.S01", deadline.Token);
await ftp.CreateDirectory(searchRoot + "/Nuked.2026.1080p-GRP", deadline.Token);
foreach (var method in new[] { SearchMethod.CachedIndex, SearchMethod.LiveCrawl })
{
    using var search = new FtpSearchService(pool, new SearchConfig
    {
        Method = method, SearchPaths = [searchRoot], MaxDepth = 2
    });
    if (method == SearchMethod.CachedIndex) await search.RefreshIndex(ct: deadline.Token);
    var results = await search.Search("", ct: deadline.Token);
    Check(results.Count == 3 && results.Any(r => r.ReleaseName == "Show.S01.COMPLETE.1080p-GRP") &&
        results.Any(r => r.ReleaseName == "[Group] Show.S01") &&
        results.Any(r => r.ReleaseName == "Nuked.2026.1080p-GRP"),
        $"native {method} excludes status and explicit nuke directories and preserves real release names");
}
// A reused session can have a non-root CWD. The configured "/" must still
// list the filesystem root, for both cached and live search.
await using (var rootSearchPool = new FtpConnectionPool(factory, 1))
{
    await rootSearchPool.Initialize(deadline.Token);
    foreach (var method in new[] { SearchMethod.CachedIndex, SearchMethod.LiveCrawl })
    {
        await using (var connection = await rootSearchPool.Borrow(deadline.Token))
            await connection.Client.SetWorkingDirectory(searchRoot, deadline.Token);
        using var search = new FtpSearchService(rootSearchPool, new SearchConfig
        {
            Method = method, SearchPaths = ["/"], MaxDepth = 0
        });
        if (method == SearchMethod.CachedIndex) await search.RefreshIndex(ct: deadline.Token);
        var results = await search.Search(remoteRoot.TrimStart('/'), ct: deadline.Token);
        Check(results.Any(r => r.RemotePath == remoteRoot),
            $"native {method} searches configured filesystem root despite non-root session CWD");
    }
}
var notificationRoot = remoteRoot + "/notifications";
await ftp.CreateDirectory(notificationRoot + "/TV/existing", deadline.Token);
var releaseMonitor = new NewReleaseMonitor(pool,
    new NotificationConfig { WatchPath = notificationRoot }, () => MountState.Connected);
var pollCycle = typeof(NewReleaseMonitor).GetMethod("PollCycle", BindingFlags.Instance | BindingFlags.NonPublic)!;
var notifications = new List<string>();
releaseMonitor.NewReleaseDetected += (_, _, path) => notifications.Add(path);
await (Task)pollCycle.Invoke(releaseMonitor, [deadline.Token])!;
Check(notifications.Count == 0, "native notification poll seeds existing releases");
await ftp.CreateDirectory(notificationRoot + "/TV/new", deadline.Token);
await (Task)pollCycle.Invoke(releaseMonitor, [deadline.Token])!;
Check(notifications.SequenceEqual([notificationRoot + "/TV/new"]), "native notification poll detects new release");
await (Task)pollCycle.Invoke(releaseMonitor, [deadline.Token])!;
Check(notifications.Count == 1, "native notification poll does not duplicate releases");
var expected = new byte[2 * 1024 * 1024];
new Random(42).NextBytes(expected);
await ftp.UploadFile(remoteRoot + "/original.bin", expected, deadline.Token);
using (var downloaded = new MemoryStream())
{
    await ftp.DownloadToStream(remoteRoot + "/original.bin", downloaded, deadline.Token);
    Check(downloaded.ToArray().SequenceEqual(expected), "native FTPS upload/download integrity");
}
var mediaStream = typeof(MediaStreamServer).GetMethod("StreamStandard", BindingFlags.NonPublic | BindingFlags.Static)!;
await using (var connection = await pool.Borrow(deadline.Token))
{
    connection.Poisoned = true;
    using var media = new MemoryStream();
    await (Task)mediaStream.Invoke(null, [connection.Client, remoteRoot + "/original.bin", 0L, (long?)expected.Length,
        media, deadline.Token, null, true])!;
    connection.Poisoned = false;
    Check(media.ToArray().SequenceEqual(expected), "media transfer bytes and FTP completion reply");
}
await using (var connection = await pool.Borrow(deadline.Token))
{
    connection.Poisoned = true; // Intentional early range completion must discard the control session.
    using var media = new MemoryStream();
    await ((Task)mediaStream.Invoke(null, [connection.Client, remoteRoot + "/original.bin", 7L, (long?)4,
        media, deadline.Token, null, false])!).WaitAsync(TimeSpan.FromSeconds(5));
    Check(media.ToArray().SequenceEqual(expected.Skip(7).Take(4)), "bounded media range without control-reply stall");
}

var cache = new DirectoryCache();
var fs = new GlDriveFileSystem(ftp, cache, remoteRoot, "ReleaseCheck", readBufferSpillThresholdMb: 1);
Check(fs.Open("\\original.bin", 0, 0, out var node, out var desc, out _, out _) == 0, "open through FTP listing");
Check(fs.Open("\\original.bin", 0, 0, out var second, out _, out _, out _) == 0 && ReferenceEquals(node, second), "shared handles");
var pointer = Marshal.AllocHGlobal(4);
try
{
    Marshal.Copy(new byte[] { 1, 2, 3, 4 }, 0, pointer, 4);
    Check(fs.Write(node, desc, pointer, 7, 4, false, false, out var written, out _) == 0 && written == 4, "partial write without prior read");
    Array.Copy(new byte[] { 1, 2, 3, 4 }, 0, expected, 7, 4);
    Check(((FileNode)node).WriteBufferFile != null, "large write spills to disk");
    Check(fs.Rename(node, desc, "\\original.bin", "\\renamed.bin", false) == 0, "rename with open dirty handles");
    Check(fs.Flush(null!, null!, out _) == 0, "volume flush");
    using var actual = new MemoryStream();
    await ftp.DownloadToStream(remoteRoot + "/renamed.bin", actual, deadline.Token);
    Check(actual.ToArray().SequenceEqual(expected), "flushed bytes survive remote rename");
    Check(!await ftp.FileExists(remoteRoot + "/original.bin", deadline.Token), "old remote path remains absent");
}
finally { Marshal.FreeHGlobal(pointer); ((FileNode)node).IsDirty = false; fs.Close(node, desc); fs.Close(second, second); }

// Exercise the installed WinFsp driver using a currently unused drive letter.
var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
var letter = "ZYXWVUTSRQPONMLKJIH".First(c => !used.Contains(c));
using var host = new FileSystemHost(fs) { Prefix = @"\GlDriveReleaseCheck\" + Guid.NewGuid().ToString("N") };
var status = host.Mount(letter + ":", null, false, 0);
Check(status >= 0, $"WinFsp mount ({status:X8})");
try
{
    var path = letter + @":\driver.bin";
    File.WriteAllBytes(path, expected);
    using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
    {
        file.Position = 10;
        file.WriteByte(99);
        expected[10] = 99;
        file.Flush(true);
    }
    Check(File.ReadAllBytes(path).SequenceEqual(expected), "WinFsp create/write/flush/read");
    var renamed = letter + @":\driver-renamed.bin";
    File.Move(path, renamed);
    Check(File.ReadAllBytes(renamed).SequenceEqual(expected), "WinFsp rename/read");
    File.Delete(renamed);
    Check(!File.Exists(renamed), "WinFsp delete");

    var populated = letter + @":\nonrecursive-delete";
    Directory.CreateDirectory(populated);
    var retained = Path.Combine(populated, "keep.txt");
    File.WriteAllText(retained, "must survive a nonrecursive directory delete");
    var deleteRejected = false;
    try { Directory.Delete(populated, recursive: false); }
    catch (IOException) { deleteRejected = true; }
    Check(deleteRejected && File.ReadAllText(retained) == "must survive a nonrecursive directory delete",
        "WinFsp nonrecursive delete rejects a populated directory and preserves its contents");
    File.Delete(retained);
    Directory.Delete(populated, recursive: false);
    Check(!Directory.Exists(populated), "WinFsp nonrecursive delete removes an empty directory");

    // The directory can gain a child between CanDelete and Cleanup. Cleanup must
    // issue nonrecursive RMD even after the emptiness check has already succeeded.
    await ftp.CreateDirectory(remoteRoot + "/delete-race", deadline.Token);
    cache.Clear();
    Check(fs.Open("\\delete-race", 0, 0, out var deleteNode, out var deleteDesc, out _, out _) == 0,
        "open empty directory for delete race");
    try
    {
        Check(fs.CanDelete(deleteNode, deleteDesc, "\\delete-race") == 0, "empty directory passes delete preflight");
        await ftp.UploadFile(remoteRoot + "/delete-race/late.txt", "late arrival"u8.ToArray(), deadline.Token);
        fs.Cleanup(deleteNode, deleteDesc, "\\delete-race", 1);
        Check(await ftp.FileExists(remoteRoot + "/delete-race/late.txt", deadline.Token),
            "cleanup preserves files arriving after delete preflight");
    }
    finally { fs.Close(deleteNode, deleteDesc); }

    // Keep the directory HANDLE open across real mounted mutations. Ordinary
    // Directory.GetFiles creates a new handle and cannot detect a stale snapshot
    // retained by an existing Explorer directory handle.
    var enumerationPath = letter + @":\enumeration-restart";
    Directory.CreateDirectory(enumerationPath);
    var stableNames = Enumerable.Range(0, 5).Select(i => $"stable-{i}.txt").ToArray();
    foreach (var name in stableNames) File.WriteAllText(Path.Combine(enumerationPath, name), name);
    File.WriteAllText(Path.Combine(enumerationPath, "before.txt"), "before");
    using var directoryHandle = NativeDirectory.Open(enumerationPath);
    var baseline = NativeDirectory.Enumerate(directoryHandle);
    Check(baseline.Names.SetEquals(stableNames.Append("before.txt")) && baseline.Pages > 1,
        "WinFsp retained directory handle enumerates all continuation pages");

    File.Move(Path.Combine(enumerationPath, "before.txt"), Path.Combine(enumerationPath, "renamed.txt"));
    // Let the driver's configured one-second metadata cache expire so the
    // restart reaches GlDrive's enumeration callback on this same handle.
    await Task.Delay(1100, deadline.Token);
    var afterRename = NativeDirectory.Enumerate(directoryHandle);
    Check(afterRename.Names.SetEquals(stableNames.Append("renamed.txt")),
        "WinFsp same-handle enumeration restart reflects mounted rename");

    File.Delete(Path.Combine(enumerationPath, "renamed.txt"));
    File.WriteAllText(Path.Combine(enumerationPath, "created.txt"), "created");
    await Task.Delay(1100, deadline.Token);
    var afterDeleteCreate = NativeDirectory.Enumerate(directoryHandle);
    Check(afterDeleteCreate.Names.SetEquals(stableNames.Append("created.txt")),
        "WinFsp same-handle enumeration restart reflects mounted delete and create");
}
finally { host.Unmount(); }

// A real FTP rejection is returned normally by FluentFTP.Execute, not thrown.
// Exercise the monitor loop and recovery against the native FTPS fixture.
var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var lossCount = 0;
var monitor = new ConnectionMonitor(pool, factory, new PoolConfig
{
    KeepaliveIntervalSeconds = 1,
    ReconnectInitialDelaySeconds = 1,
    ReconnectMaxDelaySeconds = 2
});
monitor.ConnectionLost += () => { Interlocked.Increment(ref lossCount); lost.TrySetResult(); };
monitor.ConnectionRestored += () => restored.TrySetResult();
File.WriteAllText(Path.Combine(root, "noop-replies"), "");
File.WriteAllText(Path.Combine(root, "reject-noop-once"), "2");
monitor.Start();
try
{
    await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(lossCount == 1, "monitor rejects a real negative NOOP reply");
    await restored.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(File.ReadAllLines(Path.Combine(root, "noop-replies")).SequenceEqual(new[] { "500", "500", "200" }),
        "monitor waits for a positive NOOP after a rejected reconnect probe");
}
finally { await monitor.StopAsync(); }
Check(lossCount == 1, "monitor shutdown does not report a false connection loss");
Console.WriteLine("PASS: local native FTPS and WinFsp verification complete");

static void Check(bool success, string step)
{
    if (!success) throw new InvalidOperationException("FAIL: " + step);
    Console.WriteLine("PASS: " + step);
}

static async Task CheckManualEnumerationAndDownloads(FtpClientFactory factory, string fixtureRoot,
    string remoteRoot, CancellationToken ct)
{
    // Each invocation uses fresh destinations, even when reusing the server.
    // A completed retry from a prior run would otherwise bypass the failure probe.
    var localRoot = Path.Combine(fixtureRoot, Path.GetFileName(remoteRoot));
    Directory.CreateDirectory(localRoot);
    await using var singlePool = new FtpConnectionPool(factory, 1);
    await singlePool.Initialize(ct);
    var ftp = new FtpOperations(singlePool);
    var tree = remoteRoot + "/manual-folder";
    await ftp.CreateDirectory(tree + "/CD1/Deep", ct);
    await ftp.CreateDirectory(tree + "/CD2", ct);
    await ftp.CreateDirectory(tree + "/Empty", ct);
    var payloads = new Dictionary<string, byte[]>
    {
        ["release.nfo"] = "release info"u8.ToArray(),
        ["CD1/volume.bin"] = Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray(),
        ["CD2/volume.bin"] = Enumerable.Range(0, 5000).Select(i => (byte)(255 - i)).ToArray(),
        ["CD1/Deep/sample.bin"] = [7, 8, 9]
    };
    foreach (var (relative, bytes) in payloads) await ftp.UploadFile(tree + "/" + relative, bytes, ct);
    Check(!await ftp.DirectoryExists(tree + "/release.nfo", ct)
        && await ftp.FileExists(tree + "/release.nfo", ct),
        "native FTP existence probes distinguish a file from a directory");

    FluentFTP.AsyncFtpClient original;
    await using (var connection = await singlePool.Borrow(ct)) original = connection.Client;
    var files = new List<(string relative, string full)>();
    Func<string, CancellationToken, Task<FluentFTP.FtpListItem[]>> list = ftp.ListDirectory;
    var enumerate = typeof(SpreadManager).GetMethod("EnumerateDirectoryRecursive", BindingFlags.Static | BindingFlags.NonPublic)!;
    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
    {
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await ((Task)enumerate.Invoke(null, [list, tree, tree, files, 0, timeout.Token])!).WaitAsync(timeout.Token);
    }
    Check(files.Select(f => f.relative).ToHashSet().SetEquals(payloads.Keys),
        "manual FXP enumeration traverses nested folders with one pool connection");
    await using (var connection = await singlePool.Borrow(ct))
        Check(ReferenceEquals(original, connection.Client) && singlePool.TotalCreated == 1,
            "manual FXP enumeration returns and reuses the original listing connection");

    // Keep the app-wide ledger isolated as well as certificate trust. This process
    // runs the real StreamingDownloader without opening the user's network ledger.
    typeof(DownloadNetworkServices).GetField("_usage", BindingFlags.Static | BindingFlags.NonPublic)!
        .SetValue(null, new NetworkUsageStore(Path.Combine(localRoot, "network-usage.json")));
    var downloader = new StreamingDownloader(singlePool, "release-check", new DownloadConfig(), 0, null);
    var local = Path.Combine(localRoot, "download-tree");
    var planner = typeof(DownloadManager).Assembly.GetType("GlDrive.Downloads.DownloadTree")!
        .GetMethod("BuildAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
    Func<string, CancellationToken, Task<bool>> directoryExists = ftp.DirectoryExists;
    Func<string, CancellationToken, Task<bool>> fileExists = ftp.FileExists;
    var planning = (Task)planner.Invoke(null, [tree, local, directoryExists, fileExists, list, ct])!;
    await planning;
    var plan = planning.GetType().GetProperty("Result")!.GetValue(planning)!;
    var plannedFiles = (IReadOnlyList<FtpDownloadFile>)plan.GetType().GetProperty("Files")!.GetValue(plan)!;
    var directories = (IReadOnlyList<string>)plan.GetType().GetProperty("Directories")!.GetValue(plan)!;
    foreach (var directory in directories) Directory.CreateDirectory(directory);
    await downloader.DownloadFiles(plannedFiles, null, ct);
    Check(plannedFiles.Count == payloads.Count && payloads.All(p =>
        File.ReadAllBytes(Path.Combine(local, p.Key)).SequenceEqual(p.Value))
        && Directory.Exists(Path.Combine(local, "Empty")),
        "recursive download preserves nested duplicate basenames, empty folders and every payload byte");

    foreach (var shortData in new[] { true, false })
    {
        var name = shortData ? "short.bin" : "rejected.bin";
        var remote = remoteRoot + "/" + name;
        var payload = shortData ? new byte[10] : new byte[1000];
        await ftp.UploadFile(remote, payload, ct);
        var destination = Path.Combine(localRoot, "failed-downloads", name);
        var marker = Path.Combine(fixtureRoot, shortData ? "short-retr-size" : "reject-retr-completion");
        File.WriteAllText(marker, "1");
        IOException? failure = null;
        try
        {
            // -1 forces the real SIZE request, including the fixture's deliberately
            // overstated length. Three native attempts must still fail the job.
            await downloader.DownloadFiles([new FtpDownloadFile(remote, destination, -1)], null, ct);
        }
        catch (IOException ex) { failure = ex; }
        finally { File.Delete(marker); }
        Check(failure != null && (shortData ? failure.Message.Contains("Premature") : failure.Message.Contains("426")),
            shortData ? "standard segmented download rejects real early FTP EOF"
                      : "standard segmented download rejects a real FTP 426 completion");
        Check(!File.Exists(destination) && File.Exists(destination + StagedFile.StagingSuffix),
            $"{name}: failed native download retains staging without publishing a final file");

        // Restore a valid server response (and the expected length), then retry
        // using the same downloader. The saved partial must recover successfully.
        if (shortData) await ftp.UploadFile(remote, new byte[1000], ct);
        await downloader.DownloadFiles([new FtpDownloadFile(remote, destination, -1)], null, ct);
        Check(File.ReadAllBytes(destination).SequenceEqual(new byte[1000])
            && !File.Exists(destination + StagedFile.StagingSuffix),
            $"{name}: native retry publishes only after complete data and a successful reply");
    }
}

static async Task CheckSearchRecovery(FtpClientFactory factory, string fixtureRoot,
    string remoteRoot, CancellationToken ct)
{
    await using var searchPool = new FtpConnectionPool(factory, 1);
    await searchPool.Initialize(ct);
    var searchRoot = remoteRoot + "/search-recovery";
    var localRoot = Path.Combine(fixtureRoot, "data", searchRoot.TrimStart('/'));
    const string wanted = "Wanted.Show.S01E01.1080p-GRP";
    Directory.CreateDirectory(Path.Combine(localRoot, wanted));
    Directory.CreateDirectory(Path.Combine(localRoot, "Other.Show.S01E01.720p-GRP"));
    var responsePath = Path.Combine(fixtureRoot, "site-search-response");
    var denialPath = Path.Combine(fixtureRoot, "reject-search-path");
    try
    {
        foreach (var method in new[] { SearchMethod.CachedIndex, SearchMethod.LiveCrawl })
        {
            using var search = new FtpSearchService(searchPool, new SearchConfig
            {
                Method = method, SearchPaths = [searchRoot], MaxDepth = 1
            });
            if (method == SearchMethod.CachedIndex) await search.RefreshIndex(ct: ct);
            var result = await search.SearchDetailed("  1080p   Wanted  ", ct: ct);
            Check(result.Results.Count == 1 && result.Results[0].ReleaseName == wanted && !result.IsPartial,
                $"native {method} matches all words across release punctuation in any order");
        }

        using (var search = new FtpSearchService(searchPool, new SearchConfig
        {
            Method = SearchMethod.Auto, SearchPaths = [searchRoot], MaxDepth = 1
        }))
        {
            await search.RefreshIndex(ct: ct);
            await using (var held = await searchPool.Borrow(ct))
            {
                var cached = await search.SearchDetailed("Wanted 1080p", ct: ct)
                    .WaitAsync(TimeSpan.FromSeconds(3), ct);
                Check(cached.Results.Count == 1, "native Auto returns cached matches while the only connection is busy");
            }
            // A nonempty old index and a successful but unfamiliar SITE reply
            // must both allow live fallback for a release that arrived afterwards.
            const string late = "Arrived.Later.S01E02.2160p-GRP";
            Directory.CreateDirectory(Path.Combine(localRoot, late));
            File.WriteAllText(responsePath, "Results are available in an unfamiliar server format");
            var arrived = await search.SearchDetailed("Later 2160p", ct: ct);
            Check(arrived.Results.Count == 1 && arrived.Results[0].ReleaseName == late,
                "native Auto finds a new release beyond an old cache and an unrecognized SITE reply");
        }

        File.WriteAllText(responsePath, "No matches");
        using (var search = new FtpSearchService(searchPool, new SearchConfig
        {
            Method = SearchMethod.SiteSearch, SearchPaths = [searchRoot], MaxDepth = 1
        }))
        {
            var fallback = await search.SearchDetailed("Wanted 1080p", ct: ct);
            Check(fallback.Results.Any(r => r.ReleaseName == wanted),
                "native empty SITE SEARCH falls back to a live directory search");
        }

        var deniedRoot = searchRoot + "/denied";
        Directory.CreateDirectory(Path.Combine(localRoot, "denied"));
        File.WriteAllText(denialPath, deniedRoot);
        FluentFTP.AsyncFtpClient originalListingClient;
        await using (var connection = await searchPool.Borrow(ct)) originalListingClient = connection.Client;
        var deniedListingRejected = false;
        try { await new FtpOperations(searchPool).ListDirectory(deniedRoot, ct); }
        catch (DataCommandRejectedException ex) when (ex.ControlChannelInSync) { deniedListingRejected = true; }
        Check(deniedListingRejected, "native FTP listing rejection cannot appear as an empty download directory");
        using (var search = new FtpSearchService(searchPool, new SearchConfig
        {
            Method = SearchMethod.LiveCrawl, SearchPaths = [deniedRoot, searchRoot], MaxDepth = 0
        }))
        {
            var partial = await search.SearchDetailed("Wanted 1080p", ct: ct);
            Check(partial.IsPartial && partial.Results.Any(r => r.ReleaseName == wanted),
                "native denied search roots retain healthy results and report incomplete coverage");
        }
        await using (var connection = await searchPool.Borrow(ct))
            Check(ReferenceEquals(originalListingClient, connection.Client),
                "clean listing denial preserves the healthy native FTP session");
        File.Delete(denialPath);

        // Reproduce the production condition: one connection held by a transfer.
        // Release it after the first borrow deadline so the bounded retry must recover.
        using (var search = new FtpSearchService(searchPool, new SearchConfig
        {
            Method = SearchMethod.LiveCrawl, SearchPaths = [searchRoot], MaxDepth = 0
        }))
        {
            var held = await searchPool.Borrow(ct);
            Task<SearchOutcome>? pending = null;
            try
            {
                pending = search.SearchDetailed("Wanted 1080p", ct: ct);
                await Task.Delay(TimeSpan.FromSeconds(17), ct);
            }
            finally { await held.DisposeAsync(); }
            var recovered = await pending!.WaitAsync(TimeSpan.FromSeconds(20), ct);
            Check(recovered.Results.Any(r => r.ReleaseName == wanted) && !recovered.IsPartial,
                "native live search retries a busy connection and succeeds when the slot becomes available");
        }
    }
    finally
    {
        File.Delete(responsePath);
        File.Delete(denialPath);
    }
}

static async Task CheckIncompleteDownloadRecovery(FtpClientFactory factory, string fixtureRoot,
    string remoteRoot, CancellationToken ct)
{
    await using var waitingPool = new FtpConnectionPool(factory, 1);
    await waitingPool.Initialize(ct);
    var ftp = new FtpOperations(waitingPool);
    var release = remoteRoot + "/Arriving.Release-GRP";
    var source = Path.Combine(fixtureRoot, "data", release.TrimStart('/'));
    Directory.CreateDirectory(source);
    var first = new byte[8192];
    var second = new byte[16384];
    new Random(3112).NextBytes(first);
    new Random(3113).NextBytes(second);
    File.WriteAllBytes(Path.Combine(source, "first.bin"), first);
    File.WriteAllText(Path.Combine(source, "second.bin-missing"), "");
    File.WriteAllText(Path.Combine(source, "release.sfv"),
        $"first.bin {System.IO.Hashing.Crc32.HashToUInt32(first):X8}\n" +
        $"second.bin {System.IO.Hashing.Crc32.HashToUInt32(second):X8}\n");
    var localRoot = Path.Combine(fixtureRoot, "waiting-download-check", remoteRoot.TrimStart('/'));
    Directory.CreateDirectory(localRoot);
    var storePath = Path.Combine(localRoot, "queue.json");
    var destination = Path.Combine(localRoot, "release");
    // A valid part retained from an earlier run must survive waiting and restart.
    Directory.CreateDirectory(destination);
    File.WriteAllBytes(Path.Combine(destination, "first.bin"), first);
    var downloadConfig = new DownloadConfig
    {
        MaxConcurrentDownloads = 1, MaxRetries = 0, AutoExtract = false,
        VerifySfv = true, SkipIncompleteReleases = false
    };
    var downloader = new StreamingDownloader(waitingPool, "waiting-fixture", downloadConfig, 0, null);
    DownloadStore NewStore()
    {
        var store = new DownloadStore("isolated-waiting-fixture");
        typeof(DownloadStore).GetField("_filePath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, storePath);
        return store;
    }
    var item = new DownloadItem
    {
        RemotePath = release, LocalPath = destination, ReleaseName = "Arriving.Release-GRP",
        ServerId = "waiting-fixture"
    };
    var firstStore = NewStore();
    using (var manager = new DownloadManager(firstStore, ftp, downloader, downloadConfig))
    {
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentinelDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.DownloadStatusChanged += changed =>
        {
            if (changed.Id == item.Id && changed.Status == DownloadStatus.Queued &&
                changed.ErrorMessage?.StartsWith("Waiting for release completion", StringComparison.Ordinal) == true)
                parked.TrySetResult();
            if (changed.ReleaseName == "sentinel" && changed.Status == DownloadStatus.Completed)
                sentinelDone.TrySetResult();
        };
        firstStore.Add(item);
        manager.Start();
        try
        {
            await parked.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            Check(item.Status == DownloadStatus.Queued && item.RetryCount == 0 && item.CompletedAt == null,
                "incomplete native FTPS download parks without exhausting a zero retry budget");
            var sentinelPath = remoteRoot + "/waiting-sentinel.bin";
            File.WriteAllBytes(Path.Combine(fixtureRoot, "data", sentinelPath.TrimStart('/')), [1, 2, 3]);
            Check(manager.Enqueue(new DownloadItem
            {
                ReleaseName = "sentinel", RemotePath = sentinelPath,
                LocalPath = Path.Combine(localRoot, "sentinel.bin")
            }), "a waiting source allows another download to enter the queue");
            await sentinelDone.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            Check(File.ReadAllBytes(Path.Combine(localRoot, "sentinel.bin")).SequenceEqual(new byte[] { 1, 2, 3 }),
                "waiting releases free the sole network slot and native FTP connection");
        }
        finally { await manager.StopAsync(); }
    }
    var restartedStore = NewStore();
    restartedStore.Load();
    var restored = restartedStore.GetById(item.Id)!;
    Check(restored.Status == DownloadStatus.Queued && restored.RetryCount == 0,
        "source-completion waiting survives queue persistence and restart");
    using (var manager = new DownloadManager(restartedStore, ftp, downloader, downloadConfig))
    {
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.DownloadStatusChanged += changed =>
        {
            if (changed.Id != restored.Id) return;
            if (changed.Status == DownloadStatus.Queued &&
                changed.ErrorMessage?.StartsWith("Waiting for release completion", StringComparison.Ordinal) == true)
                parked.TrySetResult();
            if (changed.Status == DownloadStatus.Completed) completed.TrySetResult();
            if (changed.Status == DownloadStatus.Failed)
                completed.TrySetException(new Exception(changed.ErrorMessage));
        };
        manager.Start();
        try
        {
            await parked.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            File.WriteAllBytes(Path.Combine(source, "second.bin"), second);
            File.Delete(Path.Combine(source, "second.bin-missing"));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(75), ct);
            Check(restored.ErrorMessage == null && restored.RetryCount == 0 &&
                File.ReadAllBytes(Path.Combine(destination, "first.bin")).SequenceEqual(first) &&
                File.ReadAllBytes(Path.Combine(destination, "second.bin")).SequenceEqual(second),
                "scheduled native rescan downloads late files and verifies the complete release automatically");
            Check(!File.Exists(Path.Combine(destination, "second.bin-missing")),
                "missing-file placeholders are not downloaded as release content");
        }
        finally { await manager.StopAsync(); }
    }
}

static async Task CheckIncompleteRaceRecovery(FtpClientFactory factory, string fixtureRoot,
    string remoteRoot, CancellationToken ct)
{
    await using var racePool = new FtpConnectionPool(factory, 2);
    await racePool.Initialize(ct);
    var source = new ServerConfig { Id = "arriving-source", Name = "Arriving source" };
    var receiver = new ServerConfig { Id = "arriving-receiver", Name = "Arriving receiver" };
    source.SpreadSite.Sections["TV"] = remoteRoot + "/arriving-source";
    receiver.SpreadSite.Sections["TV"] = remoteRoot + "/arriving-receiver";
    const string release = "Already.Started.S01E01-GRP";
    var first = new byte[] { 1, 2, 3 };
    var second = new byte[] { 4, 5, 6 };
    string LocalRelease(ServerConfig server) =>
        Path.Combine(fixtureRoot, "data", server.SpreadSite.Sections["TV"].TrimStart('/'), release);
    foreach (var server in new[] { source, receiver })
    {
        var path = LocalRelease(server);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "first.bin"), first);
        File.WriteAllText(Path.Combine(path, "second.bin-missing"), "");
        File.WriteAllText(Path.Combine(path, "release.sfv"),
            $"first.bin {System.IO.Hashing.Crc32.HashToUInt32(first):X8}\n" +
            $"second.bin {System.IO.Hashing.Crc32.HashToUInt32(second):X8}\n");
    }
    SpreadJob NewJob() => new("TV", release, SpreadMode.Race,
        new SpreadConfig { CompletionRefreshIntervalSeconds = 1 },
        new() { [source.Id] = racePool, [receiver.Id] = racePool },
        new() { [source.Id] = racePool, [receiver.Id] = racePool },
        new() { [source.Id] = source, [receiver.Id] = receiver }, new SpeedTracker(), new SkiplistEvaluator());
    using (var job = NewJob())
    {
        var errors = 0;
        var completed = 0;
        job.Error += (_, _) => errors++;
        job.Completed += _ => completed++;
        await job.RunAsync().WaitAsync(TimeSpan.FromSeconds(20), ct);
        Check(job.State == SpreadJobState.Waiting && errors == 0 && completed == 0,
            "native race discovery waits when both existing folders are still incomplete");
        Check(new[] { source, receiver }.All(server =>
            File.ReadAllBytes(Path.Combine(LocalRelease(server), "first.bin")).SequenceEqual(first)),
            "parking a partial race preserves existing landed volumes");
    }
    // Model a release's later arrival at both sites without exercising unsupported
    // glftpd FXP extensions in this loopback fixture. The real admission, scans,
    // manifest reads and completion decision are rerun after durable requeue.
    foreach (var server in new[] { source, receiver })
    {
        File.WriteAllBytes(Path.Combine(LocalRelease(server), "second.bin"), second);
        File.Delete(Path.Combine(LocalRelease(server), "second.bin-missing"));
    }
    using (var job = NewJob())
    {
        await job.RunAsync().WaitAsync(TimeSpan.FromSeconds(20), ct);
        Check(job.State == SpreadJobState.Completed && job.LastError == null,
            "native resumed race completes only after the missing inventory arrives");
    }
}

static async Task CheckSourceRelocation(FtpClientFactory factory, string fixtureRoot,
    string remoteRoot, CancellationToken ct)
{
    // Drive the real scanner and migration handler at a deterministic mid-race
    // boundary. Only the source role/path normally set by RunAsync are seeded;
    // ownership, directory probes and payload transfer all use native FTPS.
    // This fixture does not implement glftpd's server-to-server FXP extension.
    await using var sourcePool = new FtpConnectionPool(factory, 1);
    await sourcePool.Initialize(ct);
    var sourceFtp = new FtpOperations(sourcePool);
    FluentFTP.AsyncFtpClient originalClient;
    int connectCount;
    await using (var connection = await sourcePool.Borrow(ct))
    {
        originalClient = connection.Client;
        connectCount = originalClient.Status.ConnectCount;
    }

    const string fileName = "payload.bin";
    var payload = new byte[8192];
    new Random(153).NextBytes(payload);
    foreach (var trigger in new[] { "retr", "empty-list" })
    {
        var releaseName = $"Relocated.{trigger}.S01E01.1080p-GRP";
        var source = new ServerConfig { Id = "relocation-source", Name = "Relocating source" };
        var basePath = remoteRoot + "/relocation-" + trigger;
        source.SpreadSite.Sections["TV_HD"] = basePath + "/incoming";
        source.SpreadSite.Sections["ARCHIVE"] = basePath + "/archive";
        source.Notifications.WatchPath = basePath + "/recent";
        var oldPath = source.SpreadSite.Sections["TV_HD"] + "/" + releaseName;
        var newPath = source.Notifications.WatchPath + "/tv-hd/" + releaseName;
        var decoyPath = source.SpreadSite.Sections["ARCHIVE"] + "/" + releaseName;
        await sourceFtp.CreateDirectory(oldPath, ct);
        await sourceFtp.CreateDirectory(newPath, ct);
        await sourceFtp.CreateDirectory(decoyPath, ct);
        await sourceFtp.UploadFile(oldPath + "/" + fileName, payload, ct);
        await sourceFtp.UploadFile(decoyPath + "/" + fileName, [99, 98, 97], ct);

        using var job = new SpreadJob("TV_HD", releaseName, SpreadMode.Race, new SpreadConfig(),
            new() { [source.Id] = sourcePool }, new() { [source.Id] = sourcePool },
            new() { [source.Id] = source }, new SpeedTracker(), new SkiplistEvaluator());
        var paths = new Dictionary<string, string> { [source.Id] = oldPath };
        typeof(SpreadJob).GetField("_sitePathsRef", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(job, paths);
        JobField<HashSet<string>>(job, "_sourceServersField").Add(source.Id);
        await JobTask(job, "ScanSites", paths, ct);
        Check(job.Sites[source.Id].FilesOwned == 1, $"{trigger}: native scan establishes source ownership");
        var observedFile = JobField<Dictionary<string, SpreadFileInfo>>(job, "_fileInfos")[fileName];

        // Model a cross-filesystem glftpd move: the contents have moved, but the
        // original release directory remains visible until its later cleanup.
        string LocalPath(string remote) => Path.Combine(fixtureRoot, "data", remote.TrimStart('/'));
        File.Move(LocalPath(oldPath + "/" + fileName), LocalPath(newPath + "/" + fileName));
        await using (var connection = await sourcePool.Borrow(ct))
        {
            Check(await connection.Client.DirectoryExists(oldPath, ct)
                && !await connection.Client.FileExists(oldPath + "/" + fileName, ct),
                $"{trigger}: native FTPS sees the old directory after its payload moved");
            if (trigger == "retr")
            {
                var rejection = await connection.Client.Execute("RETR " + oldPath + "/" + fileName, ct);
                Check(rejection.Code == "550", "retr: native RETR rejects the vanished source file");
            }
        }

        if (trigger == "retr")
            await JobTask(job, "HandleSourceMigration", source.Id, ct);
        else
            await JobTask(job, "ScanSites", paths, ct); // Empty LIST must initiate the migration itself.

        var overrides = JobField<System.Collections.Concurrent.ConcurrentDictionary<string, string>>(
            job, "_extraSourcePaths");
        // ScanSites starts the handler asynchronously. Bound the wait and observe
        // its single-flight completion before reading the resulting job state.
        var until = DateTime.UtcNow.AddSeconds(5);
        while ((!overrides.ContainsKey(source.Id) || JobField<int>(job, "_failoverInFlight") != 0)
            && DateTime.UtcNow < until)
            await Task.Delay(20, ct);
        Check(overrides.TryGetValue(source.Id, out var relocated) && relocated == newPath,
            $"{trigger}: migration follows watch path ahead of a populated section decoy while old directory exists");
        Check(!JobField<HashSet<string>>(job, "_sourceMigratedAway").Contains(source.Id)
            && !JobField<HashSet<string>>(job, "_sourceMigrationPending").Contains(source.Id)
            && JobField<bool>(job, "_forceScan"),
            $"{trigger}: relocation preserves the source, clears pending state and requests an immediate scan");
        var resolved = (string)typeof(SpreadJob).GetMethod("ResolveSourcePath", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [relocated, observedFile])!;
        Check(resolved == newPath + "/" + fileName,
            $"{trigger}: a file observed before the move resolves to the relocated RETR path");
        await JobTask(job, "ScanSites", paths, ct);
        Check(job.Sites[source.Id].FilesOwned == 1 && job.Sites[source.Id].FilesTotal == 1,
            $"{trigger}: native rescan restores ownership from the relocated directory");
        using var downloaded = new MemoryStream();
        await sourceFtp.DownloadToStream(resolved, downloaded, ct);
        Check(downloaded.ToArray().SequenceEqual(payload), $"{trigger}: relocated native FTPS download preserves every byte");
    }

    // Failed directory probes are normal negative replies. Search through several
    // misses and verify the original pooled session remains usable without login.
    var fallback = new ServerConfig { Id = "fallback-source", Name = "Fallback source" };
    var fallbackRoot = remoteRoot + "/relocation-fallback";
    fallback.Notifications.WatchPath = fallbackRoot + "/recent";
    fallback.SpreadSite.Sections["TV_HD"] = fallbackRoot + "/incoming";
    fallback.SpreadSite.Sections["MISSING_ONE"] = fallbackRoot + "/missing-one";
    fallback.SpreadSite.Sections["MISSING_TWO"] = fallbackRoot + "/missing-two";
    fallback.SpreadSite.Sections["ARCHIVE"] = fallbackRoot + "/archive";
    const string fallbackRelease = "Fallback.S01E01.1080p-GRP";
    var fallbackPath = fallback.SpreadSite.Sections["ARCHIVE"] + "/" + fallbackRelease;
    await sourceFtp.CreateDirectory(fallbackPath, ct);
    using (var job = new SpreadJob("TV_HD", fallbackRelease, SpreadMode.Race, new SpreadConfig(),
        new() { [fallback.Id] = sourcePool }, new() { [fallback.Id] = sourcePool },
        new() { [fallback.Id] = fallback }, new SpeedTracker(), new SkiplistEvaluator()))
    {
        var task = (Task<string?>)typeof(SpreadJob).GetMethod("FindRelocatedSourcePath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(job, [fallback.Id, fallback.SpreadSite.Sections["TV_HD"] + "/" + fallbackRelease, ct, CancellationToken.None])!;
        Check(await task == fallbackPath, "native relocation falls back through missing watch and section directories");
    }
    await using (var connection = await sourcePool.Borrow(ct))
    {
        var reply = await connection.Client.Execute("NOOP", ct);
        Check(ReferenceEquals(connection.Client, originalClient)
            && connection.Client.Status.ConnectCount == connectCount && reply.Code == "200",
            "relocation scans, negative probes and downloads preserve one reusable native FTPS session");
    }
}

static T JobField<T>(SpreadJob job, string name) =>
    (T)typeof(SpreadJob).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(job)!;

static Task JobTask(SpreadJob job, string name, params object[] arguments) =>
    (Task)typeof(SpreadJob).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(job, arguments)!;

internal static class NativeDirectory
{
    // SDK FILE_INFO_BY_HANDLE_CLASS: class 11 restarts the existing HANDLE,
    // class 10 continues after the last returned entry. FILE_ID_BOTH_DIR_INFO
    // has FileNameLength at byte 60 and its UTF-16 FileName at byte 104.
    // https://learn.microsoft.com/windows/win32/api/minwinbase/ne-minwinbase-file_info_by_handle_class
    // https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-file_id_both_dir_info
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        IntPtr buffer, uint bufferSize);

    internal static SafeFileHandle Open(string path)
    {
        var handle = CreateFileW(path, 1 /* FILE_LIST_DIRECTORY */, 7 /* share read/write/delete */,
            IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error, "Could not open the smoke fixture directory");
    }

    internal static (HashSet<string> Names, int Pages) Enumerate(SafeFileHandle handle)
    {
        const int bufferSize = 512; // Deliberately small: exercise continuation, not just restart.
        var buffer = Marshal.AllocHGlobal(bufferSize);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var informationClass = 11;
        var pages = 0;
        try
        {
            while (GetFileInformationByHandleEx(handle, informationClass, buffer, bufferSize))
            {
                if (++pages > 100) throw new InvalidOperationException("Directory enumeration did not terminate");
                informationClass = 10;
                var offset = 0;
                while (true)
                {
                    if (offset > bufferSize - 104) throw new InvalidDataException("Directory entry exceeds its buffer");
                    var entry = IntPtr.Add(buffer, offset);
                    var next = Marshal.ReadInt32(entry);
                    var nameBytes = Marshal.ReadInt32(entry, 60);
                    if (nameBytes < 0 || (nameBytes & 1) != 0 || nameBytes > bufferSize - offset - 104)
                        throw new InvalidDataException("Invalid directory entry name length");
                    var name = Marshal.PtrToStringUni(IntPtr.Add(entry, 104), nameBytes / 2)!;
                    if (name is not "." and not ".." && !names.Add(name))
                        throw new InvalidDataException("Directory continuation returned a duplicate entry");
                    if (next == 0) break;
                    if (next < 104 || (next & 7) != 0 || next > bufferSize - offset)
                        throw new InvalidDataException("Invalid directory entry continuation offset");
                    offset += next;
                }
            }
            var error = Marshal.GetLastWin32Error();
            if (error != 18 /* ERROR_NO_MORE_FILES */)
                throw new Win32Exception(error, "Could not enumerate the smoke fixture directory");
            return (names, pages);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
