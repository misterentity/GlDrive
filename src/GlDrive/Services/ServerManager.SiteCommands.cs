using GlDrive.Ftp;
using GlDrive.Irc;
using Serilog;

namespace GlDrive.Services;

public sealed record SiteCommandReply(bool Success, int Code, string Message);

public partial class ServerManager
{
    /// <summary>
    /// Send one glftpd addline to a mounted site, the way SITE INVITE is sent: the main pool,
    /// falling back to the live spread pool when the main login is busy. The connection goes
    /// back to the pool: these commands change no session state.
    /// </summary>
    public async Task<SiteCommandReply> SendSiteAddlineAsync(string serverId, SiteAddlineCommand addline, CancellationToken ct)
    {
        // Re-validate here so nothing but the three addline verbs can reach a site via this path.
        if (!SiteAddline.TryParseLine(addline.Command, out var command, out var error))
            throw new ArgumentException(error);
        if (!_servers.TryGetValue(serverId, out var mount) || mount.Pool is not { } mainPool
            || mount.CurrentState != MountState.Connected)
            throw new InvalidOperationException("That site isn't connected. Mount it, then try again.");

        var name = _config.Servers.FirstOrDefault(s => s.Id == serverId)?.Name ?? serverId;
        var spreadPool = _spreadManager?.GetLivePool(serverId);
        await using var conn = await PoolFallback.BorrowAsync(
            t => mainPool.BorrowFor(FtpBorrowPurpose.SiteCommand, t),
            spreadPool == null ? null : t => spreadPool.BorrowFor(FtpBorrowPurpose.SiteCommand, t),
            SiteInviteMainPoolBudget, ct);
        var reply = await conn.Client.Execute(command.Command, ct);
        var message = string.Join(Environment.NewLine,
            reply.InfoMessages?.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0) ?? [])
            is { Length: > 0 } info ? info + Environment.NewLine + reply.Message : reply.Message;
        Log.Information("Addline {Command} sent to {Server}: {Code} {Message}",
            command.Redacted, name, reply.Code, reply.Message);
        return new SiteCommandReply(reply.Success, int.TryParse(reply.Code, out var code) ? code : 0, message);
    }
}
