using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Ftp;
using GlDrive.Irc;
using GlDrive.Spread;

namespace GlDrive.Services.Automation;

/// <summary>Automation uses the running services, so UI and MCP share queues and state.</summary>
public sealed partial class AutomationService : IAutomationModule
{
    private readonly AppConfig _config;
    private readonly ServerManager _servers;
    private readonly NotificationStore _notifications;
    private readonly WishlistStore _wishlist = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public AutomationService(AppConfig config, ServerManager servers, NotificationStore notifications)
    {
        _config = config;
        _servers = servers;
        _notifications = notifications;
    }

    public IReadOnlyList<AutomationToolDefinition> Tools { get; } = BuildTools();

    public async Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Arguments must be a JSON object.");
        ct.ThrowIfCancellationRequested();
        // Mount/unmount/reconnect are multi-step lifecycles. Serialize those requests without
        // blocking transfer cancellation, queue edits or status reads behind a slow network.
        var lifecycle = operation.StartsWith("servers.", StringComparison.Ordinal) && operation is not ("servers.list" or "servers.get")
            || operation is "irc.start" or "irc.stop" or "irc.reconnect";
        if (lifecycle) await _lifecycleGate.WaitAsync(ct);
        try
        {
            var result = operation.Split('.')[0] switch
            {
                "servers" => await ServersAsync(operation, arguments, ct),
                "ftp" => await FtpAsync(operation, arguments, ct),
                "downloads" => Downloads(operation, arguments),
                "search" => await SearchAsync(operation, arguments, ct),
                "wishlist" => operation == "wishlist.calendar_refresh"
                    ? await new MovieCalendar(_config.Downloads, _wishlist).RefreshAsync(Required(arguments, "itemId", 128), ct)
                    : Wishlist(operation, arguments),
                "notifications" => Notifications(operation, arguments),
                "irc" => await IrcAsync(operation, arguments, ct),
                "spread" => await SpreadAsync(operation, arguments, ct),
                _ => throw new ArgumentException($"Unknown operation: {operation}")
            };
            // Never hand a caller live mutable service objects or deferred enumerables.
            return JsonSerializer.SerializeToElement(result, Json);
        }
        finally { if (lifecycle) _lifecycleGate.Release(); }
    }

    private async Task<object?> ServersAsync(string op, JsonElement a, CancellationToken ct)
    {
        if (op == "servers.list")
            return _config.Servers.Select(s => ServerSnapshot(s)).ToArray();
        if (op == "servers.mount_all")
        {
            await _servers.MountAll(ct, manualRetry: true);
            return _config.Servers.Select(s => ServerSnapshot(s)).ToArray();
        }
        if (op == "servers.unmount_all")
        {
            await _servers.UnmountAllAsync();
            return new { unmounted = true };
        }
        var id = Required(a, "serverId", 128);
        var server = Server(id);
        switch (op)
        {
            case "servers.get": return ServerSnapshot(server);
            case "servers.mount": await _servers.MountServer(id, ct, manualRetry: true); break;
            case "servers.unmount": await _servers.UnmountServerAsync(id); break;
            case "servers.remount":
                await _servers.UnmountServerAsync(id);
                ct.ThrowIfCancellationRequested();
                await _servers.MountServer(id, ct, manualRetry: true);
                break;
            case "servers.cache_clear": Mounted(id).RefreshCache(); break;
            case "servers.stats_refresh": await Mounted(id).RefreshStatsAsync(); break;
            case "servers.pool_reinitialize":
                _servers.RetryAuthentication(id);
                await (Mounted(id).Pool ?? throw new InvalidOperationException("FTP pool unavailable.")).Reinitialize(ct);
                break;
            default: throw new ArgumentException($"Unknown operation: {op}");
        }
        return ServerSnapshot(server);
    }

    private object ServerSnapshot(ServerConfig server)
    {
        var live = _servers.GetServer(server.Id);
        var pool = live?.Pool;
        return new
        {
            server.Id, server.Name, server.Enabled,
            host = server.Connection.Host, port = server.Connection.Port,
            driveLetter = server.Mount.DriveLetter,
            state = live?.CurrentState.ToString() ?? MountState.Unmounted.ToString(),
            authenticationHeld = _servers.IsAuthenticationHeld(server.Id),
            authenticationRetryAtUtc = _servers.AuthenticationRetryAtUtc(server.Id),
            health = _servers.GetSiteHealth(server.Id),
            waiting = _servers.GetWaitingState(server.Id),
            loginBudget = GuardedFtpLogin.GetLoginBudget(FtpLoginAccount.From(server.Connection)),
            loginUsage = _servers.GetLoginUsage(server.Id),
            announceHealth = _servers.AnnounceHealth(server.Id),
            announceHealthPersistenceHealthy = _servers.AnnounceHealthPersistenceHealthy,
            stats = live?.Stats,
            ircState = _servers.GetIrcService(server.Id)?.State.ToString() ?? "Disconnected",
            downloads = live?.Downloads?.Store.Items.Count ?? 0,
            pool = pool == null ? null : new
            {
                pool.IsConnected, pool.ActiveCount, pool.TotalCreated, pool.MaxSize,
                pool.EffectiveMaxSize, pool.IsExhausted, pool.IsThrottled, pool.QuarantineSize,
                pool.ObservedLoginCap, pool.UseCpsv
            }
        };
    }

    private ServerConfig Server(string id) => _config.Servers.FirstOrDefault(s => s.Id == id)
        ?? throw new KeyNotFoundException($"Unknown server: {id}");
    private MountService Mounted(string id)
    {
        Server(id);
        return _servers.GetServer(id) ?? throw new InvalidOperationException($"Server {id} is not mounted.");
    }
    private SpreadManager Spread => _servers.Spread ?? throw new InvalidOperationException("Spread service is unavailable.");

    internal static string Required(JsonElement a, string name, int maxLength = 4096)
        => Optional(a, name, maxLength) is { } s && !string.IsNullOrWhiteSpace(s)
            ? s : throw new ArgumentException($"'{name}' is required.");

    internal static string? Optional(JsonElement a, string name, int maxLength = 4096)
    {
        if (!a.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException($"'{name}' must be a string.");
        var result = value.GetString()!;
        if (result.Length > maxLength) throw new ArgumentException($"'{name}' exceeds {maxLength} characters.");
        return result;
    }

    internal static int Integer(JsonElement a, string name, int fallback, int min, int max)
    {
        if (!a.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < min || number > max)
            throw new ArgumentException($"'{name}' must be an integer between {min} and {max}.");
        return number;
    }

    internal static bool Flag(JsonElement a, string name, bool fallback = false)
    {
        if (!a.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException($"'{name}' must be a boolean.");
        return value.GetBoolean();
    }

    internal static T EnumValue<T>(JsonElement a, string name, T fallback) where T : struct, Enum
    {
        var text = Optional(a, name, 64);
        if (text == null) return fallback;
        return Enum.TryParse<T>(text, true, out var result) && Enum.IsDefined(result)
            ? result : throw new ArgumentException($"'{name}' must be one of {string.Join(", ", Enum.GetNames<T>())}.");
    }

    internal static string RemotePath(JsonElement a, string name = "path", bool allowRoot = true)
    {
        var path = Line(Required(a, name), name).Replace('\\', '/');
        if (!path.StartsWith('/') || path.Split('/').Any(p => p is "." or ".."))
            throw new ArgumentException($"'{name}' must be an absolute FTP path without '.' or '..' segments.");
        path = "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (!allowRoot && path == "/") throw new ArgumentException($"'{name}' cannot be the FTP root.");
        return path;
    }

    internal static string Line(string text, string name)
    {
        if (text.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException($"'{name}' cannot contain CR, LF, or NUL.");
        return text;
    }

    private static string Target(JsonElement a, string name = "target")
    {
        var value = Line(Required(a, name, 200), name);
        if (value.Any(char.IsWhiteSpace) || value.Contains(':') || value.Contains(','))
            throw new ArgumentException($"'{name}' must be one IRC target without spaces, ':' or ','.");
        return value;
    }
}
