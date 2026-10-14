using System.IO;
using FluentFTP;
using FluentFTP.Exceptions;

namespace GlDrive.Ftp;

/// <summary>Do not interpret FluentFTP's absorbed LIST/MLSD failures as empty directories.</summary>
internal static class FtpListing
{
    internal static async Task<FtpListItem[]> GetListing(AsyncFtpClient client, string path, CancellationToken ct)
    {
        var previousReply = client.LastReply;
        FtpListItem[] items;
        try
        {
            // AllFiles does not request the follow-up MDTM/SIZE commands that can
            // replace a successful listing's completion reply with a metadata error.
            items = await client.GetListing(path, FtpListOption.AllFiles, ct);
        }
        catch (FtpCommandException ex)
        {
            throw new DataCommandRejectedException("LIST " + path,
                new FtpReply { Code = ex.CompletionCode ?? "", Message = ex.Message },
                FtpCommandRejection.LeavesChannelInSync(ex.CompletionCode));
        }
        ct.ThrowIfCancellationRequested();
        ValidateReply(LatestListingReply(client.LastReplies, previousReply), path);
        return items;
    }

    internal static FtpReply LatestListingReply(IEnumerable<FtpReply>? replies, FtpReply previous)
    {
        // FluentFTP 53 stores five replies newest first. FtpReply is a struct,
        // but its parsed strings preserve identity when copied into this history.
        // Stop at the pre-call reply so an absorbed passive-open failure cannot
        // reuse a successful LIST from an earlier invocation. New identical replies
        // have newly parsed code/message strings and are not mistaken for the boundary.
        if (replies != null)
            foreach (var reply in replies)
            {
                if (previous.Code != null && ReferenceEquals(reply.Code, previous.Code)
                    && ReferenceEquals(reply.Message, previous.Message)
                    && ReferenceEquals(reply.InfoMessages, previous.InfoMessages)
                    && ReferenceEquals(reply.Command, previous.Command))
                    break;
                // Listing completion may have no Command; accept that fresh reply.
                // Only navigation after a listing can be skipped. In particular an
                // EPSV/PASV reply must not fall back to an older successful listing.
                if (reply.Success && reply.Command is { } command &&
                    (command.Equals("PWD", StringComparison.OrdinalIgnoreCase)
                    || command.StartsWith("CWD ", StringComparison.OrdinalIgnoreCase)))
                    continue;
                return reply;
            }
        return default;
    }

    internal static void ValidateReply(FtpReply reply, string path)
    {
        if (reply.Code is "226" or "250") return;
        if (reply.Code is { Length: 3 } code && (code[0] == '4' || code[0] == '5'))
            throw new DataCommandRejectedException("LIST " + path, reply,
                FtpCommandRejection.LeavesChannelInSync(code));
        throw new IOException($"LIST {path} did not complete: {reply.Code} {reply.Message}");
    }
}
