namespace GlDrive.Config;

/// <summary>
/// Non-secret Plex settings (the auth token lives in Windows Credential Manager via
/// CredentialStore.GetApiKey/SaveApiKey("plex"), never in this JSON). Mirrors the
/// connection state helpr kept in its .env (PLEX_TOKEN / PLEX_SERVER_NAME) but scoped
/// to a single owned server selected through the in-app OAuth flow.
/// </summary>
public class PlexConfig
{
    /// <summary>Stable per-install client identifier. Plex ties the OAuth token to
    /// this GUID, so it is generated once and then preserved for the life of the
    /// install. Empty until first use; PlexService.EnsureClientId fills it.</summary>
    public string ClientIdentifier { get; set; } = "";

    /// <summary>machineIdentifier of the selected owned Plex server.</summary>
    public string ServerMachineId { get; set; } = "";

    /// <summary>Friendly name of the selected server (for display).</summary>
    public string ServerName { get; set; } = "";

    /// <summary>Cached best connection URI (e.g. https://1-2-3-4.<hash>.plex.direct:32400)
    /// so library/section calls don't need a resources round-trip every time.</summary>
    public string ServerUri { get; set; } = "";

    /// <summary>Default state of the "allow downloads/sync" toggle on the invite form.</summary>
    public bool AllowDownloadsDefault { get; set; }
}
