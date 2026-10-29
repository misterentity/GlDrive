using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Services.Operations;
using GlDrive.Services.Statistics;

namespace GlDrive.Services;

public partial class ServerManager
{
    internal DossierLocalSnapshot ReadDossierSnapshot(string release)
    {
        var notices = _notificationStore.ReadDossierEvents(release);
        var rows = new List<DossierEvent>(notices.Events);
        var wishes = _wantedWishlist.ReadDiagnosticSnapshot();
        var partial = notices.Partial || wishes.Partial;
        var mounted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in _servers.Values.Take(64))
        {
            if (service.Downloads is not { } downloads) continue;
            mounted.Add(service.ServerId);
            var snapshot = downloads.Store.ReadDossierEvents(release);
            rows.AddRange(snapshot.Events); partial |= snapshot.Partial;
        }
        partial |= _servers.Count > 64;
        lock (_torrentDownloadLock)
            if (_torrentDownloads is { } torrents)
            { var snapshot = torrents.Store.ReadDossierEvents(release); rows.AddRange(snapshot.Events); partial |= snapshot.Partial; }
        if (_spreadManager is { } spread)
        { var races = spread.ReadDossierEvents(release); rows.AddRange(races.Events); partial |= races.Partial; }
        // Read unmounted queues without constructing DownloadStore (which owns a save timer and normalizes states).
        var budget = 8L * 1024 * 1024;
        var watch = Stopwatch.StartNew();
        foreach (var server in _config.Servers.Take(64))
        {
            if (mounted.Contains(server.Id)) continue;
            if (watch.ElapsedMilliseconds > 1500) { partial = true; break; }
            var id = server.Id;
            if (string.IsNullOrEmpty(id) || id.Length > 256 || id.IndexOfAny(['/', '\\', ':']) >= 0) { partial = true; continue; }
            var path = Path.Combine(ConfigManager.AppDataPath, "downloads-" + id + ".json");
            try
            {
                if (!File.Exists(path)) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { partial = true; continue; }
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 2 * 1024 * 1024 || stream.Length > budget) { partial = true; continue; }
                budget -= stream.Length;
                var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
                using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
                if (document.RootElement.ValueKind != JsonValueKind.Array) { partial = true; continue; }
                var count = 0;
                foreach (var row in document.RootElement.EnumerateArray())
                {
                    if (++count > 10_000 || rows.Count >= 1000) { partial = true; break; }
                    if (!string.Equals(TelemetryValue.Text(row, "releaseName", 1024), release, StringComparison.OrdinalIgnoreCase)) continue;
                    var time = TelemetryValue.Timestamp(row, "completedAt") ?? TelemetryValue.Timestamp(row, "startedAt") ?? TelemetryValue.Timestamp(row, "queuedAt");
                    if (time == null) { partial = true; continue; }
                    rows.Add(new(time.Value, "download-persisted", id,
                        $"Recorded status {TelemetryValue.Text(row, "status") ?? TelemetryValue.Integer(row, "status")?.ToString() ?? "unknown"}; persisted queue snapshot."));
                }
            }
            catch { partial = true; }
        }
        partial |= _config.Servers.Count > 64 || rows.Count > 1000;
        return new(rows.Take(1000).ToArray(), wishes.Items, partial, wishes.Partial);
    }
}
