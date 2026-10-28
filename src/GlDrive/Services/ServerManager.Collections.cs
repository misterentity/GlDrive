using GlDrive.Downloads;
using GlDrive.Library;
using GlDrive.Config;
using GlDrive.Util;
using System.Text.Json;

namespace GlDrive.Services;

public partial class ServerManager
{
    /// <summary>The last explicit B2 inventory; reading it never scans disk or contacts a provider.</summary>
    public LibraryIndexSnapshot CurrentLibraryIndex => Volatile.Read(ref _watchlistLibrary);
    public CollectionGapService Collections { get; private set; } = null!;

    private void InitializeCollections() => Collections = new(_config, _wantedWishlist,
        () => CurrentLibraryIndex, () => GetDownloadManagers().SelectMany(manager => manager.Store.Items),
        refreshIndex: RefreshCollectionLibraryAsync);

    private async Task<LibraryIndexSnapshot> RefreshCollectionLibraryAsync(CancellationToken ct)
    {
        AppConfig config = null!;
        SecureFile.WithPathLock(ConfigManager.ConfigPath, () =>
            config = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(_config))!);
        var inventory = await LibraryInventoryReader.ReadAsync(config, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        ObserveLibraryIndex(inventory.Snapshot);
        return inventory.Snapshot;
    }
}
