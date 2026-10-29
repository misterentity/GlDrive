using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlDrive.Config;

namespace GlDrive.Services.Automation;

[Flags]
public enum AutomationCapability { Observe = 1, Operate = 2, Configure = 4, Secrets = 8, RemoteAdmin = 16, LocalFiles = 32 }

public sealed record AutomationPrincipal(string Id, string Name, AutomationCapability Capabilities,
    IReadOnlyList<string> ToolFamilies, bool AllowDestructive, bool IsLegacy = false, string TokenHash = "", IReadOnlyList<string>? AllowedLocalRoots = null)
{
    public static AutomationPrincipal Legacy { get; } = new("legacy", "Legacy owner", (AutomationCapability)63, [], true, true);
}

public sealed class AutomationDeniedException(string message, string code = "scope_denied") : UnauthorizedAccessException(message)
{
    public string Code { get; } = code;
}

public sealed record AutomationCallAudit(string CallId, string ClientId, string ClientName, string Tool,
    JsonElement Arguments, string Outcome, long DurationMilliseconds, string? JobId = null);

[Flags]
public enum AutomationApprovalKind { None = 0, Dangerous = 1, Configuration = 2 }

public sealed record AutomationRisk(AutomationCapability Required, bool Destructive, string? ApprovalReason,
    AutomationApprovalKind ApprovalKind = AutomationApprovalKind.None);

/// <summary>Authentication and authorization use an explicit identity, never an ambient execution context.</summary>
public sealed class AutomationAccessController(ControlApiConfig config, AutomationApprovalBroker approvals)
{
    public AutomationApprovalBroker Approvals { get; } = approvals;
    public bool RequireDangerousApproval => config.Security.RequireDangerousApproval;
    public bool RequiresApproval(AutomationPrincipal principal, AutomationRisk risk) =>
        (!principal.IsLegacy || config.Security.RequireLegacyApproval) &&
        (config.Security.RequireDangerousApproval && risk.ApprovalKind.HasFlag(AutomationApprovalKind.Dangerous) ||
        config.Security.RequireConfigurationApproval && risk.ApprovalKind.HasFlag(AutomationApprovalKind.Configuration));
    public TimeSpan ApprovalTimeout => TimeSpan.FromSeconds(Math.Clamp(config.Security.ApprovalTimeoutSeconds, 5, 600));
    public Func<string, string?>? ServerName { get; set; }
    public string DescribeApproval(string tool, JsonElement arguments, string reason, string? targetName = null)
        => AutomationApprovalDescription.Describe(tool, arguments, reason, ServerName, targetName);

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    internal static AutomationPrincipal LegacyPrincipal(string token) => AutomationPrincipal.Legacy with { TokenHash = HashToken(token) };

    public static AutomationPrincipal? Authenticate(ControlApiConfig config, string presented, DateTimeOffset? now = null)
    {
        if (string.IsNullOrEmpty(presented) || presented.Length > 4096) return null;
        if (!string.IsNullOrEmpty(config.Token) && FixedEquals(presented, config.Token)) return LegacyPrincipal(config.Token);
        var hash = HashToken(presented);
        foreach (var profile in config.Security.Clients.ToArray())
            if (profile.ExpiresUtc is not { } expiry || expiry > (now ?? DateTimeOffset.UtcNow))
                if (FixedEquals(hash, profile.TokenHash.ToLowerInvariant())) return FromProfile(profile);
        return null;
    }

    public AutomationRisk Authorize(AutomationPrincipal principal, AutomationToolDefinition tool, JsonElement arguments)
    {
        var risk = AutomationRiskPolicy.Classify(tool, arguments);
        if (principal.IsLegacy)
        {
            if (principal.TokenHash.Length > 0 && !FixedEquals(principal.TokenHash, HashToken(config.Token)))
                throw new AutomationDeniedException("The legacy token was rotated while the operation was pending.");
            return risk;
        }
        if (tool.Name == "ui.invoke" && risk.Destructive)
            throw new AutomationDeniedException("Scoped clients must use stable-ID service tools for destructive UI selection.");
        var profile = config.Security.Clients.FirstOrDefault(client => client.Id == principal.Id &&
            FixedEquals(client.TokenHash.ToLowerInvariant(), principal.TokenHash.ToLowerInvariant()) &&
            (client.ExpiresUtc == null || client.ExpiresUtc > DateTimeOffset.UtcNow));
        if (profile == null) throw new AutomationDeniedException("The scoped token expired, was revoked or was rotated.");
        var current = FromProfile(profile);
        if (tool.Name.StartsWith("app.jobs", StringComparison.Ordinal)) return risk;
        if ((current.Capabilities & risk.Required) != risk.Required || risk.Destructive && !current.AllowDestructive)
            throw new AutomationDeniedException("This client is not allowed to perform that operation.");
        var family = AutomationRiskPolicy.Family(tool.Name);
        // Job discovery is restricted to the caller's own jobs below, and must stay available for polling.
        if (!tool.Name.StartsWith("app.jobs", StringComparison.Ordinal) && current.ToolFamilies.Count > 0 &&
            !current.ToolFamilies.Contains(family, StringComparer.Ordinal))
            throw new AutomationDeniedException("This tool family is outside this client's scope.");
        if (AutomationRiskPolicy.ChangesLegacyAuthority(tool.Name, arguments))
            throw new AutomationDeniedException("Scoped clients cannot change the legacy control token or its listener settings.");
        AutomationLocalPathPolicy.Enforce(tool.Name, arguments, current.AllowedLocalRoots ?? []);
        return risk;
    }

    internal static AutomationPrincipal FromProfile(AutomationClientConfig profile)
    {
        AutomationCapability scopes = 0;
        foreach (var name in profile.Scopes)
            if (Enum.TryParse<AutomationCapability>(name, true, out var scope)) scopes |= scope;
        return new(profile.Id, profile.Name, scopes, profile.ToolFamilies.ToArray(), profile.AllowDestructive,
            TokenHash: profile.TokenHash, AllowedLocalRoots: profile.AllowedLocalRoots.ToArray());
    }

    private static bool FixedEquals(string first, string second) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second));
}

public static class AutomationRiskPolicy
{
    private static readonly HashSet<string> ReadOnlyRawCommands = new(StringComparer.OrdinalIgnoreCase)
    { "PWD", "SYST", "FEAT", "NOOP", "SITE WHO", "SITE HELP" };
    private static readonly HashSet<string> InstallerSecretFields = new(StringComparer.OrdinalIgnoreCase)
    { "ssh_password", "chanpass1", "chanpass2", "ircpass", "admin_password" };
    private static readonly HashSet<string> LocalDeletion = new(StringComparer.Ordinal)
    {
        "cleanup.delete", "config_snapshot_delete", "wishlist.remove", "downloads.remove", "downloads.clear_finished",
        "notifications.clear", "spread.queue_remove", "spread.blacklist_remove", "spread.blacklist_clear",
        "irc.key_remove", "plex.logout", "library.import"
    };
    private static readonly HashSet<string> RemoteAdministration = new(StringComparer.Ordinal)
    {
        "ftp.command", "ftp.write", "ftp.upload", "ftp.mkdir", "ftp.rename", "ftp.delete", "spread.cleanup_own", "spread.fxp",
        "irc.raw", "installer.start", "plex.invite", "plex.revoke", "plex.update_share"
    };
    private static readonly HashSet<string> UiDeletion = new(StringComparer.Ordinal)
    { "RemoveWishlistCommand", "RemoveSelectedNotificationsCommand", "RemoveSelectedDownloadsCommand", "RemoveSelectedQueuedRacesCommand" };

    public static string Family(string tool)
    {
        var separator = tool.IndexOfAny(['.', '_']);
        return separator < 0 ? tool : tool[..separator];
    }

    public static AutomationRisk Classify(AutomationToolDefinition tool, JsonElement args)
    {
        var name = tool.Name;
        var safeRaw = name == "ftp.command" && IsReadOnlyRawCommand(Text(args, "command"));
        var read = tool.ReadOnly || safeRaw || name is "ui.state" or "extractor.state" or "installer.state" or "installer.profile_get" ||
            (name is "config_lease" or "config_experiment" && Text(args, "action") == "list") ||
            (name == "agent_freeze" && Text(args, "action") == "list") ||
            (name == "agent_memo" && Text(args, "action") is null or "get") ||
            (name == "credentials_manage" && Text(args, "action") == "get");
        // Catalog destructive annotations predate enforcement and include ordinary stop,
        // pause and view operations. Derive deletion/overwrite from actual semantics here.
        var destructive = name == "ftp.command" && !safeRaw || name is "ftp.delete" or "ftp.rename" or "spread.cleanup_own" or "spread.fxp" or
            "installer.start" or "plex.revoke" or "plex.update_share" or "irc.key_set" or "irc.key_mode" ||
            name is "ftp.write" or "ftp.upload" && Flag(args, "overwrite");
        if (name == "ui.invoke") destructive = UiDeletion.Contains(Text(args, "command") ?? "");
        if (name is "installer.logs_clear" or "extractor.clear" or "extractor.remove") destructive = false;
        var required = read ? AutomationCapability.Observe : AutomationCapability.Operate;
        string? approval = null;
        var reveals = name == "irc.key_get" || Flag(args, "includeSecrets") ||
            (name == "credentials_manage" && Text(args, "action") == "get" && Flag(args, "includeValue"));
        if (reveals) { required |= AutomationCapability.Secrets; approval = "Reveal secret values"; }
        if (name is "config_patch" or "config_snapshot_restore" or "config_restore" ||
            (name is "config_lease" or "config_experiment" && !read) ||
            name is "agent_run" or "agent_audit_action" or "installer.profile_set" or "app.update_install" or "plex.login_start" or "plex.select_server" ||
            (name is "agent_freeze" or "agent_memo" && !read) ||
            (name == "credentials_manage" && !read)) required |= AutomationCapability.Configure;
        if (name is "irc.key_set" or "irc.key_remove" or "irc.key_mode" or "irc.key_exchange" or "plex.logout" or "plex.login_start" || Flag(args, "allowSecrets") ||
            (name == "credentials_manage" && !read)) required |= AutomationCapability.Secrets;
        if (name is "irc.key_set" or "irc.key_mode" or "irc.key_exchange" || Flag(args, "allowSecrets") || name == "credentials_manage" && !read)
        { approval ??= "Change stored secrets"; destructive = true; }
        if (name is "config_snapshot_restore" or "config_restore" || name == "config_experiment" && Text(args, "action") == "revert")
        { required |= AutomationCapability.Secrets; approval ??= "Restore persisted settings and secrets"; destructive = true; }
        if (name == "installer.profile_set" && args.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object &&
            profile.EnumerateObject().Any(field => InstallerSecretFields.Contains(field.Name)))
        { required |= AutomationCapability.Secrets; destructive = true; approval ??= "Change installer credentials"; }
        if (name is "installer.connect" or "installer.cancel") required |= AutomationCapability.RemoteAdmin;
        if (RemoteAdministration.Contains(name) && !safeRaw)
        { required |= AutomationCapability.RemoteAdmin; approval ??= "Administer a remote service or write remote files"; }
        if (LocalDeletion.Contains(name) || name == "ui.invoke" && destructive ||
            name == "credentials_manage" && Text(args, "action") == "delete" ||
            name == "ftp.download" && Flag(args, "overwrite") || name is "extractor.start" or "extractor.settings")
        { required |= AutomationCapability.LocalFiles; approval ??= "Delete or overwrite local data"; destructive = true; }
        if (name is "ftp.upload" or "ftp.download" or "player.play_local" or "player.download_torrent" or "cleanup.scan" or "extractor.add" ||
            name.StartsWith("library.", StringComparison.Ordinal) && name is "library.plan" or "library.import" ||
            name == "downloads.enqueue" && Text(args, "localPath") != null)
            required |= AutomationCapability.LocalFiles;
        var approvalKind = approval == null ? AutomationApprovalKind.None : AutomationApprovalKind.Dangerous;
        // Explicit persisted settings edits require local review independently of secret
        // access. Validator-driven agent work and ordinary UI form edits keep their rules.
        if (ChangesConfiguration(name, args))
        {
            approvalKind |= AutomationApprovalKind.Configuration;
            approval ??= "Change saved application settings";
        }
        return new(required, destructive, approval, approvalKind);
    }

    private static bool ChangesConfiguration(string name, JsonElement args) =>
        name is "config_restore" or "config_snapshot_restore" or "plex.select_server" or "plex.logout" ||
        name == "config_lease" && Text(args, "action") == "start" ||
        name == "config_experiment" && Text(args, "action") == "revert" ||
        name == "config_patch" && args.TryGetProperty("operations", out var operations) &&
            operations.ValueKind == JsonValueKind.Array && operations.EnumerateArray().Any(op => Text(op, "op") != "test");

    internal static bool IsReadOnlyRawCommand(string? command) => command != null && !command.Any(char.IsControl) && ReadOnlyRawCommands.Contains(command.Trim());

    internal static bool ChangesLegacyAuthority(string name, JsonElement args)
    {
        if (name == "config_lease" && Text(args, "action") == "start")
        {
            var pointer = Text(args, "pointer") ?? "";
            return IsControlAuthorityPointer(pointer);
        }
        if (name != "config_patch" || !args.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array) return false;
        foreach (var operation in operations.EnumerateArray())
        {
            if (Text(operation, "op") == "test") continue;
            var path = Text(operation, "path") ?? "";
            if (IsControlAuthorityPointer(path)) return true;
        }
        return false;
    }

    private static bool IsControlAuthorityPointer(string pointer) => pointer.Length == 0 ||
        pointer.Equals("/controlApi", StringComparison.OrdinalIgnoreCase) ||
        pointer.StartsWith("/controlApi/", StringComparison.OrdinalIgnoreCase);

    private static bool Flag(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string? Text(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
