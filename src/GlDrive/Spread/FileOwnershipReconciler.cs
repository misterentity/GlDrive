namespace GlDrive.Spread;

/// <summary>
/// Reconciles one FTP listing observation with the race's ownership index.
///
/// A relay/data-channel failure can leave a same-named, truncated file on the
/// destination. Treating filename presence alone as ownership makes that partial
/// copy suppress the retry, after which the race waits out the completion-marker
/// timeout. The largest observed size is retained as the canonical size and only
/// observations of that exact size count as owners. In-flight destination files are
/// ignored because they are expected to be incomplete; the transfer completion path
/// records ownership atomically after the server confirms success.
/// </summary>
internal static class FileOwnershipReconciler
{
    internal static void Observe(
        string serverId,
        SpreadFileInfo file,
        Dictionary<string, HashSet<string>> ownership,
        Dictionary<string, SpreadFileInfo> fileInfos,
        Dictionary<(string fileName, string serverId), long> observedSizes,
        Dictionary<string, int> serverFileCount,
        bool inFlight)
    {
        if (inFlight) return;

        observedSizes[(file.Name, serverId)] = file.Size;

        var canonicalGrew = false;
        if (!fileInfos.TryGetValue(file.Name, out var canonical))
        {
            fileInfos[file.Name] = file;
            canonical = file;
        }
        else if (file.Size > canonical.Size)
        {
            fileInfos[file.Name] = file;
            canonical = file;
            canonicalGrew = true;
        }

        if (!ownership.TryGetValue(file.Name, out var owners))
        {
            owners = new HashSet<string>(StringComparer.Ordinal);
            ownership[file.Name] = owners;
        }

        // A destination may have been observed before the source in the first scan.
        // If the later source observation establishes a larger canonical size, revoke
        // every smaller observation that was provisionally accepted as an owner.
        if (canonicalGrew)
        {
            foreach (var owner in owners.ToList())
            {
                if (!observedSizes.TryGetValue((file.Name, owner), out var observed)
                    || observed != canonical.Size)
                    RemoveOwner(owner, owners, serverFileCount);
            }
        }

        if (file.Size == canonical.Size)
        {
            if (owners.Add(serverId))
                serverFileCount[serverId] = serverFileCount.GetValueOrDefault(serverId) + 1;
        }
        else
        {
            RemoveOwner(serverId, owners, serverFileCount);
        }
    }

    /// <summary>
    /// Apply the NEGATIVE half of a listing: a site that previously owned a file and
    /// no longer lists it loses that ownership, and a file no site lists any more
    /// leaves the race set entirely. Without this the set was add-only: a volume the
    /// source listed once and its zipscript deleted seconds later (wrong-release
    /// upload, 2026-09-07) stayed "missing" forever, driving completion sweeps that
    /// reinitialised every pool, ghost-kills that severed live sessions, and finally
    /// a SITE WIPE of the 27 good files the destination already held.
    ///
    /// An empty listing is NOT evidence about individual files — glftpd moves a
    /// finished release between sections, which reads as "0 files" for one cycle and
    /// is handled by the relocation follower — so it prunes nothing.
    /// Returns the names dropped from <paramref name="fileInfos"/> so the caller can
    /// clean its own per-file maps and log the change.
    /// </summary>
    internal static List<string> Prune(
        string serverId,
        IReadOnlyCollection<SpreadFileInfo> listing,
        Dictionary<string, HashSet<string>> ownership,
        Dictionary<string, SpreadFileInfo> fileInfos,
        Dictionary<(string fileName, string serverId), long> observedSizes,
        Dictionary<string, int> serverFileCount)
    {
        var dropped = new List<string>();
        if (listing.Count == 0) return dropped;

        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in listing) listed.Add(f.Name);

        foreach (var (name, owners) in ownership.ToList())
        {
            if (listed.Contains(name) || !owners.Contains(serverId)) continue;

            RemoveOwner(serverId, owners, serverFileCount);
            observedSizes.Remove((name, serverId));

            if (owners.Count > 0) continue;
            ownership.Remove(name);
            fileInfos.Remove(name);
            dropped.Add(name);
        }
        return dropped;
    }

    private static void RemoveOwner(
        string serverId,
        HashSet<string> owners,
        Dictionary<string, int> serverFileCount)
    {
        if (!owners.Remove(serverId)) return;

        var count = serverFileCount.GetValueOrDefault(serverId);
        if (count <= 1)
            serverFileCount.Remove(serverId);
        else
            serverFileCount[serverId] = count - 1;
    }
}
