using System.Text.Json;
using GlDrive.Config;
using GlDrive.Plex;

namespace GlDrive.Services.Automation;

/// <summary>Plex operations use the existing service and Credential Manager; tokens never enter tool results.</summary>
public sealed class PlexAutomation(AppConfig config) : IAutomationModule, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LoginAttempt? _login;
    private bool _disposed;

    private sealed class LoginAttempt : IDisposable
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public CancellationTokenSource Cancellation { get; } = new(TimeSpan.FromMinutes(4));
        public TaskCompletionSource<string> Url { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> Work { get; set; } = null!;
        public DateTime ExpiresAt { get; } = DateTime.UtcNow.AddMinutes(3);
        public void Dispose() => Cancellation.Dispose();
    }

    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        Tool("plex.status", "Read Plex login state and selected server. Tokens are never returned.", new { }, readOnly: true),
        Tool("plex.servers", "List owned Plex servers and connection candidates from the signed-in account.", new { }, readOnly: true),
        Tool("plex.libraries", "List libraries on the selected Plex server; reconnects a stale server URI if needed.", new { }, readOnly: true),
        Tool("plex.shares", "List users with access to the selected Plex server, their share IDs, and library permissions.", new { }, readOnly: true),
        Tool("plex.test", "Test the selected Plex server connection and read library availability.", new { }, readOnly: true),
        Tool("plex.select_server", "Select an owned Plex server by clientIdentifier from plex.servers. Tests connections and persists the selection.",
            new { serverId = Text() }, ["serverId"]),
        Tool("plex.invite", "Send a Plex library-sharing invitation to an email or username. Requires at least one exact library title. Changes access on Plex.",
            new { user = Text(), libraryTitles = TitlesSchema(), allowDownloads = new { type = "boolean" } }, ["user", "libraryTitles"]),
        Tool("plex.revoke", "Revoke a user's access to the selected Plex server by sharedServerId from plex.shares.",
            new { sharedServerId = Text() }, ["sharedServerId"], destructive: true),
        Tool("plex.update_share", "Replace a user's Plex library permissions by revoking and re-inviting. If the invitation fails after revoke, access remains revoked; inspect plex.shares before retrying.",
            new { sharedServerId = Text(), libraryTitles = TitlesSchema(), allowDownloads = new { type = "boolean" } }, ["sharedServerId", "libraryTitles"], destructive: true),
        Tool("plex.login_start", "Start Plex browser authorization. Returns loginId and authorizationUrl; the user must open that URL and sign in. Poll plex.login_status. Does not open a browser or return tokens.", new { }),
        Tool("plex.login_status", "Read the current Plex authorization attempt. completed means its token was stored in Credential Manager.",
            new { loginId = Text() }, ["loginId"], readOnly: true),
        Tool("plex.login_cancel", "Cancel the pending Plex authorization flow; an already saved token is not revoked.",
            new { loginId = Text() }, ["loginId"]),
        Tool("plex.logout", "Delete the saved Plex token and clear the selected server. Does not revoke users' existing shares.", new { }, destructive: true)
    ];

    public async Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        // PlexService updates shared AppConfig and persists from async continuations.
        // Keep it on the app dispatcher just like its dashboard view model.
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            return await (await dispatcher.InvokeAsync(() => ExecuteAsync(operation, arguments, ct)));
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (operation is "plex.login_status" or "plex.login_cancel")
            {
                var attempt = _login;
                if (attempt == null || attempt.Id != AutomationService.Required(arguments, "loginId", 64))
                    throw new KeyNotFoundException("Unknown or expired Plex loginId.");
                if (operation == "plex.login_cancel" && !attempt.Work.IsCompleted) attempt.Cancellation.Cancel();
                return LoginSnapshot(attempt);
            }
            if (operation == "plex.login_start") return await StartLoginAsync(ct);
            if (operation == "plex.logout" && _login is { Work.IsCompleted: false } pending)
            {
                pending.Cancellation.Cancel();
                try { await pending.Work; } catch (OperationCanceledException) { }
            }
            using var service = new PlexService(config);
            switch (operation)
            {
                case "plex.status": return Snapshot(service);
                case "plex.servers": return await service.GetServersAsync(ct);
                case "plex.libraries": return await service.GetLibrariesAsync(ct);
                case "plex.shares": return await service.GetSharedUsersAsync(ct);
                case "plex.test":
                    var test = await service.TestConnectionAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    return new { test.ok, message = test.ok ? test.message : "Plex connection failed. Check login, selected server, and app diagnostics." };
                case "plex.select_server":
                    string serverId = AutomationService.Required(arguments, "serverId", 256);
                    var selected = (await service.GetServersAsync(ct)).SingleOrDefault(s => s.ClientIdentifier == serverId)
                        ?? throw new KeyNotFoundException("Plex server was not found among owned servers.");
                    await service.SelectServerAsync(selected, ct);
                    return Snapshot(service);
                case "plex.invite":
                    string user = AutomationService.Required(arguments, "user", 320);
                    var inviteTitles = ValidateLibraryTitles(arguments, await service.GetLibrariesAsync(ct));
                    await service.InviteAsync(user, inviteTitles, AutomationService.Flag(arguments, "allowDownloads"), ct);
                    return new { invited = true, user, libraryTitles = inviteTitles };
                case "plex.revoke":
                case "plex.update_share":
                    string shareId = AutomationService.Required(arguments, "sharedServerId", 256);
                    var share = (await service.GetSharedUsersAsync(ct)).SingleOrDefault(s => s.SharedServerId == shareId)
                        ?? throw new KeyNotFoundException("Plex share not found. Refresh plex.shares.");
                    if (operation == "plex.revoke")
                    {
                        await service.RevokeAsync(share, ct);
                        return new { revoked = true, sharedServerId = shareId };
                    }
                    // Validate every requested title before the service's revoke/re-invite sequence.
                    var updateTitles = ValidateLibraryTitles(arguments, await service.GetLibrariesAsync(ct));
                    if (string.IsNullOrWhiteSpace(share.Email) && string.IsNullOrWhiteSpace(share.Username))
                        throw new InvalidOperationException("The share has no email or username; it cannot be re-invited.");
                    await service.UpdateAsync(share, updateTitles, AutomationService.Flag(arguments, "allowDownloads"), ct);
                    return new { updated = true, previousSharedServerId = shareId, libraryTitles = updateTitles, refreshShares = true };
                case "plex.logout":
                    service.Logout();
                    return Snapshot(service);
                default: throw new ArgumentException("Unknown Plex operation.");
            }
        }
        finally { _gate.Release(); }
    }

    private object Snapshot(PlexService service) => new
    {
        loggedIn = service.HasToken,
        hasServer = service.HasServer,
        serverId = config.Plex.ServerMachineId,
        serverName = service.ServerName,
        serverUri = config.Plex.ServerUri,
        refreshDashboardAfterChanges = true
    };

    private async Task<object> StartLoginAsync(CancellationToken ct)
    {
        if (_login is { Work.IsCompleted: false } existing)
            return await LoginStartedAsync(existing, ct);
        _login?.Dispose();
        var attempt = new LoginAttempt();
        _login = attempt;
        attempt.Work = RunLoginAsync(attempt);
        // Authentication continues after this method returns the URL. Use login_cancel
        // to stop it; a completed registry job no longer owns the authorization flow.
        return await LoginStartedAsync(attempt, ct);
    }

    private static async Task<object> LoginStartedAsync(LoginAttempt attempt, CancellationToken ct)
    {
        await Task.WhenAny(attempt.Url.Task, attempt.Work).WaitAsync(ct);
        if (!attempt.Url.Task.IsCompletedSuccessfully)
            return LoginSnapshot(attempt);
        return new { loginId = attempt.Id, status = "awaiting_authorization", authorizationUrl = attempt.Url.Task.Result, expiresAt = attempt.ExpiresAt };
    }

    private async Task<bool> RunLoginAsync(LoginAttempt attempt)
    {
        using var service = new PlexService(config);
        try { return await service.LoginAsync(url => attempt.Url.TrySetResult(url), attempt.Cancellation.Token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return false; } // Provider exception text can include credentials.
    }

    private static object LoginSnapshot(LoginAttempt attempt) => new
    {
        loginId = attempt.Id,
        status = attempt.Work.IsCanceled ? "cancelled" : !attempt.Work.IsCompleted ? "awaiting_authorization"
            : attempt.Work.IsCompletedSuccessfully && attempt.Work.Result ? "completed" : "failed_or_expired",
        expiresAt = attempt.ExpiresAt
    };

    internal static string[] ValidateLibraryTitles(JsonElement arguments, IReadOnlyList<PlexLibrary> libraries)
    {
        if (!arguments.TryGetProperty("libraryTitles", out var titles) || titles.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("libraryTitles must be a nonempty array of exact Plex library titles.");
        if (titles.GetArrayLength() is < 1 or > 200)
            throw new ArgumentException("Select between 1 and 200 Plex libraries.");
        var requested = titles.EnumerateArray().Select(title => title.ValueKind == JsonValueKind.String
            ? title.GetString()! : throw new ArgumentException("Each library title must be a string.")).ToArray();
        if (requested.Any(title => string.IsNullOrWhiteSpace(title) || title.Length > 256))
            throw new ArgumentException("Library titles must contain 1 to 256 characters.");
        if (requested.Any(title => !libraries.Any(library => library.Title.Equals(title, StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("One or more library titles were not found. Read plex.libraries before changing sharing permissions.");
        if (requested.Any(title => libraries.Where(library => library.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
            .Any(library => !int.TryParse(library.Key, out var sectionId) || sectionId <= 0)))
            throw new ArgumentException("One or more libraries has an invalid Plex section ID. Refresh plex.libraries before changing sharing permissions.");
        return requested.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static object Text() => new { type = "string", minLength = 1, maxLength = 320 };
    private static object TitlesSchema() => new { type = "array", minItems = 1, maxItems = 200, items = new { type = "string", minLength = 1, maxLength = 256 } };
    private static AutomationToolDefinition Tool(string name, string description, object properties,
        string[]? required = null, bool readOnly = false, bool destructive = false) => AutomationToolDefinition.Create(name, description,
            new { type = "object", properties, required = required ?? [], additionalProperties = false }, readOnly, destructive);

    public void Dispose()
    {
        _disposed = true;
        if (_login is not { } attempt) return;
        attempt.Cancellation.Cancel();
        _ = attempt.Work.ContinueWith(_ => attempt.Dispose(), TaskScheduler.Default);
    }
}
