using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GlDrive.Config;
using GlDrive.Spread;
using Serilog;

namespace GlDrive.Services.Control.Endpoints;

public sealed class SpreadEndpoints : IControlEndpoint
{
    /// <summary>Longest accepted `section`. A section key is a short word; this is slack.</summary>
    internal const int MaxSectionLength = 128;

    /// <summary>Longest accepted `release`. Scene names run ~100 chars; this is slack.</summary>
    internal const int MaxReleaseLength = 512;

    private readonly AppConfig _config;
    private readonly Func<SpreadManager?> _getSpread;

    public SpreadEndpoints(AppConfig config, Func<SpreadManager?> getSpread)
    {
        _config = config;
        _getSpread = getSpread;
    }

    public void Register(RouteTable routes)
    {
        routes.Map("GET", "/races", r => r.RespondAsync(200, Races()));
        routes.Map("GET", "/history", r => r.RespondAsync(200, History(r)));
        routes.Map("GET", "/races/{id}", RaceDetail);
        routes.Map("POST", "/races", StartRace);
        routes.Map("POST", "/races/{id}/stop", StopRace);
    }

    private object Races()
    {
        var spread = _getSpread();
        if (spread == null) return new { races = Array.Empty<object>() };
        return new
        {
            races = spread.ActiveJobs.Select(j => new
            {
                id = j.Id,
                release = j.ReleaseName,
                section = j.Section,
                state = j.State.ToString(),
                score = j.Score,
                startedAt = j.StartedAt,
                isAutoRace = j.IsAutoRace,
                sites = j.Sites.Values.Select(s => new { s.ServerName, s.FilesOwned, s.FilesTotal, s.IsSource })
            })
        };
    }

    private object History(ControlRequest r)
    {
        var limit = r.QueryInt("limit", fallback: 25, min: 1, max: 500);
        var spread = _getSpread();
        if (spread == null) return new { history = Array.Empty<object>() };
        return new { history = spread.History.Items.Take(limit) };
    }

    private Task RaceDetail(ControlRequest r)
    {
        var id = r.Param("id");
        var job = _getSpread()?.ActiveJobs.FirstOrDefault(j => j.Id == id);
        return job == null
            ? r.ErrorAsync(404, "not_found", "no such active race", id)
            : r.RespondAsync(200, job.GetDetail());
    }

    private Task StopRace(ControlRequest r)
    {
        var spread = _getSpread();
        if (spread == null)
            return r.ErrorAsync(503, "unavailable", "spread engine unavailable");

        var id = r.Param("id")!;

        // Confirm the race exists before claiming to have stopped it. StopJob is
        // fire-and-forget, so this used to answer 200 {"stopped": id} for an id that never
        // existed — while GET /races/{id} correctly 404'd the same id. A caller could not
        // tell a real stop from a no-op, and the two endpoints disagreed about reality.
        if (spread.ActiveJobs.All(j => j.Id != id))
            return r.ErrorAsync(404, "not_found", "no such active race", id);

        spread.StopJob(id);
        return r.RespondAsync(200, new { stopped = id });
    }

    private async Task StartRace(ControlRequest r)
    {
        var body = await r.ReadBodyAsync();
        if (body == null)
        {
            await r.ErrorAsync(413, "payload_too_large",
                $"request body exceeds {ControlRequest.MaxBodyBytes} bytes");
            return;
        }

        string? section, release;
        try
        {
            var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            section = doc.RootElement.TryGetProperty("section", out var s) ? s.GetString() : null;
            release = doc.RootElement.TryGetProperty("release", out var rel) ? rel.GetString() : null;
        }
        catch (JsonException ex)
        {
            await r.ErrorAsync(400, "bad_request", "invalid JSON body", ex.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(section) || string.IsNullOrWhiteSpace(release))
        {
            await r.ErrorAsync(400, "bad_request", "both 'section' and 'release' are required");
            return;
        }

        // Bound both fields BEFORE they reach the race engine or the log. A 2 MB section name
        // was accepted here and written to gldrive-{date}.log as one 2,000,172-byte line; the
        // log rolls at 10 MB keeping 3 files, so a handful of such calls erases every trace of
        // what the app was doing. Losing diagnostic history to log volume has already happened
        // twice in this project (v3.10.47, v3.10.54) — this is the same failure with a caller
        // holding the pen. A scene release name is ~100 chars and a section key is a short
        // word, so these limits are generous.
        if (section!.Length > MaxSectionLength)
        {
            await r.ErrorAsync(400, "bad_request",
                $"'section' exceeds {MaxSectionLength} characters", $"got {section.Length}");
            return;
        }
        if (release!.Length > MaxReleaseLength)
        {
            await r.ErrorAsync(400, "bad_request",
                $"'release' exceeds {MaxReleaseLength} characters", $"got {release.Length}");
            return;
        }

        var spread = _getSpread();
        if (spread == null)
        {
            await r.ErrorAsync(503, "unavailable", "spread engine unavailable");
            return;
        }

        var connected = spread.GetConnectedServerIds().ToHashSet(StringComparer.Ordinal);
        var serverIds = _config.Servers
            .Where(s => s.Enabled && connected.Contains(s.Id) && s.SpreadSite.Sections.Count > 0)
            .Select(s => s.Id).ToList();

        if (serverIds.Count < 2)
        {
            await r.ErrorAsync(409, "conflict", "need 2+ connected servers with sections configured",
                string.Join(", ", _config.Servers.Where(s => connected.Contains(s.Id)).Select(s => s.Name)));
            return;
        }

        try
        {
            var job = spread.StartRace(section!, release!, serverIds, SpreadMode.Race);
            if (job == null)
            {
                await r.ErrorAsync(409, "conflict", "race not started (queued or rejected)");
                return;
            }
            Log.Information("Control API started race {Id}: [{Section}] {Release}", job.Id, section, release);
            await r.RespondAsync(202, new { id = job.Id, release = job.ReleaseName, section = job.Section });
        }
        catch (Exception ex)
        {
            await r.ErrorAsync(500, "internal", ex.Message);
        }
    }
}
