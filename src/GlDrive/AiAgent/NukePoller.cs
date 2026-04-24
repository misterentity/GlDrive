using System.IO;
using System.Text.Json;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class NukePoller : IDisposable
{
    private readonly TelemetryRecorder _recorder;
    private readonly Services.ServerManager _servers;
    private readonly NukeCursorStore _cursors;
    private readonly string _aiDataRoot;
    private readonly int _intervalHours;
    private readonly Timer _timer;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _failCount = new();
    private const int BreakerThreshold = 3;

    public NukePoller(TelemetryRecorder recorder, Services.ServerManager servers,
                      NukeCursorStore cursors, string aiDataRoot, int intervalHours)
    {
        _recorder = recorder;
        _servers = servers;
        _cursors = cursors;
        _aiDataRoot = aiDataRoot;
        _intervalHours = Math.Max(1, intervalHours);

        // First fire 5 minutes after startup; subsequent every interval hours.
        _timer = new Timer(async _ =>
        {
            try { await PollAllAsync(); }
            catch (Exception ex) { Log.Error(ex, "NukePoller.PollAllAsync unhandled"); }
        }, null, TimeSpan.FromMinutes(5), TimeSpan.FromHours(_intervalHours));
    }

    public async Task PollAllAsync()
    {
        foreach (var ms in _servers.GetAllMountServices())
        {
            var serverId = ms.ServerId;
            if (_failCount.TryGetValue(serverId, out var f) && f >= BreakerThreshold)
            {
                Log.Debug("NukePoller circuit open for {Server}", serverId);
                continue;
            }
            try
            {
                var pool = ms.Pool;
                if (pool is null || pool.IsExhausted) continue;

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await using var lease = await pool.Borrow(cts.Token);
                if (lease?.Client is null) { BumpFail(serverId); continue; }
                var client = lease.Client;

                var reply = await client.Execute("SITE NUKES");
                if (!reply.Success) { BumpFail(serverId); continue; }

                // Parse. glftpd returns multi-line output in InfoMessages.
                var raw = reply.InfoMessages ?? reply.Message ?? "";
                var nukes = NukeParser.Parse(raw).ToList();

                var cursor = _cursors.Get(serverId);
                var newCursor = cursor;

                foreach (var n in nukes)
                {
                    if (n.NukedAt <= cursor) continue;
                    var ourRef = TryCorrelateRace(n.Release);
                    _recorder.Record(TelemetryStream.Nukes, new NukeDetectedEvent
                    {
                        ServerId = serverId,
                        Section = n.Section,
                        Release = n.Release,
                        NukedAt = n.NukedAt.ToString("O"),
                        Nuker = n.Nuker,
                        Reason = n.Reason,
                        Multiplier = n.Multiplier,
                        OurRaceRef = ourRef
                    });
                    if (n.NukedAt > newCursor) newCursor = n.NukedAt;
                }
                if (newCursor > cursor) _cursors.Set(serverId, newCursor);
                _failCount[serverId] = 0;  // reset breaker on success
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "NukePoller failed for {Server}", ms.ServerId);
                BumpFail(serverId);
            }
        }
    }

    private void BumpFail(string serverId)
    {
        var newCount = _failCount.AddOrUpdate(serverId, 1, (_, current) => current + 1);
        if (newCount == BreakerThreshold)
            Log.Warning("NukePoller breaker opened for {Server} after {N} failures", serverId, BreakerThreshold);
    }

    /// <summary>Scan today's races jsonl for a matching release name; return the raceId if found.</summary>
    private string? TryCorrelateRace(string release)
    {
        try
        {
            var racesFile = Path.Combine(_aiDataRoot, $"races-{DateTime.Now:yyyyMMdd}.jsonl");
            if (!File.Exists(racesFile)) return null;
            foreach (var line in File.ReadLines(racesFile))
            {
                if (!line.Contains($"\"release\":\"{release}\"", StringComparison.Ordinal)) continue;
                RaceOutcomeEvent? r;
                try { r = JsonSerializer.Deserialize<RaceOutcomeEvent>(line); }
                catch { continue; }
                if (r?.Release == release) return r.RaceId;
            }
        }
        catch { /* best-effort */ }
        return null;
    }

    public void Dispose() => _timer.Dispose();
}
