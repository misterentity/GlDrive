using System.IO;
using FluentFTP;

namespace GlDrive.Ftp;

/// <summary>
/// The server answered a data command (LIST / RETR / STOR) with something other
/// than a 1xx "transfer starting" reply. <see cref="ControlChannelInSync"/> says
/// whether that reply was a FINAL negative completion — in which case no transfer
/// began, no 226 will follow, and the control channel is immediately reusable —
/// or a reply that means the connection must not be trusted again (a 421 closing
/// the session, or a non-negative code that is the desync signature: a stale
/// reply from an earlier command).
/// </summary>
public sealed class DataCommandRejectedException : IOException
{
    public DataCommandRejectedException(string verb, FtpReply reply, bool controlChannelInSync)
        : base($"{verb} failed: {reply.Code} {reply.Message}")
    {
        Verb = verb;
        Code = reply.Code ?? "";
        ControlChannelInSync = controlChannelInSync;
    }

    public string Verb { get; }
    public string Code { get; }
    public bool ControlChannelInSync { get; }
}

public static class FtpCommandRejection
{
    /// <summary>
    /// True when <paramref name="code"/> is a final negative completion (4xx/5xx)
    /// that leaves the control channel in sync. 421 is excluded: the server is
    /// closing the control connection, so the session is gone regardless.
    /// </summary>
    public static bool LeavesChannelInSync(string? code)
        => code is { Length: 3 } && (code[0] == '4' || code[0] == '5') && code != "421";

    /// <summary>
    /// True when the failure was a clean command rejection that left the borrowed
    /// connection healthy — the caller must not poison it. Everything else
    /// (transport faults, timeouts, cancellation mid-read, a rejection that closed
    /// the session) keeps the conservative poison-on-failure behaviour.
    /// </summary>
    public static bool IsClean(Exception ex)
        => ex is DataCommandRejectedException { ControlChannelInSync: true };
}
