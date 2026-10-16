using System.Diagnostics;
using System.IO;
using System.Reflection;
using FluentFTP;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Services;
using GlDrive.Tls;

/// <summary>Actual target factory and native FTPS command path, with no production state.</summary>
internal static class NativeNukePollCheck
{
    public static async Task Run(FtpClientFactory factory, string root, CancellationToken ct)
    {
        var responsePath = Path.Combine(root, "site-nukes-response");
        var commandsPath = Path.Combine(root, "site-nukes-commands");
        var config = new ServerConfig { Id = "native-nuke-fixture", Name = "Native nuke fixture" };
        var certs = new CertificateManager(Path.Combine(root, "nuke-unused-trust.json"));
        using var mount = new MountService(config, new DownloadConfig(), certs);
        await using var main = new FtpConnectionPool(factory, 1);
        await using var spread = new FtpConnectionPool(factory, 1);
        await main.Initialize(ct);
        await spread.Initialize(ct);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MountService).GetField("_pool", flags)!.SetValue(mount, main);
        typeof(MountService).GetProperty(nameof(MountService.CurrentState))!.SetValue(mount, MountState.Connected);
        var createTarget = typeof(NukePoller).GetMethod("CreateTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
        Func<CancellationToken, Task<FtpReply>> Target(Func<FtpConnectionPool?>? fallback)
        {
            var target = createTarget.Invoke(null, [mount, fallback])!;
            var available = (Func<bool>)target.GetType().GetProperty("IsAvailable")!.GetValue(target)!;
            Check(available(), "native nuke factory accepts a connected mount");
            return (Func<CancellationToken, Task<FtpReply>>)target.GetType().GetProperty("ReadNukes")!.GetValue(target)!;
        }
        int Commands() => File.Exists(commandsPath) ? File.ReadAllLines(commandsPath).Length : 0;
        AsyncFtpClient spreadClient;
        await using (var seed = await spread.Borrow(ct)) spreadClient = seed.Client;
        try
        {
            File.WriteAllText(responsePath, "200");
            await using (var occupied = await main.Borrow(ct))
            {
                var fallbackCalls = 0;
                var read = Target(() => { fallbackCalls++; return spread; });
                var before = Commands();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(25));
                var elapsed = Stopwatch.StartNew();
                var reply = await read(deadline.Token).WaitAsync(TimeSpan.FromSeconds(30));
                Check(reply.Code == "200" && reply.Success && Commands() == before + 1,
                    "occupied native main pool sends exactly one SITE NUKES over the target's fallback");
                Check(fallbackCalls == 1 && elapsed.Elapsed >= TimeSpan.FromSeconds(8),
                    "actual nuke factory resolves the fallback and honors the primary borrow budget");
                await using (var reuse = await spread.Borrow(ct))
                {
                    Check(ReferenceEquals(reuse.Client, spreadClient) && !reuse.Poisoned &&
                          (await reuse.Client.Execute("NOOP", CancellationToken.None).WaitAsync(ct)).Success,
                        "native SITE NUKES fallback returns the same synchronized reusable session");
                }
                Check(main.TotalCreated == 1 && spread.TotalCreated == 1 && main.ActiveCount == 1 && spread.ActiveCount == 0,
                    "native nuke fallback neither opens an extra login nor leaks its lease");

                // No fallback: the real factory/borrow path must classify deadline before
                // any command can be sent. PollAll's breaker reaction has unit coverage.
                var readWithoutFallback = Target(null);
                before = Commands();
                using var busyDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                busyDeadline.CancelAfter(TimeSpan.FromMilliseconds(150));
                Exception? cancelled = null;
                try { await readWithoutFallback(busyDeadline.Token).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException ex) { cancelled = ex; }
                Check(cancelled?.GetType().Name == "PollBorrowCanceledException" && Commands() == before,
                    "native nuke factory classifies a busy borrow without sending a command");
            }

            // A clean final refusal is a command failure for the breaker, but the wire
            // session remains synchronized and usable for the subsequent successful poll.
            File.WriteAllText(responsePath, "500");
            var readAvailable = Target(() => spread);
            using var commandDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            commandDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            var denied = await readAvailable(commandDeadline.Token).WaitAsync(TimeSpan.FromSeconds(15));
            Check(denied.Code == "500" && !denied.Success && main.QuarantineSize == 0,
                "native final SITE NUKES denial preserves the primary session");
            File.WriteAllText(responsePath, "200");
            var recovered = await readAvailable(commandDeadline.Token).WaitAsync(TimeSpan.FromSeconds(15));
            Check(recovered.Success && main.TotalCreated == 1 && main.ActiveCount == 0,
                "native nuke command recovers on the same primary login after a clean denial");
        }
        finally
        {
            File.Delete(responsePath);
            // MountService did not mount anything and owns no background work. Pools
            // are owned by this scope, so detach the test-only composition before disposal.
            typeof(MountService).GetField("_pool", flags)!.SetValue(mount, null);
        }
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
