using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using MonoTorrent;
using MonoTorrent.Connections.Tracker;
using MonoTorrent.Messages.UdpTracker;
using MonoTorrent.Trackers;
using ReusableTasks;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using GlDrive.Player;
using Xunit;

namespace GlDrive.Tests;

public sealed class TorrentTrackerFallbackTests
{
    [Theory]
    [InlineData(TorrentEvent.None, 0)]
    [InlineData(TorrentEvent.Completed, 1)]
    [InlineData(TorrentEvent.Started, 2)]
    [InlineData(TorrentEvent.Stopped, 3)]
    public async Task Udp_events_encode_as_Bep15_without_mutating_the_tier_request(TorrentEvent clientEvent, int wireEvent)
    {
        var connection = new Connection(TrackerState.Ok, 1);
        var request = Request().WithClientEvent(clientEvent).WithPeerId(new byte[20])
            .WithBytesDownloaded(123).WithBytesUploaded(456).WithBytesLeft(789).WithKey(27);
        await Wrap(connection).AnnounceAsync(request, CancellationToken.None);
        Assert.Equal(clientEvent, request.ClientEvent);
        // Tracker itself assigns a per-tracker key; compare the real transport request.
        var expected = new AnnounceMessage(42, 12345, connection.LastRequest!.WithClientEvent(clientEvent), request.InfoHashes.V1OrV2, 49002).Encode().ToArray();
        BinaryPrimitives.WriteInt32BigEndian(expected.AsSpan(80), wireEvent);
        var actual = new AnnounceMessage(42, 12345, connection.LastRequest!, request.InfoHashes.V1OrV2, 49002).Encode().ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal(wireEvent, BinaryPrimitives.ReadInt32BigEndian(actual.AsSpan(80)));
        Assert.Equal(request.BytesDownloaded, connection.LastRequest.BytesDownloaded);
        Assert.Equal(request.BytesUploaded, connection.LastRequest.BytesUploaded);
        Assert.Equal(request.BytesLeft, connection.LastRequest.BytesLeft);
    }

    [Theory]
    [InlineData("http", TorrentEvent.None)]
    [InlineData("http", TorrentEvent.Completed)]
    [InlineData("http", TorrentEvent.Started)]
    [InlineData("http", TorrentEvent.Stopped)]
    [InlineData("https", TorrentEvent.None)]
    [InlineData("https", TorrentEvent.Completed)]
    [InlineData("https", TorrentEvent.Started)]
    [InlineData("https", TorrentEvent.Stopped)]
    public async Task Http_event_requests_are_unchanged(string scheme, TorrentEvent clientEvent)
    {
        var connection = new Connection(TrackerState.Ok, 1, scheme);
        var request = Request().WithClientEvent(clientEvent);
        await Wrap(connection).AnnounceAsync(request, CancellationToken.None);
        // The underlying Tracker may copy the request to attach its key.
        Assert.Equal(clientEvent, request.ClientEvent);
        Assert.Equal(clientEvent, connection.LastRequest!.ClientEvent);
    }

    [Fact]
    public async Task Real_udp_wire_interval_controls_the_ordinary_announce_schedule()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var uri = new Uri($"udp://127.0.0.1:{((IPEndPoint)listener.Client.LocalEndPoint!).Port}/announce");
        int announces = 0;
        var serving = Serve();
        try
        {
            var factories = Factories.Default.WithTrackerCreator("udp", address => new Tracker(
                new UdpTrackerConnection(address, AddressFamily.InterNetwork) { RetryDelay = TimeSpan.FromMilliseconds(100) }));
            var tracker = TorrentTrackerFactories.Create(factories).CreateTracker(uri)!;
            var tier = Tier(tracker);
            await Announce(tier, TorrentEvent.Started, deadline.Token);
            Assert.True(tier.LastAnnounceSucceeded);
            Assert.Equal(TimeSpan.FromSeconds(37), tracker.MinUpdateInterval);
            Assert.Equal(TimeSpan.FromSeconds(37), tracker.UpdateInterval);
            Assert.Equal(1, Volatile.Read(ref announces));
            await Announce(tier, TorrentEvent.None, deadline.Token);
            Advance(tier, TimeSpan.FromSeconds(36));
            await Announce(tier, TorrentEvent.None, deadline.Token);
            Assert.Equal(1, Volatile.Read(ref announces));
            Advance(tier, TimeSpan.FromSeconds(38));
            await Announce(tier, TorrentEvent.None, deadline.Token);
            Assert.Equal(2, Volatile.Read(ref announces));
        }
        finally
        {
            deadline.Cancel();
            await serving;
        }

        async Task Serve()
        {
            try
            {
                while (true)
                {
                    var message = await listener.ReceiveAsync(deadline.Token);
                    Assert.True(IPAddress.IsLoopback(message.RemoteEndPoint.Address));
                    var input = message.Buffer;
                    var action = BinaryPrimitives.ReadInt32BigEndian(input.AsSpan(8));
                    byte[] response;
                    if (action == 0)
                    {
                        response = new byte[16];
                        BinaryPrimitives.WriteInt64BigEndian(response.AsSpan(8), 12345);
                    }
                    else
                    {
                        Assert.Equal(1, action);
                        Assert.Equal(98, input.Length);
                        Interlocked.Increment(ref announces);
                        response = new byte[20];
                        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(8), 37);
                    }
                    BinaryPrimitives.WriteInt32BigEndian(response, action);
                    input.AsSpan(12, 4).CopyTo(response.AsSpan(4));
                    await listener.SendAsync(response, message.RemoteEndPoint, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }

    [Theory]
    [InlineData(TrackerState.Offline)]
    [InlineData(TrackerState.InvalidResponse)]
    public async Task Returned_failure_tries_healthy_next_tracker_and_retains_success_intervals(TrackerState failure)
    {
        var first = new Connection(failure, 1, "https");
        var second = new Connection(TrackerState.Ok, 2, "https")
        {
            Minimum = TimeSpan.FromSeconds(7), Interval = TimeSpan.FromSeconds(19)
        };
        var tier = Tier(Wrap(first), Wrap(second));

        await Announce(tier, TorrentEvent.Started);

        Assert.Equal(1, first.Announces);
        Assert.Equal(1, second.Announces);
        Assert.True(tier.LastAnnounceSucceeded);
        Assert.Equal(second.Uri, tier.ActiveTracker.Uri);
        Assert.Equal(second.Minimum, tier.ActiveTracker.MinUpdateInterval);
        Assert.Equal(second.Interval, tier.ActiveTracker.UpdateInterval);
        await Announce(tier, TorrentEvent.None);
        Assert.Equal(1, second.Announces);
        Advance(tier, TimeSpan.FromSeconds(20));
        await Announce(tier, TorrentEvent.None);
        Assert.Equal(1, first.Announces);
        Assert.Equal(2, second.Announces);
    }

    [Fact]
    public async Task All_failed_trackers_use_failure_interval_and_can_recover()
    {
        var first = new Connection(TrackerState.Offline, 1);
        var second = new Connection(TrackerState.InvalidResponse, 2);
        var tier = Tier(Wrap(first), Wrap(second));
        await Announce(tier, TorrentEvent.Started);
        Assert.False(tier.LastAnnounceSucceeded);
        Assert.Equal(1, first.Announces);
        Assert.Equal(1, second.Announces);
        await Announce(tier, TorrentEvent.None);
        Assert.Equal(1, first.Announces);
        Assert.Equal(1, second.Announces);

        first.State = TrackerState.Ok;
        Advance(tier, TimeSpan.FromMinutes(4));
        await Announce(tier, TorrentEvent.None);
        Assert.True(tier.LastAnnounceSucceeded);
        Assert.Equal(2, first.Announces);
        Assert.Equal(1, second.Announces);
    }

    [Fact]
    public async Task Successful_response_is_preserved_and_scrape_does_not_overwrite_announce_intervals()
    {
        var connection = new Connection(TrackerState.Ok, 1, "https")
        {
            Minimum = TimeSpan.FromSeconds(11), Interval = TimeSpan.FromSeconds(37)
        };
        var tracker = Wrap(connection);
        var response = await tracker.AnnounceAsync(Request(), CancellationToken.None);
        Assert.Same(connection.LastResponse, response);
        Assert.Single(response.Peers);
        Assert.Equal(connection.Minimum, tracker.MinUpdateInterval);
        Assert.Equal(connection.Interval, tracker.UpdateInterval);
        var scrape = await tracker.ScrapeAsync(new ScrapeRequest(Request().InfoHashes), CancellationToken.None);
        Assert.Same(connection.LastScrape, scrape);
        Assert.Equal(connection.Minimum, tracker.MinUpdateInterval);
        Assert.Equal(connection.Interval, tracker.UpdateInterval);
    }

    [Fact]
    public async Task Thrown_failure_still_falls_back_and_first_success_needs_no_fallback()
    {
        var first = new Connection(TrackerState.Ok, 1) { Throw = true };
        var second = new Connection(TrackerState.Ok, 2);
        var tier = Tier(Wrap(first), Wrap(second));
        await Announce(tier, TorrentEvent.Started);
        Assert.Equal(1, second.Announces);

        first = new Connection(TrackerState.Ok, 3);
        second = new Connection(TrackerState.Ok, 4);
        tier = Tier(Wrap(first), Wrap(second));
        await Announce(tier, TorrentEvent.Started);
        Assert.Equal(1, first.Announces);
        Assert.Equal(0, second.Announces);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    [InlineData("udp")]
    public async Task Factory_wraps_supported_protocol_without_changing_the_original_factory(string scheme)
    {
        var connection = new Connection(TrackerState.Offline, 1, scheme);
        var original = Factories.Default.WithTrackerCreator(scheme, _ => new Tracker(connection));
        var wrapped = TorrentTrackerFactories.Create(original).CreateTracker(connection.Uri)!;
        await Assert.ThrowsAsync<TrackerException>(() => wrapped.AnnounceAsync(Request(), CancellationToken.None).AsTask());
        var unchanged = original.CreateTracker(connection.Uri)!;
        Assert.Equal(TrackerState.Offline, (await unchanged.AnnounceAsync(Request(), CancellationToken.None)).State);
    }

    [Fact]
    public async Task Cancellation_before_call_does_not_announce()
    {
        var connection = new Connection(TrackerState.Ok, 1);
        var tracker = Wrap(connection);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tracker.AnnounceAsync(Request(), cancelled.Token).AsTask());
        Assert.Equal(0, connection.Announces);
    }

    [Theory]
    [InlineData(TrackerState.Ok)]
    [InlineData(TrackerState.Offline)]
    public async Task Cancellation_drains_ignored_token_transport_without_fallback_or_failure_log(TrackerState eventualResponse)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Connection(eventualResponse, 1) { Gate = gate.Task };
        var second = new Connection(TrackerState.Ok, 2);
        var sink = new Sink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var tier = Tier(Wrap(first, logger), Wrap(second, logger));
        using var stop = new CancellationTokenSource();
        var operation = Announce(tier, TorrentEvent.Started, stop.Token);
        Assert.Equal(1, first.Announces);
        stop.Cancel();
        Assert.False(operation.IsCompleted);
        gate.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, second.Announces);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task Failed_announce_log_and_exception_do_not_expose_tracker_uri_or_server_message()
    {
        var connection = new Connection(TrackerState.InvalidResponse, 1, "https")
        {
            Failure = "private-passkey and magnet:?xt=secret",
            Address = new Uri("https://private.example/secret-passkey/announce?token=private-token")
        };
        var sink = new Sink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var tracker = Wrap(connection, logger);
        var failure = await Assert.ThrowsAsync<TrackerException>(() => tracker.AnnounceAsync(Request(), CancellationToken.None).AsTask());
        var entry = Assert.Single(sink.Events);
        Assert.Null(entry.Exception);
        Assert.Equal(new[] { "Scheme", "State" }, entry.Properties.Keys.OrderBy(k => k));
        var text = entry.RenderMessage() + failure.ToString();
        Assert.Contains("https", text);
        Assert.Contains("InvalidResponse", text);
        Assert.DoesNotContain("private", text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("magnet", text);
    }

    private static AnnounceRequest Request() => new AnnounceRequest(InfoHashes.FromV1(InfoHash.FromMemory(new byte[20])))
        .WithPeerId(new byte[20]).WithReportedEndPointFunc(_ => (null, 49002));
    private static ITracker Wrap(Connection connection, ILogger? logger = null) =>
        TorrentTrackerFactories.Create(Factories.Default.WithTrackerCreator(connection.Uri.Scheme,
            _ => new Tracker(connection)), logger).CreateTracker(connection.Uri)!;

    // Only the test controls internal tier ordering/timers; production uses supported factories.
    private static TrackerTier Tier(params ITracker[] trackers)
    {
        var tier = (TrackerTier)typeof(TrackerTier).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(ITracker)], null)!.Invoke([trackers[0]]);
        typeof(TrackerTier).GetProperty(nameof(TrackerTier.Trackers))!.SetValue(tier, Array.AsReadOnly(trackers));
        return tier;
    }
    private static async Task Announce(TrackerTier tier, TorrentEvent evt, CancellationToken token = default)
    {
        var method = typeof(TrackerTier).GetMethod("AnnounceAsync", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(AnnounceRequest), typeof(CancellationToken)], null)!;
        await (ReusableTask)method.Invoke(tier, [Request().WithClientEvent(evt), token])!;
    }
    private static void Advance(TrackerTier tier, TimeSpan elapsed) =>
        typeof(TrackerTier).GetProperty(nameof(TrackerTier.TimeSinceLastAnnounce))!.SetValue(tier, elapsed);

    private sealed class Sink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed class Connection(TrackerState state, int number, string scheme = "udp") : ITrackerConnection
    {
        public bool CanScrape => true;
        public Uri? Address { get; init; }
        public Uri Uri => Address ?? new Uri($"{scheme}://127.0.0.1:{40000 + number}/announce");
        public TrackerState State { get; set; } = state;
        public int Announces { get; private set; }
        public bool Throw { get; init; }
        public Task? Gate { get; init; }
        public string Failure { get; init; } = "Synthetic returned failure";
        public TimeSpan Minimum { get; init; } = TimeSpan.FromMinutes(3);
        public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(30);
        public AnnounceResponse? LastResponse { get; private set; }
        public AnnounceRequest? LastRequest { get; private set; }
        public ScrapeResponse? LastScrape { get; private set; }
        public async ReusableTask<AnnounceResponse> AnnounceAsync(AnnounceRequest request, CancellationToken token)
        {
            Announces++;
            LastRequest = request;
            if (Gate != null) await Gate; // Deliberately models the pinned UDP token limitation.
            if (Throw) throw new IOException("Synthetic thrown failure");
            return LastResponse = new AnnounceResponse(State,
                State == TrackerState.Ok ? new Dictionary<InfoHash, IList<PeerInfo>>
                {
                    [request.InfoHashes.V1OrV2] = new[] { new PeerInfo(new Uri("ipv4://127.0.0.1:49001")) }
                } : null, Minimum, Interval, failureMessage: State == TrackerState.Ok ? "" : Failure);
        }
        public async ReusableTask<ScrapeResponse> ScrapeAsync(ScrapeRequest request, CancellationToken token)
        {
            await Task.CompletedTask;
            return LastScrape = new ScrapeResponse(TrackerState.Ok);
        }
    }
}
