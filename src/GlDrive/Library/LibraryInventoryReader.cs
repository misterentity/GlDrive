using GlDrive.Config;
using GlDrive.Plex;
using GlDrive.Services;

namespace GlDrive.Library;

public sealed record LibraryInventoryResult(LibraryIndexSnapshot Snapshot, string PlexStatus);

/// <summary>The explicit disk/Plex inventory shared by the desktop and automation.</summary>
public static class LibraryInventoryReader
{
    public static async Task<LibraryInventoryResult> ReadAsync(AppConfig config, CancellationToken ct = default,
        Func<PlexConfig, PlexClient?>? plex = null)
    {
        ct.ThrowIfCancellationRequested();
        var items = new List<PlexLibraryItem>();
        // A configured server with missing credentials is unavailable, not absent.
        var plexStatus = string.IsNullOrWhiteSpace(config.Plex.ServerUri) ? "unconfigured" : "incomplete";
        try
        {
            using var client = (plex ?? CreatePlex)(config.Plex);
            if (client != null)
            {
                plexStatus = "complete";
                var sections = await client.GetLibrarySectionsAsync(config.Plex.ServerUri, ct).ConfigureAwait(false);
                foreach (var section in sections.Where(s => s.Type is "movie" or "show"))
                    items.AddRange(await client.GetLibraryItemsAsync(config.Plex.ServerUri, section, ct).ConfigureAwait(false));
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
        return new(diskSnapshot with { Complete = diskSnapshot.Complete && (plexStatus is "complete" or "unconfigured") }, plexStatus);
    }

    internal static PlexClient? CreatePlex(PlexConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ServerUri)) return null;
        var token = CredentialStore.GetApiKey("plex");
        if (string.IsNullOrEmpty(token)) return null;
        return new PlexClient(string.IsNullOrWhiteSpace(config.ClientIdentifier) ? "gldrive-library-readonly" : config.ClientIdentifier,
            "GlDrive", UpdateChecker.CurrentVersion.ToString(), token);
    }
}
