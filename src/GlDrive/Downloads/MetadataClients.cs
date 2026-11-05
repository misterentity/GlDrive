using GlDrive.Config;

namespace GlDrive.Downloads;

/// <summary>
/// Process-wide TMDB, OMDb and TVMaze clients. One HttpClient each instead of one per lookup,
/// and each reads its API key from Credential Manager on every request (as
/// <see cref="DownloadConfig.ResolveTmdbKey"/> does), so a key saved in Settings applies at once.
/// Never dispose these.
/// </summary>
internal static class MetadataClients
{
    public static TmdbClient Tmdb { get; } = new(() => CredentialStore.GetApiKey("tmdb") ?? "");
    public static OmdbClient Omdb { get; } = new(() => CredentialStore.GetApiKey("omdb") ?? "");
    public static TvMazeClient TvMaze { get; } = new();
}
