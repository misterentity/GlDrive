using GlDrive.Downloads;
using GlDrive.Library;
using GlDrive.Plex;

namespace GlDrive.Services;

public partial class ServerManager
{
    private LibraryIndexSnapshot _watchlistLibrary = LibraryIndexSnapshot.Empty;
    public PlexWatchlistSync Watchlist { get; private set; } = null!;

    private void InitializeWatchlist()
    {
        Watchlist = new(_config, _wantedWishlist,
            () => GetDownloadManagers().SelectMany(manager => manager.Store.Items),
            () => Volatile.Read(ref _watchlistLibrary));
        _wantedWishlist.Changed += Watchlist.QueueOwnershipReconcile;
    }

    public void ObserveLibraryIndex(LibraryIndexSnapshot snapshot)
    {
        Volatile.Write(ref _watchlistLibrary, snapshot);
        Watchlist.QueueOwnershipReconcile();
    }
}
