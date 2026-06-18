using System.Text.Json.Serialization;

namespace GlDrive.Plex;

// ---- plex.tv /api/v2/pins (OAuth PIN flow) ----

public sealed class PlexPin
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("authToken")] public string? AuthToken { get; set; }
}

// ---- plex.tv /api/v2/resources (server discovery) ----

public sealed class PlexResource
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("clientIdentifier")] public string ClientIdentifier { get; set; } = "";
    [JsonPropertyName("provides")] public string Provides { get; set; } = "";
    [JsonPropertyName("owned")] public bool Owned { get; set; }
    [JsonPropertyName("connections")] public List<PlexConnection> Connections { get; set; } = new();

    [JsonIgnore] public bool IsServer =>
        Provides?.Split(',').Any(p => p.Trim().Equals("server", StringComparison.OrdinalIgnoreCase)) ?? false;
}

public sealed class PlexConnection
{
    [JsonPropertyName("uri")] public string Uri { get; set; } = "";
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "";
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("local")] public bool Local { get; set; }
    [JsonPropertyName("relay")] public bool Relay { get; set; }
}

// ---- server /library/sections ----

public sealed class PlexLibrary
{
    /// <summary>Section key — the numeric id used as librarySectionId when inviting.</summary>
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
}

// ---- shared_servers (who currently has access) ----

public sealed class PlexSharedUser
{
    /// <summary>SharedServer id — required to revoke/update this share.</summary>
    public string SharedServerId { get; set; } = "";
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string UserId { get; set; } = "";
    public bool Accepted { get; set; }
    public bool AllowSync { get; set; }
    public List<PlexSharedSection> Sections { get; set; } = new();

    public string LibrariesDisplay => Sections.Count == 0 ? "—"
        : string.Join(", ", Sections.Where(s => s.Shared).Select(s => s.Title));
    public string StatusDisplay => Accepted ? "Active" : "Pending";
    public string DownloadsDisplay => AllowSync ? "Yes" : "No";
}

public sealed class PlexSharedSection
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Shared { get; set; }
}

/// <summary>Raised by PlexService for predictable, user-facing conditions
/// (already-has-access, user-not-found, etc.) so the VM can surface a clean message
/// instead of a raw HTTP error — mirrors helpr's ValueError translation.</summary>
public sealed class PlexException(string message) : Exception(message);
