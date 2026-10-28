using System.IO;

namespace GlDrive.Util;

internal static class LogFileRetry
{
    private static bool SharingViolation(IOException error) => (error.HResult & 0xffff) is 32 or 33;

    internal static void Append(Action append)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { append(); return; }
            catch (IOException error) when (attempt < 2 && SharingViolation(error))
            { Thread.Sleep(25 * (attempt + 1)); }
        }
    }

    internal static async Task AppendAsync(Func<Task> append, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { await append().ConfigureAwait(false); return; }
            catch (IOException error) when (attempt < 2 && SharingViolation(error))
            { await Task.Delay(25 * (attempt + 1), cancellationToken).ConfigureAwait(false); }
        }
    }
}
