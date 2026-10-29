using System.IO;
using System.Security.Cryptography;
using Serilog;

namespace GlDrive.Irc;

internal sealed record FishKeyRecoverySnapshot(int StoresExamined, int StoresNeedingReview, bool Incomplete)
{
    internal bool NeedsReview => StoresNeedingReview > 0 || Incomplete;
}

/// <summary>Recover local stores even when their server is disabled or no longer configured.</summary>
internal static class FishKeyRecovery
{
    private static FishKeyRecoverySnapshot _snapshot = new(0, 0, false);
    internal static FishKeyRecoverySnapshot Snapshot => Volatile.Read(ref _snapshot);

    internal static void Start(string directory)
    {
        // Disk recovery never delays engine startup, joins or logins. An inventory may
        // briefly remain unavailable until verified quarantine completes in the worker.
        using (ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow())
            _ = Task.Run(() => Volatile.Write(ref _snapshot, Sweep(directory)));
    }

    internal static FishKeyRecoverySnapshot Sweep(string directory, Func<string, FishKeyStore>? open = null)
    {
        open ??= path => new FishKeyStore(path,
            bytes => ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser),
            bytes => ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
        var examined = 0;
        var needsReview = 0;
        var incomplete = false;
        try
        {
            // Materialize names before constructors rename/promote files. Pending-only
            // files map to their primary path; a store is opened only once per sweep.
            var paths = FishKeyStore.InventoryFiles(directory).Select(path =>
                    path.EndsWith(".pending", StringComparison.OrdinalIgnoreCase) ? path[..^8] : path)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var path in paths)
            {
                try
                {
                    var store = open(path);
                    examined++;
                    if (store.RecoveryRequired || !store.PersistenceHealthy) needsReview++;
                }
                catch { incomplete = true; }
            }
        }
        catch { incomplete = true; }
        if (incomplete) Log.Warning("Some local FiSH storage could not be checked; available keys and engine startup are unaffected");
        return new(examined, needsReview, incomplete);
    }
}
