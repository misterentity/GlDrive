using System.IO;
using FluentFTP;
using FluentFTP.Exceptions;

namespace GlDrive.Filesystem;

public static class NtStatusMapper
{
    // Common NTSTATUS values
    public const int STATUS_SUCCESS = 0;
    public const int STATUS_NOT_IMPLEMENTED = unchecked((int)0xC0000002);
    public const int STATUS_OBJECT_NAME_NOT_FOUND = unchecked((int)0xC0000034);
    public const int STATUS_OBJECT_NAME_COLLISION = unchecked((int)0xC0000035);
    public const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);
    public const int STATUS_OBJECT_PATH_NOT_FOUND = unchecked((int)0xC000003A);
    public const int STATUS_DISK_FULL = unchecked((int)0xC000007F);
    public const int STATUS_CONNECTION_ABORTED = unchecked((int)0xC0000241);
    public const int STATUS_IO_TIMEOUT = unchecked((int)0xC00000B5);
    public const int STATUS_LOGON_FAILURE = unchecked((int)0xC000006D);
    public const int STATUS_HOST_UNREACHABLE = unchecked((int)0xC000023D);
    public const int STATUS_DEVICE_NOT_READY = unchecked((int)0xC00000A3);
    public const int STATUS_END_OF_FILE = unchecked((int)0xC0000011);
    public const int STATUS_DIRECTORY_NOT_EMPTY = unchecked((int)0xC0000101);
    public const int STATUS_NOT_A_DIRECTORY = unchecked((int)0xC0000103);
    public const int STATUS_FILE_IS_A_DIRECTORY = unchecked((int)0xC00000BA);
    public const int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);
    public const int STATUS_NO_MORE_FILES = unchecked((int)0x80000006);
    public const int STATUS_INTERNAL_ERROR = unchecked((int)0xC00000E5);

    public static int MapException(Exception ex)
    {
        return ex switch
        {
            FtpAuthenticationException => STATUS_LOGON_FAILURE,
            FtpCommandException ftpEx => MapFtpCode(ftpEx.CompletionCode),
            TimeoutException => STATUS_IO_TIMEOUT,
            OperationCanceledException => STATUS_CONNECTION_ABORTED,
            IOException => STATUS_CONNECTION_ABORTED,
            _ => STATUS_INTERNAL_ERROR
        };
    }

    public static int MapFtpCode(string completionCode)
    {
        return completionCode switch
        {
            "421" => STATUS_HOST_UNREACHABLE,
            "425" => STATUS_CONNECTION_ABORTED,
            "426" => STATUS_CONNECTION_ABORTED,
            "450" => STATUS_ACCESS_DENIED,
            "451" => STATUS_INTERNAL_ERROR,
            "452" => STATUS_DISK_FULL,
            "500" => STATUS_NOT_IMPLEMENTED,
            "501" => STATUS_NOT_IMPLEMENTED,
            "530" => STATUS_LOGON_FAILURE,
            "550" => STATUS_OBJECT_NAME_NOT_FOUND,
            "552" => STATUS_DISK_FULL,
            "553" => STATUS_ACCESS_DENIED,
            _ => STATUS_INTERNAL_ERROR
        };
    }
}
