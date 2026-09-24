using System.IO;
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
using GlDrive.Spread;

// This harness only connects to the disposable loopback fixture, with an isolated trust store.
var state = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement;
var port = state.GetProperty("port").GetInt32();
var root = state.GetProperty("root").GetString()!;
var fingerprint = state.GetProperty("fingerprint").GetString()!;
var config = new ServerConfig { Name = "Release verification", Connection = new() { Host = "127.0.0.1", Port = port, Username = "release-check" } };
var certs = new CertificateManager(Path.Combine(root, "trusted.json"));
certs.TrustCertificate($"127.0.0.1:{port}", fingerprint);
certs.CertificatePrompt += (_, _) => Task.FromResult(false);
var factory = new FtpClientFactory(config, certs);
await using var pool = new FtpConnectionPool(factory, 2);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
await pool.Initialize(deadline.Token);
var ftp = new FtpOperations(pool);
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
var remoteRoot = "/check-" + Guid.NewGuid().ToString("N");
await ftp.CreateDirectory(remoteRoot, deadline.Token);
// The sole upload-capable site already holds the release; the other site is
// download-only and does not have it. Exercise real discovery and terminal state.
const string existingRelease = "Already.Present.S01E01.1080p-GRP";
var receiver = new ServerConfig { Id = "receiver", Name = "Receiver" };
receiver.SpreadSite.Sections["TV"] = remoteRoot + "/receiver";
var leech = new ServerConfig { Id = "leech", Name = "Download only" };
leech.SpreadSite.Sections["TV"] = remoteRoot + "/leech";
leech.SpreadSite.DownloadOnly = true;
await ftp.CreateDirectory(receiver.SpreadSite.Sections["TV"] + "/" + existingRelease, deadline.Token);
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
await ftp.CreateDirectory(searchRoot + "/Show.S01.COMPLETE.1080p-GRP", deadline.Token);
await ftp.CreateDirectory(searchRoot + "/[Group] Show.S01", deadline.Token);
foreach (var method in new[] { SearchMethod.CachedIndex, SearchMethod.LiveCrawl })
{
    using var search = new FtpSearchService(pool, new SearchConfig
    {
        Method = method, SearchPaths = [searchRoot], MaxDepth = 2
    });
    if (method == SearchMethod.CachedIndex) await search.RefreshIndex(ct: deadline.Token);
    var results = await search.Search("", ct: deadline.Token);
    Check(results.Count == 2 && results.Any(r => r.ReleaseName == "Show.S01.COMPLETE.1080p-GRP") &&
        results.Any(r => r.ReleaseName == "[Group] Show.S01"),
        $"native {method} excludes status directories and preserves real release names");
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
