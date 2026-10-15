using MonoTorrent;
using MonoTorrent.Connections.Tracker;
using MonoTorrent.Trackers;
using ReusableTasks;
using Serilog;

namespace GlDrive.Player;

/// <summary>Correct the pinned engine's tracker fallback, intervals and UDP wire events.</summary>
internal static class TorrentTrackerFactories
{
    internal static Factories Create(Factories? original = null, ILogger? logger = null)
    {
        var transportFactories = original ?? Factories.Default;
        var result = transportFactories;
        foreach (var scheme in new[] { "http", "https", "udp" })
        {
            result = result.WithTrackerCreator(scheme, uri =>
            {
                var tracker = transportFactories.CreateTracker(uri);
                // Preserve unsupported/invalid creator behavior without recursively
                // invoking the new factory or mutating the shared default factories.
                return tracker == null ? null! : new ResponseCheckedTracker(tracker, logger ?? Log.Logger);
            });
        }
        return result;
    }

    private sealed class ResponseCheckedTracker(ITracker inner, ILogger logger) : ITracker
    {
        private AnnounceResponse? _lastSuccessfulAnnounce;
        private bool IsUdp => inner.Uri.Scheme == "udp";
        public bool CanScrape => inner.CanScrape;
        // MonoTorrent 3.0.2 Tracker never updates its LastAnnounceResponse.
        // Retain the successful response's intervals instead of its constructor defaults.
        public TimeSpan MinUpdateInterval => _lastSuccessfulAnnounce?.MinUpdateInterval ?? inner.MinUpdateInterval;
        // Its UDP parser also passes the BEP 15 wire interval as the constructor's
        // minUpdateInterval argument, leaving UpdateInterval at the 30-minute default.
        public TimeSpan UpdateInterval => _lastSuccessfulAnnounce is { } response
            ? IsUdp ? response.MinUpdateInterval : response.UpdateInterval
            : inner.UpdateInterval;
        public TimeSpan TimeSinceLastAnnounce => inner.TimeSinceLastAnnounce;
        public TrackerState Status => inner.Status;
        public Uri Uri => inner.Uri;
        public string WarningMessage => inner.WarningMessage;
        public string FailureMessage => inner.FailureMessage;

        public async ReusableTask<AnnounceResponse> AnnounceAsync(AnnounceRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // Pinned AnnounceMessage writes the enum ordinal, but TorrentEvent uses
            // Started=1/Stopped=2/Completed=3. BEP 15 requires Completed=1/Started=2/
            // Stopped=3 (https://www.bittorrent.org/beps/bep_0015.html).
            // UDP's connection only serializes this value. Copy at the transport
            // boundary so TrackerTier retains the original lifecycle event.
            var transportRequest = IsUdp ? request.WithClientEvent(request.ClientEvent switch
            {
                TorrentEvent.Completed => (TorrentEvent)1,
                TorrentEvent.Started => (TorrentEvent)2,
                TorrentEvent.Stopped => (TorrentEvent)3,
                _ => request.ClientEvent
            }) : request;
            AnnounceResponse response;
            try { response = await inner.AnnounceAsync(transportRequest, token); }
            catch when (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
                throw;
            }
            // Some pinned UDP operations ignore the token while in flight. Keep
            // awaiting their completion; do not leave an abandoned network request.
            token.ThrowIfCancellationRequested();
            if (response.State != TrackerState.Ok)
            {
                // TrackerTier only tries its next tracker when this call throws.
                // Never log the URI, peer identities, server message or inner exception.
                logger.Warning("Torrent tracker announce failed ({Scheme}, {State})", Uri.Scheme, response.State);
                throw new TrackerException($"Tracker announce failed ({response.State}).");
            }
            _lastSuccessfulAnnounce = response;
            return response;
        }

        public ReusableTask<ScrapeResponse> ScrapeAsync(ScrapeRequest request, CancellationToken token) =>
            inner.ScrapeAsync(request, token);
    }
}
