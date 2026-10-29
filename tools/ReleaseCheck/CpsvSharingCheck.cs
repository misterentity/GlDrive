using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Downloads.Segmented;
using GlDrive.Ftp;
using GlDrive.Spread;
using GlDrive.Tls;

// Opt-in native verification, not a unit-test dependency on a particular network adapter.
// Control accepts loopback only. Data binds a local RFC1918 address because the product
// correctly rejects CPSV loopback targets; only a peer on that SAME host address is accepted.
// The peer pins the application's CPSV TLS certificate and serves only in-memory fixture bytes.
internal static class CpsvSharingCheck
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<string> Passed = [];
    private static string Phase = "";

    internal static async Task RunMultiNetwork(string evidenceDirectory, string firstAdapter, string secondAdapter)
    {
        Directory.CreateDirectory(evidenceDirectory);
        var evidence = Path.GetFullPath(evidenceDirectory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var started = DateTime.UtcNow;
        string? failure = null;
        object? networks = null;
        try
        {
            Phase = "two selected networks";
            var configured = new[] { firstAdapter, secondAdapter }.Select(name => new NetworkPathConfig { AdapterName = name }).ToList();
            var selected = NetworkCatalog.Choose(configured, NetworkCatalog.EnumerateAdapters());
            Check(selected.Count == 2 && selected.All(n => !n.IsDefault) && selected.Select(n => n.BindAddress).Distinct().Count() == 2,
                "two selected live adapters resolve to distinct bound IPv4 addresses");
            foreach (var path in selected) path.Settings!.AdapterId = path.Id;
            networks = selected.Select(n => new { name = n.DisplayName, bound = !n.IsDefault }).ToArray();
            await using var f = await Fixture.Create(evidence, "two-networks", true, deadline.Token,
                networkAddresses: selected.Select(n => n.BindAddress!).ToArray());
            var usage = new NetworkUsageStore(Path.Combine(f.Root, "isolated-network-usage.json"));
            var usageField = typeof(DownloadNetworkServices).GetField("_usage", BindingFlags.NonPublic | BindingFlags.Static)!;
            var configField = typeof(DownloadNetworkServices).GetField("_config", BindingFlags.NonPublic | BindingFlags.Static)!;
            var priorUsage = usageField.GetValue(null);
            var priorConfig = configField.GetValue(null);
            usageField.SetValue(null, usage);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            Task? download = null;
            try
            {
                var config = new DownloadConfig
                {
                    MultiStream = new MultiStreamConfig { Enabled = true, BorrowIdleRaceLogin = true, FtpMinSplitMb = 4, Networks = configured }
                };
                var downloader = new StreamingDownloader(f.Main, f.ServerId, config, 0, f.Manager);
                var destination = Path.Combine(f.Root, "multi-network-payload.bin");
                download = downloader.DownloadFiles([new FtpDownloadFile("/payload.bin", destination, f.Peer.Payload.Length)], null, operation.Token);
                await Task.WhenAll(f.Peer.FirstMainData.Task, f.Peer.FirstLoanData.Task).WaitAsync(TimeSpan.FromSeconds(20), deadline.Token);
                Check(!download.IsCompleted && f.Main.ActiveCount == 1 && f.Spread.ActiveCount == 1,
                    "actual StreamingDownloader opens simultaneous main and borrowed race lanes");
                Check(f.Peer.ObservedSources.Order().SequenceEqual(new[] { 0, 1 }),
                    "native fixture observes one data connection from each selected adapter address");
                Check(f.Gate.Held == 2 && f.Peer.MaxLiveAuthenticated == 2,
                    "two networks use the existing two-account-permit budget without additional logins");
                f.Peer.ReleaseMain.TrySetResult();
                await download.WaitAsync(TimeSpan.FromSeconds(60), deadline.Token);
                Check(File.ReadAllBytes(destination).SequenceEqual(f.Peer.Payload),
                    "two bound native CPSV lanes reconstruct the exact 16 MiB payload");
                var firstBytes = usage.UsedThisPeriod(selected[0].Id, AllowancePeriod.Daily);
                var secondBytes = usage.UsedThisPeriod(selected[1].Id, AllowancePeriod.Daily);
                Check(firstBytes > 0 && secondBytes > 0 && firstBytes + secondBytes == f.Peer.Payload.Length,
                    "both selected adapters carry accounted payload bytes with no duplicate accounting");
                Check(f.Main.ActiveCount == 0 && f.Spread.ActiveCount == 0 && f.Main.QuarantineSize == 0 && f.Spread.QuarantineSize == 0,
                    "successful two-network completion returns both leases without quarantine");
                await using var main = await f.Main.Borrow(deadline.Token);
                await using var spread = await f.Spread.Borrow(deadline.Token);
                Check((await Noop(main, deadline.Token)).Code == "200" && (await Noop(spread, deadline.Token)).Code == "200",
                    "both native control sessions remain synchronized after multi-network data completion");
            }
            finally
            {
                operation.Cancel();
                f.Peer.ReleaseMain.TrySetResult();
                if (download != null && !await Settled(download, TimeSpan.FromSeconds(20)))
                {
                    f.SuppressTeardown("Multi-network download did not settle");
                    throw new TimeoutException("Native teardown suppressed while multi-network work is active");
                }
                usageField.SetValue(null, priorUsage);
                configField.SetValue(null, priorConfig);
            }
            await f.DisposeAsync();
            Check(f.Gate.Held == 0, "fixture disposal releases every login permit");
            Console.WriteLine("PASS: native CPSV two-network verification complete");
        }
        catch (Exception ex)
        {
            // Runtime adapter addresses are intentionally excluded from this receipt.
            failure = ex.GetType().Name;
            throw;
        }
        finally
        {
            File.WriteAllText(Path.Combine(evidence, "network-result.json"), JsonSerializer.Serialize(new
            {
                startedUtc = started, finishedUtc = DateTime.UtcNow,
                appVersion = typeof(FtpConnectionPool).Assembly.GetName().Version?.ToString(),
                success = failure == null, assertions = Passed, failure, networks,
                scope = "Actual StreamingDownloader, native GnuTLS control and managed CPSV data TLS; two same-host source addresses, shared login gate and isolated usage store. Proves socket binding, not independent WAN bandwidth or physical adapter egress. No production queue or configuration changes."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    internal static async Task Run(string evidenceDirectory)
    {
        Directory.CreateDirectory(evidenceDirectory);
        var evidence = Path.GetFullPath(evidenceDirectory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var started = DateTime.UtcNow;
        string? failure = null;
        try
        {
            Phase = "clean yield";
            await CleanYieldAndReacquire(evidence, deadline.Token);
            Phase = "stalled setup";
            await CleanYieldAndReacquire(evidence, deadline.Token, stalledSetup: true);
            Phase = "manual FXP handoff";
            await CleanYieldAndReacquire(evidence, deadline.Token, manualActivity: true);
            Phase = "opposite pair acquisition";
            await OppositePairAcquisition(evidence, deadline.Token);
            Phase = "poison wake";
            await PoisonedSlotWakesWaitingBorrower(evidence, deadline.Token);
            Console.WriteLine("PASS: native CPSV race-login sharing verification complete");
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            throw;
        }
        finally
        {
            File.WriteAllText(Path.Combine(evidence, "sharing-result.json"), JsonSerializer.Serialize(new
            {
                startedUtc = started, finishedUtc = DateTime.UtcNow,
                appVersion = typeof(FtpConnectionPool).Assembly.GetName().Version?.ToString(),
                success = failure == null, assertions = Passed, failure,
                scope = "Real native GnuTLS control, managed CPSV data TLS, separate pools/shared gate, actual lender/lane/scheduler; queued race, held-destination manual StartFxp handoff and opposite-direction pair acquisition. No remote FXP payload or production data."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + Phase + ": " + message);
        Passed.Add(Phase + ": " + message);
        Console.WriteLine("PASS: " + Phase + ": " + message);
    }

    private static async Task CleanYieldAndReacquire(string evidence, CancellationToken ct, bool stalledSetup = false, bool manualActivity = false)
    {
        var phaseName = manualActivity ? "manual" : stalledSetup ? "stalled-setup" : "clean";
        await using var f = await Fixture.Create(evidence, phaseName, pauseFirstTransfers: true, ct, stalledSetup);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var manualCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Check(f.Main.UseCpsv && f.Spread.UseCpsv, "native FEAT discovers CPSV on both separate pools");
        var originalSpread = await f.Spread.Borrow(ct);
        var spreadClient = originalSpread.Client;
        await originalSpread.DisposeAsync();
        var mainConnection = await f.Main.Borrow(ct);
        var mainClient = mainConnection.Client;
        Check(!ReferenceEquals(mainClient, spreadClient), "main and race connections are distinct");
        var network = new NetworkPath("fixture", "same-host fixture", null, null);
        var laneType = typeof(SegmentedTransfer).Assembly.GetType("GlDrive.Downloads.Segmented.FtpLane")!;
        var mainLane = (ISegmentLane)Activator.CreateInstance(laneType,
            [mainConnection, f.Main, true, network, "main", null, null, null])!;
        var sourceType = typeof(SegmentedTransfer).Assembly.GetType("GlDrive.Downloads.Segmented.RaceLoginLaneSource")!;
        var source = new RecordingSource((ILaneSource)Activator.CreateInstance(sourceType, [f.Manager, f.ServerId, network, null, null])!);
        var localPath = Path.Combine(f.Root, "payload.bin");
        var transfer = new SegmentedTransfer([new TransferFile("/payload.bin", localPath, f.Peer.Payload.Length)],
            new SegmentedTransferOptions
            {
                BufferSize = 8192, MinSplitBytes = 16384, MaxLanes = 2,
                ExtraLanePollInterval = TimeSpan.FromMilliseconds(30),
                ProgressInterval = TimeSpan.FromMilliseconds(30), StallTimeout = TimeSpan.FromSeconds(30)
            });
        var download = transfer.RunAsync([mainLane], source, null, operation.Token);
        Task<PooledConnection>? waitingRace = null;
        Task? manual = null;
        bool raceLeaseClaimed = false;
        try
        {
            await f.Peer.FirstMainData.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            await (stalledSetup ? f.Peer.LoanSetupEntered.Task : f.Peer.FirstLoanData.Task).WaitAsync(TimeSpan.FromSeconds(15), ct);
            Check(f.Main.ActiveCount == 1 && f.Spread.ActiveCount == 1 && f.Gate.Held == 2,
                "idle download holds one main and one borrowed race login within the account cap");
            Check(await source.TryAcquireAsync(ct) == null, "one lane source cannot lend its already outstanding loan twice");

            var watch = Stopwatch.StartNew();
            if (manualActivity)
            {
                // The main lane already holds this destination pool. The real StartFxp
                // entrypoint must take activity ownership, reclaim the source loan, and
                // wait here before issuing ANY remote-transfer command.
                const string destinationId = "z-fixture-held-destination";
                Field<Dictionary<string, FtpConnectionPool>>(f.Manager, "_spreadPools").Add(destinationId, f.Main);
                var firstLoan = source.Acquired.Single();
                manual = f.Manager.StartFxp(f.ServerId, "/payload.bin", destinationId, "/never-written.bin", manualCancellation.Token);
                Check(!f.Manager.IsRaceIdle(f.ServerId), "actual manual StartFxp immediately prevents new download loans");
                await Until(() => Field<PooledConnection?>(firstLoan, "_conn") == null && f.Spread.ActiveCount == 1,
                    TimeSpan.FromSeconds(8), ct);
                Check(!manual.IsCompleted && !download.IsCompleted && f.Main.ActiveCount == 1 && f.Peer.DoubleAbortReplies > 0,
                    "manual FXP reclaims the synchronized source while its destination remains held and download stays alive");
                Check(f.Peer.Authenticated == 2 && f.Peer.MaxLiveAuthenticated == 2 && f.Gate.Held == 2,
                    "manual handoff creates no third login or account permit");
                Check(await source.TryAcquireAsync(ct) == null, "manual activity prevents a competing download loan");
                File.WriteAllText(Path.Combine(evidence, phaseName + "-yield-latency-ms.txt"), watch.ElapsedMilliseconds.ToString());
                manualCancellation.Cancel();
                try { await manual.WaitAsync(TimeSpan.FromSeconds(8), ct); throw new InvalidOperationException("Held manual transfer unexpectedly completed"); }
                catch (OperationCanceledException) when (manualCancellation.IsCancellationRequested) { }
                Check(f.Manager.IsRaceIdle(f.ServerId), "cancelled manual operation releases its activity scope and acquired source lease");
            }
            else
            {
                f.SetQueued(true);
                waitingRace = f.Spread.Borrow(operation.Token);
                await using (var returned = await waitingRace.WaitAsync(TimeSpan.FromSeconds(8), ct))
                {
                    raceLeaseClaimed = true;
                    Check(ReferenceEquals(spreadClient, returned.Client) != stalledSetup,
                        stalledSetup ? "queued race receives a replacement for the loan stalled before TLS setup" : "queued race receives the same yielded native control session");
                    if (stalledSetup) spreadClient = returned.Client;
                    Check(!PendingSequence(returned.Client), stalledSetup
                        ? "replacement has no pending CPSV sequence; original setup session is quarantined"
                        : "yield consumes the CPSV data completion before returning the session");
                    var reply = await Noop(returned, ct);
                    Check(reply.Code == "200", stalledSetup ? "replacement control session answers NOOP 200" : "race's first NOOP sees 200 after an injected 426 plus trailing 226");
                    if (stalledSetup)
                    {
                        await f.Peer.LoanSetupClosed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                        Check(!download.IsCompleted && f.Gate.Held == 2 && f.Spread.QuarantineSize == 1,
                            "blocked TLS setup yields promptly while the download stays alive and old native session remains quarantined");
                    }
                    else Check(f.Peer.DoubleAbortReplies > 0, "fixture actually sent the double abort completion being tested");
                    Check(await source.TryAcquireAsync(ct) == null, "queued race prevents lending while its returned session is held");
                    if (!stalledSetup) Check(f.Peer.Authenticated == 2 && f.Peer.MaxLiveAuthenticated == 2,
                        "clean yield creates no third physical login or reconnect");
                    File.WriteAllText(Path.Combine(evidence, phaseName + "-yield-latency-ms.txt"), watch.ElapsedMilliseconds.ToString());
                }
                f.SetQueued(false);
            }
            await f.Peer.SecondLoanData.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Check(f.Spread.ActiveCount == 1, "the same in-progress download reacquires the race lane after the race ends");
            f.Peer.ReleaseMain.TrySetResult();
            await download.WaitAsync(TimeSpan.FromSeconds(20), ct);
            Check(File.ReadAllBytes(localPath).SequenceEqual(f.Peer.Payload), "yield and reacquisition preserve every final payload byte");
            Check(!File.Exists(localPath + StagedFile.StagingSuffix), "successful shared download publishes and removes staging");
            if (stalledSetup) await Until(() => f.Spread.QuarantineSize == 0, TimeSpan.FromSeconds(45), ct);
            Check(f.Main.ActiveCount == 0 && f.Spread.ActiveCount == 0 && f.Spread.QuarantineSize == 0,
                "clean sharing leaves no active borrow or quarantined session");
            await using (var mainAgain = await f.Main.Borrow(ct))
            await using (var spreadAgain = await f.Spread.Borrow(ct))
            {
                Check(ReferenceEquals(mainClient, mainAgain.Client) && ReferenceEquals(spreadClient, spreadAgain.Client),
                    "each session returns to its original pool after completion");
                Check((await Noop(mainAgain, ct)).Code == "200" &&
                      (await Noop(spreadAgain, ct)).Code == "200",
                    "both returned control sessions remain synchronized and reusable");
            }
            Check(f.Gate.Held == 2 && f.Main.TotalCreated == 1 && f.Spread.TotalCreated == 1 &&
                  f.Peer.Authenticated == (stalledSetup ? 3 : 2) && f.Peer.MaxLiveAuthenticated == (stalledSetup ? 3 : 2),
                stalledSetup ? "one quarantined setup failure requires exactly one replacement; accounting remains at two permits" : "clean borrow/yield/reacquire keeps exactly two physical sessions and two accounted permits");
        }
        finally
        {
            f.SetQueued(true);
            f.Peer.ReleaseMain.TrySetResult();
            operation.Cancel();
            manualCancellation.Cancel();
            // Cleanup uses its own deadline, not the possibly already-cancelled test token.
            // Suppress pool teardown if a transfer does not settle: the isolated process must
            // fail without freeing any native session underneath a live receiver.
            var drained = await Settled(download, TimeSpan.FromSeconds(45));
            if (manual != null) drained &= await Settled(manual, TimeSpan.FromSeconds(15));
            if (waitingRace != null)
            {
                drained &= await Settled(waitingRace, TimeSpan.FromSeconds(15));
                if (!raceLeaseClaimed && waitingRace.IsCompletedSuccessfully)
                    await waitingRace.Result.DisposeAsync();
            }
            if (!drained)
            {
                f.SuppressTeardown("Native sharing operations did not settle after cancellation");
                throw new TimeoutException("Fixture cleanup could not drain its operations; native teardown suppressed");
            }
            f.SetQueued(false);
        }
        await f.DisposeAsync();
        Check(f.Gate.Held == 0, "clean fixture disposal releases both account permits");
    }

    private static async Task OppositePairAcquisition(string evidence, CancellationToken ct)
    {
        await using var f = await Fixture.Create(evidence, "opposite-pairs", pauseFirstTransfers: false, ct);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var method = typeof(SpreadManager).GetMethod("BorrowDirectFxpPair", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(PooledConnection));
        async Task<PooledConnection> DelayedBorrow(FtpConnectionPool pool, CancellationToken token)
        {
            var lease = await pool.Borrow(token);
            try { await Task.Delay(80, token); return lease; }
            catch { await lease.DisposeAsync(); throw; }
        }
        async Task UsePair(string sourceId, FtpConnectionPool sourcePool, string destinationId, FtpConnectionPool destinationPool)
        {
            var pair = await (Task<(PooledConnection Source, PooledConnection Destination)>)method.Invoke(null,
                [sourceId, (Func<CancellationToken, Task<PooledConnection>>)(token => DelayedBorrow(sourcePool, token)),
                 destinationId, (Func<CancellationToken, Task<PooledConnection>>)(token => DelayedBorrow(destinationPool, token)),
                 operation.Token, TimeSpan.FromSeconds(8)])!;
            await using var source = pair.Source;
            await using var destination = pair.Destination;
            Check(sourcePool.ActiveCount == 1 && destinationPool.ActiveCount == 1 && !ReferenceEquals(source.Client, destination.Client),
                sourceId + " to " + destinationId + " acquires two distinct native one-slot pool sessions");
            Check((await Noop(source, operation.Token)).Code == "200" && (await Noop(destination, operation.Token)).Code == "200",
                sourceId + " to " + destinationId + " sessions remain synchronized");
        }
        var both = Task.WhenAll(UsePair("a", f.Main, "b", f.Spread), UsePair("b", f.Spread, "a", f.Main));
        try
        {
            await both.WaitAsync(TimeSpan.FromSeconds(15), ct);
            Check(f.Main.ActiveCount == 0 && f.Spread.ActiveCount == 0 && f.Gate.Held == 2 &&
                  f.Peer.Authenticated == 2 && f.Peer.MaxLiveAuthenticated == 2,
                "opposite-direction acquisitions complete without ABBA deadlock, leaked leases, or extra logins");
        }
        finally
        {
            operation.Cancel();
            if (!await Settled(both, TimeSpan.FromSeconds(20)))
            {
                f.SuppressTeardown("Opposite pair borrowers did not settle");
                throw new TimeoutException("Pair fixture cleanup did not settle; native teardown suppressed");
            }
        }
        await f.DisposeAsync();
        Check(f.Gate.Held == 0, "pair fixture disposal releases both account permits");
    }

    private sealed class RecordingSource(ILaneSource inner) : ILaneSource
    {
        internal ConcurrentQueue<ISegmentLane> Acquired { get; } = [];
        public async Task<ISegmentLane?> TryAcquireAsync(CancellationToken ct)
        {
            var lane = await inner.TryAcquireAsync(ct);
            if (lane != null) Acquired.Enqueue(lane);
            return lane;
        }
    }

    private static async Task PoisonedSlotWakesWaitingBorrower(string evidence, CancellationToken ct)
    {
        await using var f = await Fixture.Create(evidence, "poison", pauseFirstTransfers: false, ct);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loan = await f.Spread.Borrow(operation.Token);
        var original = loan.Client;
        var waiting = f.Spread.Borrow(operation.Token);
        bool claimed = false;
        try
        {
            Check(!waiting.IsCompleted, "race borrower really waits behind the occupied one-slot pool");
            loan.Poison("isolated sharing fixture: unsuccessful data sequence");
            await loan.DisposeAsync();
            await using (var replacement = await waiting.WaitAsync(TimeSpan.FromSeconds(10), ct))
            {
                claimed = true;
                Check(!ReferenceEquals(original, replacement.Client), "poisoned loan is never handed to the waiting race");
                Check((await Noop(replacement, ct)).Code == "200",
                    "slot-freed wake creates a healthy native session through the normal borrow path");
                Check(f.Gate.Held == 2 && f.Spread.TotalCreated == 1 && f.Main.TotalCreated == 1,
                    "poison recovery holds two accounted permits while native quarantine drains separately");
            }
            await Until(() => f.Spread.QuarantineSize == 0, TimeSpan.FromSeconds(45), ct);
            Check(f.Spread.ActiveCount == 0 && f.Spread.QuarantineSize == 0,
                "poison recovery finishes deferred quarantine without an active-borrow leak");
        }
        finally
        {
            operation.Cancel();
            await loan.DisposeAsync();
            if (!await Settled(waiting, TimeSpan.FromSeconds(15)))
            {
                f.SuppressTeardown("Poison test borrower did not settle");
                throw new TimeoutException("Native teardown suppressed because a borrower is still active");
            }
            if (!claimed && waiting.IsCompletedSuccessfully) await waiting.Result.DisposeAsync();
        }
        await f.DisposeAsync();
        Check(f.Gate.Held == 0, "poison fixture disposal releases all accounted permits");
    }

    private static bool PendingSequence(AsyncFtpClient client) => CpsvDataHelper.HasPendingDataSequence(client);

    private static async Task<FtpReply> Noop(PooledConnection connection, CancellationToken ct)
    {
        try { return await connection.Client.Execute("NOOP", ct); }
        catch { connection.Poison("fixture NOOP failed or was cancelled"); throw; }
    }

    private static async Task Until(Func<bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > timeout) throw new TimeoutException("Fixture condition did not settle: " + timeout);
            await Task.Delay(30, ct);
        }
    }

    private static async Task<bool> Settled(Task task, TimeSpan timeout)
    {
        try { await task.WaitAsync(timeout); }
        catch (Exception) when (task.IsCompleted) { /* observed failure/cancellation, but it is settled */ }
        catch (TimeoutException) { return false; }
        return task.IsCompleted;
    }

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Hidden)!.GetValue(value)!;

    private sealed class Fixture : IAsyncDisposable
    {
        internal required string Root { get; init; }
        internal required string ServerId { get; init; }
        internal required Peer Peer { get; init; }
        internal required FtpConnectionPool Main { get; init; }
        internal required FtpConnectionPool Spread { get; init; }
        internal required ServerLoginGate Gate { get; init; }
        internal required SpreadManager Manager { get; init; }
        private int _disposed;
        private bool _suppressTeardown;

        internal void SuppressTeardown(string reason)
        {
            _suppressTeardown = true;
            File.WriteAllText(Path.Combine(Root, "native-teardown-suppressed.txt"), reason);
            Console.Error.WriteLine(reason + "; native handles retained until this isolated process exits. Evidence: " + Root);
        }

        internal static async Task<Fixture> Create(string evidence, string name, bool pauseFirstTransfers, CancellationToken ct, bool stalledSetup = false,
            IReadOnlyList<IPAddress>? networkAddresses = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "gldrive-sharing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var peer = new Peer(Path.Combine(evidence, name + "-protocol.json"), pauseFirstTransfers, stalledSetup, networkAddresses);
            var config = new ServerConfig
            {
                Id = "fixture-" + Guid.NewGuid().ToString("N"), Name = "same-host sharing fixture",
                Connection = new() { Host = "127.0.0.1", Port = peer.Port, Username = peer.Username }
            };
            var certs = new CertificateManager(Path.Combine(root, "trusted.json"));
            certs.TrustCertificate($"127.0.0.1:{peer.Port}", peer.Certificate.GetCertHashString(HashAlgorithmName.SHA256));
            var factory = new FtpClientFactory(config, certs);
            var gate = new ServerLoginGate(config.Id, 2, 2, reserved: 1);
            var main = new FtpConnectionPool(factory, 1, gate);
            var spread = new FtpConnectionPool(factory, 1, gate, priorityLogins: true);
            var appConfig = new AppConfig();
            appConfig.Servers.Add(config);
            var manager = (SpreadManager)Activator.CreateInstance(typeof(SpreadManager), Hidden, null, [appConfig, root], null)!;
            var fixture = new Fixture { Root = root, ServerId = config.Id, Peer = peer, Main = main, Spread = spread, Gate = gate, Manager = manager };
            try
            {
                await main.Initialize(ct);
                await spread.Initialize(ct);
                Field<Dictionary<string, FtpConnectionPool>>(manager, "_spreadPools").Add(config.Id, spread);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        internal void SetQueued(bool queued)
        {
            lock (Field<object>(Manager, "_lock"))
            {
                var requests = Field<List<PersistedRace>>(Manager, "_raceQueue");
                requests.Clear();
                if (queued) requests.Add(new PersistedRace("fixture", "synthetic-sharing-race", [ServerId],
                    SpreadMode.Race, ServerId, "/fixture", DateTime.UtcNow));
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_suppressTeardown) return;
            SetQueued(false);
            Field<Dictionary<string, FtpConnectionPool>>(Manager, "_spreadPools").Clear();
            Manager.Dispose();
            await Main.DisposeAsync();
            await Spread.DisposeAsync();
            await Peer.DisposeAsync();
            // Leave this unique temporary directory for inspection; it never contains app data.
        }
    }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _workers = [];
        private readonly ConcurrentQueue<object> _events = [];
        private readonly Task _accept;
        private readonly bool _pause;
        private readonly bool _stalledSetup;
        private readonly string _evidence;
        private readonly IPAddress _dataAddress;
        private readonly IReadOnlyList<IPAddress>? _networkAddresses;
        private readonly ConcurrentDictionary<int, byte> _observedSources = new();
        private int _sessionId, _authenticated, _liveAuthenticated, _maximum, _doubleAbort;
        internal string Username { get; } = "fixture-" + Guid.NewGuid().ToString("N");
        internal byte[] Payload { get; }
        internal IEnumerable<int> ObservedSources => _observedSources.Keys;
        internal X509Certificate2 Certificate { get; }
        internal int Port { get; }
        internal int Authenticated => Volatile.Read(ref _authenticated);
        internal int MaxLiveAuthenticated => Volatile.Read(ref _maximum);
        internal int DoubleAbortReplies => Volatile.Read(ref _doubleAbort);
        internal TaskCompletionSource FirstMainData { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstLoanData { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondLoanData { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource LoanSetupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource LoanSetupClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseMain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Peer(string evidence, bool pauseFirstTransfers, bool stalledSetup, IReadOnlyList<IPAddress>? networkAddresses = null)
        {
            _evidence = evidence;
            _pause = pauseFirstTransfers;
            _stalledSetup = stalledSetup;
            _networkAddresses = networkAddresses;
            Payload = Enumerable.Range(0, (networkAddresses == null ? 2 : 16) * 1024 * 1024).Select(i => (byte)(i * 17 + i / 251)).ToArray();
            _dataAddress = networkAddresses?.FirstOrDefault() ?? NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .OrderBy(n => n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || n.Name.StartsWith("vEthernet") ? 0 : 1)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
                .FirstOrDefault(IsPrivateV4) ?? throw new InvalidOperationException(
                    "Native CPSV verification needs a local RFC1918 IPv4 interface; product loopback-target guard is intentionally unchanged.");
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            Certificate = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accept = Accept();
        }

        private static bool IsPrivateV4(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = address.GetAddressBytes();
            return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168;
        }

        private void Trace(int session, string kind, string value) => _events.Enqueue(new
        {
            atUtc = DateTime.UtcNow, session, kind,
            value = _networkAddresses != null && kind == "reply" && value.StartsWith("227 ") ? "227 [same-host data endpoint]" : value
        });

        private async Task Accept()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                    if (!IPAddress.IsLoopback(((IPEndPoint)tcp.Client.RemoteEndPoint!).Address)) { tcp.Dispose(); continue; }
                    _workers.Add(Control(tcp, Interlocked.Increment(ref _sessionId)));
                }
            }
            catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or SocketException) { }
        }

        private async Task Control(TcpClient tcp, int session)
        {
            using (tcp)
            using (var sendGate = new SemaphoreSlim(1))
            {
                Stream stream = tcp.GetStream();
                StreamReader reader = new(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                StreamWriter writer = new(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
                TcpListener? data = null;
                bool loggedIn = false, tls = false, usernameAccepted = false;
                long offset = 0;
                int transfers = 0;
                async Task Reply(string line)
                {
                    await sendGate.WaitAsync(_stop.Token);
                    try { Trace(session, "reply", line); await writer.WriteLineAsync(line); }
                    finally { sendGate.Release(); }
                }
                try
                {
                    await Reply("220 Isolated CPSV sharing fixture");
                    while (await reader.ReadLineAsync(_stop.Token) is { } command)
                    {
                        var split = command.Split(' ', 2);
                        var verb = split[0].ToUpperInvariant();
                        var value = split.Length == 2 ? split[1] : "";
                        Trace(session, "command", verb is "USER" or "PASS" ? verb + " [fixture credential]" : command);
                        if (verb == "AUTH")
                        {
                            await Reply("234 Proceed with TLS");
                            reader.Dispose(); writer.Dispose();
                            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                            { ServerCertificate = Certificate, EnabledSslProtocols = SslProtocols.Tls12 }, _stop.Token);
                            stream = ssl;
                            reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
                            writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true, NewLine = "\r\n" };
                            tls = true;
                            continue;
                        }
                        if (verb == "USER") { usernameAccepted = tls && value == Username; await Reply(usernameAccepted ? "331 Password required" : "530 Rejected"); continue; }
                        if (verb == "PASS")
                        {
                            if (!tls || !usernameAccepted || loggedIn || value.Length != 0) { await Reply("530 Fixture credentials required"); continue; }
                            loggedIn = true;
                            Interlocked.Increment(ref _authenticated);
                            var live = Interlocked.Increment(ref _liveAuthenticated);
                            int previous;
                            do { previous = Volatile.Read(ref _maximum); } while (live > previous && Interlocked.CompareExchange(ref _maximum, live, previous) != previous);
                            await Reply("230 Fixture login accepted"); continue;
                        }
                        if (!loggedIn && verb is not ("FEAT" or "SYST")) { await Reply("530 Login required"); continue; }
                        switch (verb)
                        {
                            case "FEAT": await Reply("211-Features"); await Reply(" CPSV"); await Reply(" SIZE"); await Reply(" MDTM"); await Reply(" REST STREAM"); await Reply(" UTF8"); await Reply("211 End"); break;
                            case "SYST": await Reply("215 UNIX Type: L8"); break;
                            case "PWD": await Reply("257 \"/\" is current directory"); break;
                            case "SIZE": await Reply("213 " + Payload.Length); break;
                            case "MDTM": await Reply("213 20261015000000"); break;
                            case "REST": offset = long.Parse(value); await Reply("350 Restart accepted"); break;
                            case "CPSV":
                                data?.Stop();
                                data = new TcpListener(_dataAddress, 0); data.Start(1);
                                var p = ((IPEndPoint)data.LocalEndpoint).Port;
                                await Reply($"227 Entering Passive Mode ({_dataAddress.ToString().Replace('.', ',')},{p / 256},{p % 256})");
                                break;
                            case "RETR":
                                if (data == null || value != "/payload.bin" || offset < 0 || offset > Payload.Length) { await Reply("550 No such synthetic file"); break; }
                                await Reply("150 Opening synthetic data");
                                var listener = data; data = null;
                                _workers.Add(SendData(listener, session, ++transfers, offset, Reply)); offset = 0;
                                break;
                            case "QUIT": await Reply("221 Goodbye"); return;
                            default: await Reply("200 OK"); break;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or AuthenticationException) { Trace(session, "control-ended", ex.GetType().Name); }
                finally
                {
                    data?.Stop(); reader.Dispose(); writer.Dispose(); await stream.DisposeAsync();
                    if (loggedIn) Interlocked.Decrement(ref _liveAuthenticated);
                }
            }
        }

        private async Task SendData(TcpListener listener, int session, int transfer, long offset, Func<string, Task> reply)
        {
            bool complete = false;
            try
            {
                using var tcp = await listener.AcceptTcpClientAsync(_stop.Token);
                listener.Stop();
                var sourceAddress = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
                var sourceIndex = _networkAddresses?.ToList().FindIndex(address => address.Equals(sourceAddress)) ?? (sourceAddress.Equals(_dataAddress) ? 0 : -1);
                if (sourceIndex < 0)
                    throw new IOException("Data peer is not the selected same-host address");
                _observedSources.TryAdd(sourceIndex, 0);
                if (_stalledSetup && session == 2 && transfer == 1)
                {
                    Trace(session, "data", "TLS setup deliberately held before ClientHello");
                    LoanSetupEntered.TrySetResult();
                    var probe = new byte[1];
                    try
                    {
                        if (await tcp.GetStream().ReadAsync(probe, _stop.Token) != 0)
                            throw new IOException("Unexpected bytes from the CPSV TLS server while waiting for ClientHello");
                    }
                    finally { LoanSetupClosed.TrySetResult(); }
                    throw new IOException("Yield closed the deliberately stalled TLS setup socket");
                }
                // Only the application's private fixture endpoint has this generated certificate.
                var lazy = typeof(CpsvDataHelper).GetField("SelfSignedCert", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                var expected = ((X509Certificate2)lazy.GetType().GetProperty("Value")!.GetValue(lazy)!).GetCertHashString(HashAlgorithmName.SHA256);
                using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert != null &&
                    Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())) == expected);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                { TargetHost = "CPSV application peer", EnabledSslProtocols = SslProtocols.Tls12 }, _stop.Token);
                Trace(session, "data", $"RETR #{transfer} offset={offset}, TLS={ssl.SslProtocol}, same-host peer verified");
                var first = (int)Math.Min(8192, Payload.Length - offset);
                if (first > 0) await ssl.WriteAsync(Payload.AsMemory((int)offset, first), _stop.Token);
                offset += first;
                if (session == 1 && transfer == 1) FirstMainData.TrySetResult();
                if (session == 2 && transfer == 1) FirstLoanData.TrySetResult();
                if (session == 2 && transfer == 2 || _stalledSetup && session == 3 && transfer == 1) SecondLoanData.TrySetResult();
                if (_pause && transfer == 1)
                {
                    if (session == 1 || _networkAddresses != null) await ReleaseMain.Task.WaitAsync(_stop.Token);
                    else if (session == 2)
                    {
                        var probe = new byte[1];
                        if (await ssl.ReadAsync(probe, _stop.Token) != 0)
                            throw new IOException("Unexpected client bytes on a RETR data socket");
                        throw new IOException("Synthetic loan observed peer's early data close");
                    }
                }
                while (offset < Payload.Length)
                {
                    var count = (int)Math.Min(8192, Payload.Length - offset);
                    await ssl.WriteAsync(Payload.AsMemory((int)offset, count), _stop.Token);
                    offset += count;
                    await Task.Delay(1, _stop.Token);
                }
                complete = true;
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or AuthenticationException)
            { Trace(session, "data-ended", ex.GetType().Name + ": " + ex.Message); }
            finally { listener.Stop(); }
            if (_stop.IsCancellationRequested) return;
            if (complete) await reply("226 Synthetic transfer complete");
            else
            {
                Interlocked.Increment(ref _doubleAbort);
                // Both replies precede any following NOOP response, as one protocol sequence.
                await reply("426 Synthetic early data close\r\n226 Synthetic abort cleanup complete");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop(); ReleaseMain.TrySetResult();
            await _accept;
            try { await Task.WhenAll(_workers.ToArray()).WaitAsync(TimeSpan.FromSeconds(10)); }
            finally
            {
                File.WriteAllText(_evidence, JsonSerializer.Serialize(new
                {
                    controlAddress = "127.0.0.1", controlPort = Port,
                    dataAddress = _networkAddresses == null ? _dataAddress.ToString() : null, sameHostDataPeersOnly = true,
                    observedSourceCount = _observedSources.Count,
                    authenticatedSessions = Authenticated, maximumPhysicalSessions = MaxLiveAuthenticated,
                    remainingPhysicalSessions = Volatile.Read(ref _liveAuthenticated),
                    doubleAbortReplies = DoubleAbortReplies, payloadSha256 = Convert.ToHexString(SHA256.HashData(Payload)), events = _events
                }, new JsonSerializerOptions { WriteIndented = true }));
                Certificate.Dispose(); _stop.Dispose();
            }
        }
    }
}
