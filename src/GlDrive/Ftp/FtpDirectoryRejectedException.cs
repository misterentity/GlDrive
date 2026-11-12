using System.IO;

namespace GlDrive.Ftp;

/// <summary>A completed directory-navigation failure, retaining its actual FTP reply.</summary>
public sealed class FtpDirectoryRejectedException(string path, string? code, string? replyMessage)
    : IOException($"MKD failed for {path}: {code} {replyMessage}".TrimEnd())
{
    public string Code { get; } = code ?? "";
    public string ReplyMessage { get; } = replyMessage ?? "";
    public bool ControlChannelInSync => IsSynchronizedRejection(Code);

    internal static bool IsSynchronizedRejection(string? code) =>
        code is { Length: 3 } && code.All(char.IsAsciiDigit) &&
        FtpCommandRejection.LeavesChannelInSync(code);
}
