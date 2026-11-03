using System.IO;
using GlDrive.Ftp;

internal static class NativePasvFallbackCheck
{
    internal static async Task Run(FtpOperations ftp, string host, string fixtureRoot,
        string emptyDirectory, CancellationToken ct)
    {
        var rejectedPath = Path.Combine(fixtureRoot, "reject-search-path");
        var refusedPassive = Path.Combine(fixtureRoot, "reject-passive");
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            Console.WriteLine("PASS: " + message);
        }
        DateTime SeedNearlyExpiredFallback()
        {
            var now = DateTime.UtcNow;
            CpsvDataHelper.MarkCpsvUnusableAt(host,
                now - CpsvDataHelper.CpsvSwitchLifetime + TimeSpan.FromMinutes(1));
            return now + TimeSpan.FromMinutes(2);
        }
        try
        {
            CpsvDataHelper.ForgetHost(host);
            await ftp.ListDirectory(emptyDirectory, ct);
            Check(!CpsvDataHelper.IsCpsvUnusable(host),
                "ordinary standard FTPS listing does not create a CPSV fallback");

            var afterOriginalExpiry = SeedNearlyExpiredFallback();
            var listing = await ftp.ListDirectory(emptyDirectory, ct);
            Check(listing.Length == 0 && CpsvDataHelper.IsCpsvUnusable(host, afterOriginalExpiry),
                "validated empty native listing renews fallback beyond its original expiry");

            afterOriginalExpiry = SeedNearlyExpiredFallback();
            File.WriteAllText(rejectedPath, emptyDirectory);
            var rejected = false;
            try { await ftp.ListDirectory(emptyDirectory, ct); }
            catch (DataCommandRejectedException) { rejected = true; }
            finally { File.Delete(rejectedPath); }
            Check(rejected && !CpsvDataHelper.IsCpsvUnusable(host, afterOriginalExpiry),
                "native LIST 550 does not renew the fallback using an older successful reply");

            afterOriginalExpiry = SeedNearlyExpiredFallback();
            File.WriteAllText(refusedPassive, "1");
            var refused = false;
            try { await ftp.ListDirectory(emptyDirectory, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { refused = true; }
            finally { File.Delete(refusedPassive); }
            Check(refused && !CpsvDataHelper.IsCpsvUnusable(host, afterOriginalExpiry),
                "native passive data refusal does not renew the fallback");
        }
        finally
        {
            File.Delete(rejectedPath);
            File.Delete(refusedPassive);
            CpsvDataHelper.ForgetHost(host);
        }
    }
}
