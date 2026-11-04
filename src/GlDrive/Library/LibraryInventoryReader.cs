using GlDrive.Config;
using GlDrive.Plex;
using GlDrive.Services;

namespace GlDrive.Library;

public sealed record LibraryInventoryResult(LibraryIndexSnapshot Snapshot, string PlexStatus);

/// <summary>A Plex server to read; a null <c>Uri</c> means none of its connections answered.</summary>
internal sealed record PlexInventoryTarget(string MachineId, string Name, string? Uri);

/// <summary>The explicit disk/Plex inventory shared by the desktop and automation.</summary>
public static class LibraryInventoryReader
{
    public static async Task<LibraryInventoryResult> ReadAsync(AppConfig config, CancellationToken ct = default,
        Func<PlexConfig, PlexClient?>? plex = null)
    {
        ct.ThrowIfCancellationRequested();
        var items = new List<PlexLibraryItem>();
        IReadOnlyList<string> servers = [];
        // A configured server with missing credentials is unavailable, not absent.
        var plexStatus = string.IsNullOrWhiteSpace(config.Plex.ServerUri) ? "unconfigured" : "incomplete";
        try
        {
            using var client = (plex ?? CreatePlex)(config.Plex);
            if (client != null)
            {
                var (targets, discovered) = await DiscoverAsync(client, config.Plex, ct).ConfigureAwait(false);
                (plexStatus, servers) = await ReadServersAsync(targets, discovered,
                    (uri, t) => ReadServerAsync(client, uri, t), items, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Read-only Plex library inventory is incomplete");
            plexStatus = "incomplete";
        }
        var diskSnapshot = await Task.Run(() => new LibraryIndex().Refresh(
            new[] { config.Library.MovieRoot, config.Library.TvRoot }.Where(p => !string.IsNullOrWhiteSpace(p)), items, ct), ct).ConfigureAwait(false);
        return new(diskSnapshot with
        {
            Complete = diskSnapshot.Complete && (plexStatus is "complete" or "unconfigured"),
            PlexServers = servers,
            PlexComplete = plexStatus == "complete",
        }, plexStatus);
    }

    /// <summary>
    /// Every server the linked account owns (the same list the Plex tab offers), not only the
    /// selected one. When plex.tv itself cannot be reached the selected server is still read,
    /// but the result can no longer claim to cover the whole account.
    /// </summary>
    private static async Task<(IReadOnlyList<PlexInventoryTarget> Targets, bool Discovered)> DiscoverAsync(
        PlexClient client, PlexConfig config, CancellationToken ct)
    {
        List<PlexResource> owned;
        try
        {
            owned = (await client.GetResourcesAsync(ct).ConfigureAwait(false)).Where(r => r.Owned && r.IsServer).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Plex account server discovery failed; reading only the selected server");
            return (string.IsNullOrWhiteSpace(config.ServerUri)
                ? []
                : [new PlexInventoryTarget(config.ServerMachineId, config.ServerName, config.ServerUri)], false);
        }

        var targets = await Task.WhenAll(owned.Select(async server =>
        {
            // The selected server's cached connection is usually right; avoid re-probing all of them.
            if (server.ClientIdentifier == config.ServerMachineId && !string.IsNullOrWhiteSpace(config.ServerUri)
                && await client.ProbeAsync(config.ServerUri, ct).ConfigureAwait(false))
                return new PlexInventoryTarget(server.ClientIdentifier, server.Name, config.ServerUri);
            var uri = await client.FindWorkingConnectionAsync(server, ct).ConfigureAwait(false);
            return new PlexInventoryTarget(server.ClientIdentifier, server.Name, uri);
        })).ConfigureAwait(false);
        return (targets, true);
    }

    private static async Task<IReadOnlyList<PlexLibraryItem>> ReadServerAsync(PlexClient client, string uri, CancellationToken ct)
    {
        var items = new List<PlexLibraryItem>();
        var sections = await client.GetLibrarySectionsAsync(uri, ct).ConfigureAwait(false);
        foreach (var section in sections.Where(s => s.Type is "movie" or "show"))
            items.AddRange(await client.GetLibraryItemsAsync(uri, section, ct).ConfigureAwait(false));
        return items;
    }

    /// <summary>
    /// Merge each server's catalog. One unreachable server leaves the others' items in place
    /// but makes the whole read incomplete, so absence is never inferred from a partial view.
    /// </summary>
    internal static async Task<(string Status, IReadOnlyList<string> Servers)> ReadServersAsync(
        IReadOnlyList<PlexInventoryTarget> targets, bool discovered,
        Func<string, CancellationToken, Task<IReadOnlyList<PlexLibraryItem>>> readServer,
        List<PlexLibraryItem> items, CancellationToken ct)
    {
        if (discovered && targets.Count == 0) return ("unconfigured", []);
        var read = new List<string>();
        var failed = !discovered;
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(target.Uri))
            {
                Serilog.Log.Information("Plex library inventory: {Server} is unreachable on every connection", target.Name);
                failed = true;
                continue;
            }
            try
            {
                var serverItems = await readServer(target.Uri, ct).ConfigureAwait(false);
                // Rating keys are only unique per server; keep two servers' items distinct.
                items.AddRange(serverItems.Select(i => i with { RatingKey = $"{target.MachineId}/{i.RatingKey}" }));
                read.Add(target.Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Serilog.Log.Information("Plex library inventory: reading {Server} failed: {Error}", target.Name, ex.Message);
                failed = true;
            }
        }
        if (read.Count == 0) return ("incomplete", read);
        return (failed ? "incomplete" : "complete", read);
    }

    /// <summary>A client for the linked account. Needs only the token: servers are discovered
    /// from the account, so a missing selected server no longer hides the others.</summary>
    internal static PlexClient? CreatePlex(PlexConfig config)
    {
        var token = CredentialStore.GetApiKey("plex");
        if (string.IsNullOrEmpty(token)) return null;
        return new PlexClient(string.IsNullOrWhiteSpace(config.ClientIdentifier) ? "gldrive-library-readonly" : config.ClientIdentifier,
            "GlDrive", UpdateChecker.CurrentVersion.ToString(), token);
    }
}
