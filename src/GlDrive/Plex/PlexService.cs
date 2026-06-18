using GlDrive.Config;
using Serilog;

namespace GlDrive.Plex;

/// <summary>
/// High-level Plex operations backing the dashboard tab. Owns the client identifier
/// + token persistence (config JSON for the non-secret id, Credential Manager for the
/// token), the OAuth login flow, and the invite/revoke/list operations expressed in
/// domain terms (library titles rather than section ids). This is the C# analogue of
/// helpr's plex_service.py, minus everything Stripe/subscription related.
/// </summary>
public sealed class PlexService : IDisposable
{
    private const string Product = "GlDrive";
    private const string TokenService = "plex"; // CredentialStore.GetApiKey/SaveApiKey key

    private readonly AppConfig _config;
    private readonly PlexClient _client;

    public PlexService(AppConfig config)
    {
        _config = config;
        EnsureClientId();
        var token = CredentialStore.GetApiKey(TokenService);
        _client = new PlexClient(_config.Plex.ClientIdentifier, Product, CurrentVersion(), token);
    }

    public bool HasToken => !string.IsNullOrEmpty(_client.Token);
    public bool HasServer => !string.IsNullOrEmpty(_config.Plex.ServerMachineId)
                             && !string.IsNullOrEmpty(_config.Plex.ServerUri);
    public string ServerName => _config.Plex.ServerName;

    private static string CurrentVersion()
    {
        try { return Services.UpdateChecker.CurrentVersion.ToString(); }
        catch { return "1.0"; }
    }

    private void EnsureClientId()
    {
        // Generate in memory only. We must NOT persist config from a constructor —
        // the VM is built whenever the dashboard opens, and an incidental Save() there
        // was what let --screenshots clobber the real config (2026-06-17). The id is
        // persisted lazily on successful login, when there's a token worth keeping.
        if (string.IsNullOrEmpty(_config.Plex.ClientIdentifier))
            _config.Plex.ClientIdentifier = Guid.NewGuid().ToString("N");
    }

    private void SaveConfig()
    {
        try { ConfigManager.Save(_config); }
        catch (Exception ex) { Log.Warning(ex, "Plex: failed to persist config"); }
    }

    // ---- OAuth login ----

    /// <summary>
    /// Runs the PIN-based browser OAuth flow: creates a PIN, hands the auth URL to
    /// <paramref name="openBrowser"/>, then polls until Plex returns a token or the
    /// timeout/cancellation fires. On success the token is stored in Credential Manager.
    /// </summary>
    public async Task<bool> LoginAsync(Action<string> openBrowser, CancellationToken ct)
    {
        var pin = await _client.CreatePinAsync(ct);
        openBrowser(_client.BuildAuthUrl(pin.Code));

        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var polled = await _client.PollPinAsync(pin.Id, ct);
            if (!string.IsNullOrEmpty(polled?.AuthToken))
            {
                _client.Token = polled!.AuthToken;
                CredentialStore.SaveApiKey(TokenService, polled.AuthToken!);
                SaveConfig(); // persist the now-meaningful client identifier
                Log.Information("Plex: OAuth login succeeded");
                return true;
            }
        }
        Log.Warning("Plex: OAuth login timed out waiting for authorization");
        return false;
    }

    public void Logout()
    {
        _client.Token = null;
        CredentialStore.DeleteApiKey(TokenService);
        _config.Plex.ServerMachineId = "";
        _config.Plex.ServerName = "";
        _config.Plex.ServerUri = "";
        SaveConfig();
    }

    // ---- servers / libraries ----

    public Task<List<PlexResource>> GetServersAsync(CancellationToken ct) => GetOwnedServersAsync(ct);

    private async Task<List<PlexResource>> GetOwnedServersAsync(CancellationToken ct)
    {
        var all = await _client.GetResourcesAsync(ct);
        return all.Where(r => r.Owned && r.IsServer).ToList();
    }

    public async Task SelectServerAsync(PlexResource server, CancellationToken ct)
    {
        var uri = await _client.FindWorkingConnectionAsync(server, ct)
                  ?? throw new PlexException(
                      $"Found '{server.Name}' but none of its connections were reachable. " +
                      "Enable Remote Access (or Relay) on the server, or check it's online.");
        _config.Plex.ServerMachineId = server.ClientIdentifier;
        _config.Plex.ServerName = server.Name;
        _config.Plex.ServerUri = uri;
        SaveConfig();
        Log.Information("Plex: selected server {Name} ({Id}) via {Uri}", server.Name, server.ClientIdentifier, uri);
    }

    /// <summary>
    /// Guarantee <c>ServerUri</c> points at a connection that currently responds.
    /// The cached URI can go stale (the server's public IP/port changes, remote access
    /// toggled, switched networks), so if a quick probe fails we re-resolve from the
    /// live resource list. This is what makes "connection refused" self-heal instead of
    /// sticking to a dead connection.
    /// </summary>
    private async Task EnsureWorkingConnectionAsync(CancellationToken ct)
    {
        RequireServer();
        if (await _client.ProbeAsync(_config.Plex.ServerUri, ct)) return;

        Log.Information("Plex: cached connection unreachable, re-resolving for {Id}", _config.Plex.ServerMachineId);
        var server = (await _client.GetResourcesAsync(ct))
            .FirstOrDefault(r => r.ClientIdentifier == _config.Plex.ServerMachineId)
            ?? throw new PlexException("Selected Plex server is no longer on your account.");
        var uri = await _client.FindWorkingConnectionAsync(server, ct)
                  ?? throw new PlexException(
                      $"'{server.Name}' is unreachable on every connection. " +
                      "Enable Remote Access/Relay on the server, or check it's online.");
        _config.Plex.ServerUri = uri;
        SaveConfig();
        Log.Information("Plex: re-resolved connection to {Uri}", uri);
    }

    public async Task<List<PlexLibrary>> GetLibrariesAsync(CancellationToken ct)
    {
        await EnsureWorkingConnectionAsync(ct);
        return await _client.GetLibrariesAsync(_config.Plex.ServerUri, ct);
    }

    public Task<List<PlexSharedUser>> GetSharedUsersAsync(CancellationToken ct)
    {
        RequireServer();
        return _client.GetSharedUsersAsync(_config.Plex.ServerMachineId, ct);
    }

    // ---- invite / revoke ----

    public async Task InviteAsync(string emailOrUsername, IEnumerable<string> libraryTitles,
        bool allowDownloads, CancellationToken ct)
    {
        await EnsureWorkingConnectionAsync(ct);
        if (string.IsNullOrWhiteSpace(emailOrUsername))
            throw new PlexException("Enter a Plex username or email to invite.");

        var titles = libraryTitles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sectionIds = (await _client.GetLibrariesAsync(_config.Plex.ServerUri, ct))
            .Where(l => titles.Contains(l.Title))
            .Select(l => int.TryParse(l.Key, out var id) ? id : -1)
            .Where(id => id > 0)
            .ToList();
        if (titles.Count > 0 && sectionIds.Count == 0)
            throw new PlexException("None of the selected libraries resolved to a Plex section.");

        await _client.InviteAsync(_config.Plex.ServerMachineId, emailOrUsername.Trim(),
            sectionIds, allowDownloads, ct);
        Log.Information("Plex: invited {User} to {Count} libraries (downloads={Dl})",
            emailOrUsername, sectionIds.Count, allowDownloads);
    }

    public async Task RevokeAsync(PlexSharedUser user, CancellationToken ct)
    {
        RequireServer();
        if (string.IsNullOrEmpty(user.SharedServerId))
            throw new PlexException("This share has no id; refresh the list and try again.");
        await _client.RevokeAsync(_config.Plex.ServerMachineId, user.SharedServerId, ct);
        Log.Information("Plex: revoked access for {User}", user.Username);
    }

    /// <summary>Update a user's libraries/downloads by revoking then re-inviting —
    /// the same fallback helpr used when updateFriend wasn't available.</summary>
    public async Task UpdateAsync(PlexSharedUser user, IEnumerable<string> libraryTitles,
        bool allowDownloads, CancellationToken ct)
    {
        await RevokeAsync(user, ct);
        var who = !string.IsNullOrEmpty(user.Email) ? user.Email : user.Username;
        await InviteAsync(who, libraryTitles, allowDownloads, ct);
    }

    public async Task<(bool ok, string message)> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            if (!HasToken) return (false, "Not logged in to Plex.");
            if (!HasServer) return (false, "No Plex server selected.");
            var libs = await GetLibrariesAsync(ct);
            return (true, $"Connected to {ServerName} — {libs.Count} libraries.");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    private void RequireServer()
    {
        if (!HasToken) throw new PlexException("Not logged in to Plex.");
        if (!HasServer) throw new PlexException("No Plex server selected.");
    }

    public void Dispose() => _client.Dispose();
}
