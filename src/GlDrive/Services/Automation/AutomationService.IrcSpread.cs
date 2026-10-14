using System.IO;
using System.Text.Json;
using FluentFTP;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Spread;

namespace GlDrive.Services.Automation;

public sealed partial class AutomationService
{
    private async Task<object?> IrcAsync(string op, JsonElement a, CancellationToken ct)
    {
        var id = Required(a, "serverId", 128);
        Server(id);
        if (op is "irc.start" or "irc.stop" or "irc.reconnect")
        {
            if (op is "irc.stop" or "irc.reconnect") await _servers.StopIrc(id);
            ct.ThrowIfCancellationRequested();
            if (op is "irc.start" or "irc.reconnect") await _servers.StartIrc(id);
            return new { serverId = id, state = _servers.GetIrcService(id)?.State.ToString() ?? "Disconnected" };
        }
        if (op == "irc.patterns") return _servers.DetectIrcPatterns(id);
        var irc = _servers.GetIrcService(id) ?? throw new InvalidOperationException("IRC service is not running.");
        if (op == "irc.status")
            return new
            {
                irc.ServerId, irc.ServerName, state = irc.State.ToString(), irc.CurrentNick,
                channels = irc.GetChannelSnapshot(),
                targets = irc.GetScrollbackTargets()
            };
        if (op == "irc.history")
            return irc.GetScrollback(Target(a)).TakeLast(Integer(a, "limit", 200, 1, 2000)).ToArray();
        if (op == "irc.keys_list")
            return irc.KeyStore.GetAllKeys().Select(k => new { target = k.Key, mode = k.Value.Mode.ToString(), k.Value.SetAt, k.Value.Manual }).ToArray();
        if (op == "irc.raw")
        {
            RequireIrcConnected(irc);
            await irc.SendRaw(Line(Required(a, "line", 500), "line"));
            return new { sent = true };
        }
        var target = Target(a);
        switch (op)
        {
            case "irc.key_get":
                var entry = irc.KeyStore.GetKey(target) ?? throw new KeyNotFoundException("No FiSH key for this target.");
                return new { target, entry.Key, mode = entry.Mode.ToString(), entry.SetAt, entry.Manual };
            case "irc.key_set":
                irc.SetFishKey(target, Line(Required(a, "key", 1024), "key"), EnumValue(a, "mode", FishMode.CBC));
                return new { target, stored = true };
            case "irc.key_remove":
                if (irc.KeyStore.GetKey(target) == null) throw new KeyNotFoundException("No FiSH key for this target.");
                irc.RemoveFishKey(target);
                return new { target, removed = true };
            case "irc.key_mode":
                if (irc.KeyStore.GetKey(target) == null) throw new KeyNotFoundException("No FiSH key for this target.");
                Required(a, "mode", 16);
                irc.SetFishMode(target, EnumValue(a, "mode", FishMode.CBC));
                return new { target, updated = true };
        }
        RequireIrcConnected(irc);
        switch (op)
        {
            case "irc.send":
                if (!await irc.SendMessage(target, Line(Required(a, "text", 400), "text")))
                    throw new InvalidOperationException("IRC disconnected before the message could be sent.");
                break;
            case "irc.action": await irc.SendAction(target, Line(Required(a, "text", 400), "text")); break;
            case "irc.notice": await irc.SendNotice(target, Line(Required(a, "text", 400), "text")); break;
            case "irc.join":
                RequireChannel(target);
                await irc.JoinChannel(target, Optional(a, "key", 200) is { } key ? Line(key, "key") : null);
                break;
            case "irc.part":
                RequireChannel(target);
                await irc.PartChannel(target, Optional(a, "text", 400) is { } reason ? Line(reason, "text") : null);
                break;
            case "irc.topic":
                RequireChannel(target);
                await irc.SetTopic(target, Line(Optional(a, "text", 400) ?? throw new ArgumentException("'text' is required."), "text"));
                break;
            case "irc.key_exchange":
                if (target.StartsWith('#') || target.StartsWith('&')) throw new ArgumentException("DH1080 exchange requires a nickname, not a channel.");
                await irc.InitiateKeyExchange(target);
                break;
            default: throw new ArgumentException($"Unknown operation: {op}");
        }
        return new { target, sent = true };
    }

    private static void RequireIrcConnected(IrcService irc)
    {
        if (irc.State != IrcServiceState.Connected) throw new InvalidOperationException("IRC is not connected; the operation was not sent.");
    }
    private static void RequireChannel(string target)
    {
        if (!target.StartsWith('#') && !target.StartsWith('&')) throw new ArgumentException("This operation requires an IRC channel target (#channel or &channel).");
    }

    private async Task<object?> SpreadAsync(string op, JsonElement a, CancellationToken ct)
    {
        var spread = Spread;
        switch (op)
        {
            case "spread.status": return new
            {
                jobs = spread.ActiveJobs.Select(j => new { j.Id, j.ReleaseName, j.Section, state = j.State.ToString(), j.StartedAt, j.Score, j.LastError }).ToArray(),
                queued = spread.QueuedRaces, connectedServerIds = spread.GetConnectedServerIds(),
                pools = spread.PoolHealth(), summary = spread.History.Summarize()
            };
            case "spread.history": return Page(spread.History.Items.OrderByDescending(r => r.StartedAt).ToArray(), a);
            case "spread.blacklist_list": return spread.Blacklist.GetAll();
            case "spread.blacklist_clear": spread.Blacklist.Clear(); return new { cleared = true };
            case "spread.blacklist_remove":
                var server = Required(a, "serverId", 128);
                Server(server);
                var section = Required(a, "section", 128);
                if (!spread.Blacklist.Remove(server, section)) throw new KeyNotFoundException("No exclusion exists for that server/section.");
                return new { removed = true };
            case "spread.queue_remove":
                if (!spread.RemoveQueuedRace(Required(a, "section", 128), Required(a, "releaseName", 512)))
                    throw new KeyNotFoundException("No queued race matches that section/release.");
                return new { removed = true };
            case "spread.start": return StartRace(a);
            case "spread.fxp":
            {
                var source = Required(a, "sourceServerId", 128);
                var destination = Required(a, "destinationServerId", 128);
                Server(source); Server(destination);
                if (source == destination) throw new ArgumentException("FXP requires different source and destination servers.");
                var sourcePath = RemotePath(a, "sourcePath", false);
                var destinationPath = RemotePath(a, "destinationPath", false);
                if (Flag(a, "directory"))
                    return new { completed = true, filesCopied = await FxpDirectory(source, sourcePath, destination, destinationPath, 0, ct) };
                await spread.StartFxp(source, sourcePath, destination, destinationPath, ct);
                Mounted(destination).RefreshCache();
                return new { completed = true, filesCopied = 1 };
            }
        }
        var id = Required(a, "id", 128);
        var job = spread.ActiveJobs.FirstOrDefault(j => j.Id == id) ?? throw new KeyNotFoundException($"No active race: {id}");
        switch (op)
        {
            case "spread.detail": return job.GetDetail();
            case "spread.stop": spread.StopJob(id); break;
            case "spread.pause": job.Pause(); break;
            case "spread.resume": job.Resume(); break;
            default: throw new ArgumentException($"Unknown operation: {op}");
        }
        return new { id, state = job.State.ToString() };
    }

    private object StartRace(JsonElement a)
    {
        var section = Line(Required(a, "section", 128), "section");
        var release = Line(Required(a, "releaseName", 512), "releaseName");
        if (release is "." or ".." || release.Contains('/') || release.Contains('\\'))
            throw new ArgumentException("'releaseName' must be a single directory name.");
        var connected = Spread.GetConnectedServerIds().ToHashSet(StringComparer.Ordinal);
        List<string> ids;
        if (a.TryGetProperty("serverIds", out var supplied))
        {
            if (supplied.ValueKind != JsonValueKind.Array || supplied.GetArrayLength() > 100)
                throw new ArgumentException("'serverIds' must be an array with at most 100 IDs.");
            ids = supplied.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new ArgumentException("Each server ID must be a string.")).Distinct().ToList();
        }
        else ids = _config.Servers.Where(s => s.Enabled && connected.Contains(s.Id) && s.SpreadSite.Sections.Count > 0).Select(s => s.Id).ToList();
        if (ids.Count < 2) throw new InvalidOperationException("A race needs at least two connected sites with configured sections.");
        foreach (var id in ids)
        {
            var server = Server(id);
            if (!server.Enabled || !connected.Contains(id)) throw new InvalidOperationException($"Server {id} must be enabled and have a connected spread pool.");
        }
        var source = Optional(a, "sourceServerId", 128);
        if (source != null && !ids.Contains(source)) throw new ArgumentException("'sourceServerId' must be a race participant.");
        var sourcePath = Optional(a, "sourcePath") == null ? null : RemotePath(a, "sourcePath", false);
        if (sourcePath != null && source == null) throw new ArgumentException("'sourcePath' requires sourceServerId.");
        var job = Spread.StartRace(section, release, ids, SpreadMode.Race, source, sourcePath);
        if (job != null) return new { state = "started", job.Id, job.ReleaseName, job.Section };
        if (Spread.QueuedRaces.Any(r => r.Section.Equals(section, StringComparison.OrdinalIgnoreCase) && r.ReleaseName.Equals(release, StringComparison.OrdinalIgnoreCase)))
            return new { state = "queued", section, releaseName = release };
        var active = Spread.ActiveJobs.FirstOrDefault(j => j.Section.Equals(section, StringComparison.OrdinalIgnoreCase) && j.ReleaseName.Equals(release, StringComparison.OrdinalIgnoreCase));
        if (active != null) return new { state = "already_active", active.Id, active.ReleaseName, active.Section };
        throw new InvalidOperationException("The race engine rejected the request (check rules, recent failures, and site availability).");
    }

    private async Task<int> FxpDirectory(string source, string sourcePath, string destination, string destinationPath, int depth, CancellationToken ct)
    {
        if (depth > 64) throw new IOException("Directory nesting exceeds 64 levels.");
        var src = Mounted(source);
        var dst = Mounted(destination);
        var entries = await src.Ftp!.ListDirectory(sourcePath, ct);
        await dst.Ftp!.CreateDirectory(destinationPath, ct);
        var copied = 0;
        foreach (var entry in entries.Where(e => e.Name is not ("." or "..")))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Name.Contains('/') || entry.Name.Contains('\\') || entry.Name.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new IOException("The FTP server returned an unsafe child name.");
            var srcPath = sourcePath.TrimEnd('/') + "/" + entry.Name;
            var dstPath = destinationPath.TrimEnd('/') + "/" + entry.Name;
            if (entry.Type == FtpObjectType.Directory)
                copied += await FxpDirectory(source, srcPath, destination, dstPath, depth + 1, ct);
            else if (entry.Type == FtpObjectType.File)
            {
                await Spread.StartFxp(source, srcPath, destination, dstPath, ct);
                copied++;
            }
        }
        dst.RefreshCache();
        return copied;
    }
}
