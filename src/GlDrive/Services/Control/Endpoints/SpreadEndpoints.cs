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
        spread.StopJob(id);
        return r.RespondAsync(200, new { stopped = id });
    }

    private async Task StartRace(ControlRequest r)
    {
        var body = await r.ReadBodyAsync();

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
