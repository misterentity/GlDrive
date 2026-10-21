using System.IO;
using System.Reflection;
using GlDrive.Config;
using GlDrive.Ftp;
using GlDrive.Services;
using GlDrive.Tls;

/// <summary>Publishes a real native FTPS session while competing borrowers wait or cancel.</summary>
internal static class NativeInitializationCheck
{
    public static async Task Run(FtpClientFactory factory, string root, CancellationToken ct)
    {
        foreach (var recovery in new[] { false, true })
            await CheckPublication(factory, root, recovery, ct);
    }

    private static async Task CheckPublication(FtpClientFactory factory, string root, bool recovery, CancellationToken ct)
    {
        var phase = recovery ? "reinitialization" : "initialization";
        var pause = Path.Combine(root, "pause-authentication");
        var paused = Path.Combine(root, "authentication-paused");
        var authentications = Path.Combine(root, "authentication-attempts");
        int LoginCount() => File.Exists(authentications) ? File.ReadAllLines(authentications).Length : 0;
        var before = LoginCount();
        var gate = new ServerLoginGate("native-" + phase, 1, 1);
        var pool = new FtpConnectionPool(factory, 1, gate);
        using var mount = new MountService(new ServerConfig { Id = "native-" + phase, Name = "Native readiness fixture" },
            new DownloadConfig(), new CertificateManager(Path.Combine(root, phase + "-unused-trust.json")));
        var mountPool = typeof(MountService).GetField("_pool", BindingFlags.Instance | BindingFlags.NonPublic)!;
        mountPool.SetValue(mount, pool);
        var hiddenAfterDisposal = false;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operation.CancelAfter(TimeSpan.FromSeconds(30));
        using var cancelledBorrow = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
        Task? initialization = null;
        Task<PooledConnection>? cancelled = null;
        Task<PooledConnection>? waiting = null;
        try
        {
            Check(mount.Pool == null, phase + " mount does not expose an uninitialized native pool");
            File.Delete(paused);
            File.WriteAllText(pause, "hold the fixture's first USER response");
            initialization = recovery ? pool.Reinitialize(operation.Token) : pool.Initialize(operation.Token);
            while (!File.Exists(paused))
            {
                if (initialization.IsCompleted) await initialization;
                await Task.Delay(10, operation.Token);
            }
            Check(!initialization.IsCompleted && gate.Held == 1 && pool.TotalCreated == 1 && mount.Pool == null,
                phase + " reserves one slot and one actual permit while native login is paused");

            cancelled = pool.Borrow(cancelledBorrow.Token);
            Check(!cancelled.IsCompleted && pool.TotalCreated == 1 && gate.Held == 1,
                phase + " makes a concurrent borrower wait without another login reservation");
            cancelledBorrow.Cancel();
            var wasCancelled = false;
            try { await cancelled; }
            catch (OperationCanceledException) { wasCancelled = true; }
            Check(wasCancelled && pool.TotalCreated == 1 && gate.Held == 1 && !pool.IsThrottled,
                phase + " preserves its slot and permit after the competing borrower cancels");

            waiting = pool.Borrow(operation.Token);
            Check(!waiting.IsCompleted && pool.TotalCreated == 1 && gate.Held == 1,
                phase + " keeps a second concurrent borrower waiting for the initial session");
            File.Delete(pause);
            await initialization.WaitAsync(TimeSpan.FromSeconds(10), ct);
            var connection = await waiting.WaitAsync(TimeSpan.FromSeconds(10), ct);
            var client = connection.Client;
            Check(await Noop(connection, operation.Token) && ReferenceEquals(mount.Pool, pool) && pool.TotalCreated == 1 &&
                  pool.ActiveCount == 1 && gate.Held == 1 && LoginCount() == before + 1,
                phase + " publishes one usable native session directly to the waiting borrower");
            await connection.DisposeAsync();
            await using (var reused = await pool.Borrow(operation.Token))
            {
                Check(ReferenceEquals(client, reused.Client) && await Noop(reused, operation.Token),
                    phase + " returns the same synchronized native session to the next borrower");
            }
            Check(pool.TotalCreated == 1 && pool.ActiveCount == 0 && gate.Held == 1 &&
                  pool.QuarantineSize == 0 && !pool.IsThrottled && LoginCount() == before + 1,
                phase + " finishes with one accounted login and no leaked lease or cooldown");
        }
        finally
        {
            try
            {
                File.Delete(pause);
                cancelledBorrow.Cancel();
                operation.Cancel();
                var tasks = new Task?[] { initialization, cancelled, waiting }.OfType<Task>().ToArray();
                try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch when (tasks.All(task => task.IsCompleted)) { }
                // If native work cannot settle, the deadline above escapes before
                // disposal: never tear down a native session still executing.
                if (cancelled?.IsCompletedSuccessfully == true) await cancelled.Result.DisposeAsync();
                if (waiting?.IsCompletedSuccessfully == true) await waiting.Result.DisposeAsync();
                await pool.DisposeAsync();
                hiddenAfterDisposal = mount.Pool == null;
                File.Delete(paused);
            }
            finally
            {
                // The fixture owns the pool's lifetime, including failed native
                // cleanup. Never let the synthetic mount dispose it a second time.
                mountPool.SetValue(mount, null);
            }
        }
        Check(gate.Held == 0 && pool.ActiveCount == 0 && !pool.IsConnected && hiddenAfterDisposal,
            phase + " disposal hides the mount pool and releases the single actual account permit");
    }

    private static async Task<bool> Noop(PooledConnection connection, CancellationToken ct)
    {
        try { return (await connection.Client.Execute("NOOP", ct)).Success; }
        catch
        {
            // Cancellation can leave native receive work behind the managed
            // task. Failed probes must enter deferred quarantine on return.
            connection.Poison("native initialization check NOOP failed");
            throw;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
