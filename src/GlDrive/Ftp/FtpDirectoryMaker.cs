using FluentFTP;

namespace GlDrive.Ftp;

/// <summary>
/// Makes FTP directories the way glftpd accepts them. Callers pass the command executor
/// (normally <c>AsyncFtpClient.Execute</c>) and keep their own failure bookkeeping — the race
/// engine's denial/blacklist handling stays in SpreadJob.
/// </summary>
public static class FtpDirectoryMaker
{
    /// <summary>
    /// cbftp's MKD response check: accept 257 / any 2xx as success, and also accept a
    /// failure reply whose text says the directory already exists (550 "File exists",
    /// 521 "already exists"). Different glftpd versions and themes phrase it differently.
    /// </summary>
    public static bool IsMkdSuccess(FtpReply reply)
    {
        if (reply.Success) return true;
        var msg = reply.Message ?? "";
        return msg.Contains("File exist", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("already exist", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ensures an absolute directory exists using cbftp-style navigation: CWD to the whole
    /// path first (cheap, idempotent); otherwise CWD to the root, then CWD into each
    /// component, issuing a RELATIVE MKD for any that doesn't exist yet. glftpd dirscripts
    /// and path-filters evaluate the new name relative to the CWD — an absolute
    /// "MKD /a/b/c" is widely rejected ("Denied by dirscript" / "Not allowed to make
    /// directories here") where the equivalent walk is allowed.
    ///
    /// Returns the reply code and text of the first failure. On success the message is
    /// "exists (CWD)" / "exists" when nothing was created, or "created". <paramref name="created"/>
    /// fires once per component this call actually created; a concurrent creator's
    /// "already exists" is navigable but never counts as ours.
    /// </summary>
    public static async Task<(bool Ok, string Code, string Message)> EnsurePath(
        Func<string, CancellationToken, Task<FtpReply>> execute,
        string fullPath, CancellationToken ct, Action<string>? created = null)
    {
        var sanitized = CpsvDataHelper.SanitizeFtpPath(fullPath);

        var whole = await execute($"CWD {sanitized}", ct);
        if (whole.Success) return (true, whole.Code ?? "250", "exists (CWD)");

        var parts = sanitized.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (false, "ERR", "empty path");

        var root = await execute("CWD /", ct);
        if (!root.Success) return (false, root.Code ?? "", root.Message ?? "CWD / failed");

        var createdAny = false;
        var currentPath = "";
        foreach (var part in parts)
        {
            currentPath += "/" + part;
            var cwd = await execute($"CWD {part}", ct);
            if (cwd.Success) continue;

            var mkd = await execute($"MKD {part}", ct);
            if (!IsMkdSuccess(mkd))
                return (false, mkd.Code ?? "", mkd.Message ?? "(no reply text)");
            if (mkd.Success && !(mkd.Message ?? "").Contains("exist", StringComparison.OrdinalIgnoreCase))
            {
                createdAny = true;
                created?.Invoke(currentPath);
            }

            var cwd2 = await execute($"CWD {part}", ct);
            if (!cwd2.Success)
                return (false, cwd2.Code ?? "", cwd2.Message ?? "CWD after MKD failed");
        }
        return (true, createdAny ? "257" : "250", createdAny ? "created" : "exists");
    }
}
