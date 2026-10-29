using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Spread;
using GlDrive.Util;

namespace GlDrive.Services.Automation;

/// <summary>Typed, transactional settings edits for explicitly requested external automation.</summary>
public sealed class ConfigurationAutomation : IAutomationModule, IAutomationApprovalBinding
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly AppConfig _config;
    private readonly Action<AppConfig> _persist;
    private readonly Func<AppConfig> _readCurrent;
    private readonly Action? _settingsApplied;
    private readonly Tls.CertificateManager? _certificates;
    private readonly object _gate = new();
    private const string Redacted = "[redacted]";

    public ConfigurationAutomation(AppConfig config, Action<AppConfig>? persist = null, Func<AppConfig>? readCurrent = null,
        Action? settingsApplied = null, Tls.CertificateManager? certificates = null)
    {
        _config = config;
        _settingsApplied = settingsApplied;
        _certificates = certificates;
        _persist = persist ?? ConfigManager.Save;
        _readCurrent = readCurrent ?? (persist == null ? () =>
            File.Exists(ConfigManager.ConfigPath)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigManager.ConfigPath), Json)
                    ?? throw new InvalidOperationException("Persisted settings are empty")
                : _config : () => _config);
    }

    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("config_get", "Read all application settings or a JSON Pointer subtree. Credentials are redacted unless includeSecrets=true; the control API bearer token is always redacted.",
            new { type = "object", properties = new { pointer = new { type = "string" }, includeSecrets = new { type = "boolean" } }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("config_schema", "Get the complete settings JSON schema, defaults, and editing semantics, including every server, IRC, race, download, player, Plex, and agent setting.",
            new { type = "object", properties = new { }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("config_patch", "Atomically edit application settings with JSON Patch add/replace/remove/test operations and optional revision precondition. Arrays use numeric indexes; /servers/- appends a server. Restart is required to fully apply runtime settings. Use allowSecrets=true for credential changes.",
            new { type = "object", properties = new { operations = new { type = "array", minItems = 1, maxItems = 100, items = new { type = "object", properties = new { op = new { type = "string", @enum = new[] { "add", "replace", "remove", "test" } }, path = new { type = "string" }, value = new { } }, required = new[] { "op", "path" }, additionalProperties = false } }, expectedRevision = new { type = "string" }, allowSecrets = new { type = "boolean" } }, required = new[] { "operations" }, additionalProperties = false }),
        AutomationToolDefinition.Create("credentials_manage", "Read presence, explicitly reveal, set, or delete app credentials in Windows Credential Manager. FTP/proxy/IRC credentials require serverId; API credentials require service (omdb, tmdb, openrouter, plex, or alerts-TARGET_ID JSON). Read returns presence only unless includeValue=true. Values are never returned on writes.",
            new { type = "object", properties = new { action = new { type = "string", @enum = new[] { "get", "set", "delete" } }, kind = new { type = "string", @enum = new[] { "ftp", "proxy", "irc", "api" } }, serverId = new { type = "string" }, service = new { type = "string" }, value = new { type = "string" }, includeValue = new { type = "boolean" } }, required = new[] { "action", "kind" }, additionalProperties = false }, destructive: true)
    ];

    public async Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operation is "config_patch" or "credentials_manage" && System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            return await (await dispatcher.InvokeAsync(() => ExecuteAsync(operation, arguments, cancellationToken)));
        object? result = operation switch
        {
            "config_get" => Get(String(arguments, "pointer") ?? "", Boolean(arguments, "includeSecrets")),
            "config_schema" => Schema(),
            "config_patch" => Apply(JsonNode.Parse(arguments.GetProperty("operations").GetRawText()) as JsonArray
                ?? throw new ArgumentException("operations must be an array"), String(arguments, "expectedRevision"), Boolean(arguments, "allowSecrets")),
            "credentials_manage" => Credentials(String(arguments, "action") ?? "get", String(arguments, "kind") ?? "",
                String(arguments, "serverId"), String(arguments, "service"), String(arguments, "value"), Boolean(arguments, "includeValue")),
            _ => throw new ArgumentException("Unknown configuration operation")
        };
        return result;
    }

    public Task<IAutomationApprovedCall?> BindApprovalAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (operation != "config_patch") return Task.FromResult<IAutomationApprovedCall?>(null);
        // Array pointers select a row in the current document. Hold that exact revision
        // through local review so a concurrent reorder cannot change the approved target.
        var revision = String(arguments, "expectedRevision") ?? Revision(CurrentSnapshot());
        var boundArguments = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        boundArguments["expectedRevision"] = revision;
        return Task.FromResult<IAutomationApprovedCall?>(new ApprovedPatch(this,
            JsonSerializer.SerializeToElement(boundArguments), revision));
    }

    private sealed record ApprovedPatch(ConfigurationAutomation Owner, JsonElement Arguments, string Revision) : IAutomationApprovedCall
    {
        public string Description => "Settings revision: " + Revision + ". Concurrent edits require a new request.";
        public Task<object?> ExecuteAsync(CancellationToken ct) => Owner.ExecuteAsync("config_patch", Arguments, ct);
    }

    public object Get(string pointer = "", bool includeSecrets = false)
    {
        object? result = null;
        SecureFile.WithPathLock(ConfigManager.ConfigPath, () => result = GetCore(pointer, includeSecrets));
        return result!;
    }

    private object GetCore(string pointer, bool includeSecrets)
    {
        lock (_gate)
        {
            var root = Snapshot();
            var revision = Revision(root);
            Redact(root, includeSecrets);
            return new { revision, pointer, value = ResolveRequired(root, pointer)?.DeepClone(), secretsRedacted = !includeSecrets, restartRequiredAfterChanges = true };
        }
    }

    public object Schema() => new
    {
        schema = BuildSchema(typeof(AppConfig)),
        defaults = Redact(JsonSerializer.SerializeToNode(new AppConfig(), Json)!, false),
        patchFormat = "RFC 6902 subset: add, replace, remove, test; paths use RFC 6901 JSON Pointer; array append uses /-.",
        runtime = "Settings are persisted atomically. Server/download settings schedule a serialized runtime refresh: connection, TLS, mount and login-budget changes remount; other changes update their owning services. IRC restarts independently. Active race destination paths remain pinned. WinFsp fileInfoTimeoutMs applies on the next drive mount; unrelated listeners, logging, scheduling and UI state may still require restart.",
        secrets = "[redacted] is a read-only placeholder; explicit allowSecrets is required to change secrets. The control API token cannot be read; use Regenerate control-API token in MCP settings, or a secret-enabled config_patch. External credentials use credentials_manage."
    };

    public object Apply(JsonArray operations, string? expectedRevision = null, bool allowSecrets = false)
    {
        using var attribution = ConfigWriteScope.Fallback("mcp:config_patch");
        object? result = null;
        SecureFile.WithPathLock(ConfigManager.ConfigPath, () => result = ApplyCore(operations, expectedRevision, allowSecrets));
        return result!;
    }

    public object Preview(JsonArray operations)
    {
        object? result = null;
        SecureFile.WithPathLock(ConfigManager.ConfigPath, () =>
        {
            lock (_gate)
            {
                if (operations.Count is < 1 or > 100) throw new ArgumentException("Supply between 1 and 100 patch operations");
                var original = Snapshot();
                var next = original.DeepClone();
                foreach (var operation in operations)
                    ApplyOperation(next, operation as JsonObject ?? throw new ArgumentException("Each operation must be an object"));
                RestoreRedacted(next, original);
                RejectHookChanges(original, next);
                if (SecretValuesChanged(original, next)) throw new ArgumentException("Secret changes are not supported by preview");
                ConfigManager.NormalizeLibraryEnable(original, next);
                ValidateCaptureFlag(next);
                var candidate = next.Deserialize<AppConfig>(Json)!;
                RejectInvalidAttentionChanges(original, next, candidate);
                Validate(candidate);
                var baseline = original.Deserialize<AppConfig>(Json)!;
                var servers = baseline.Servers.Select(s => s.Id).Union(candidate.Servers.Select(s => s.Id)).Select(id =>
                {
                    var old = baseline.Servers.SingleOrDefault(s => s.Id == id);
                    var current = candidate.Servers.SingleOrDefault(s => s.Id == id);
                    if (old == null || current == null) return new { serverId = id, changes = new[] { "server added or removed" }, remount = true, ircRestart = true };
                    var prior = RuntimeConfigPolicy.Capture(old, baseline.Downloads, "", baseline.Spread.SpreadPoolSize);
                    var after = RuntimeConfigPolicy.Capture(current, candidate.Downloads, "", candidate.Spread.SpreadPoolSize);
                    return new { serverId = id, changes = after.Changed(prior).ToArray(),
                        remount = after.Changed(prior, RuntimeConfigEffect.Remount).Count > 0,
                        ircRestart = after.Changed(prior, RuntimeConfigEffect.Irc).Count > 0 };
                }).ToArray();
                result = new { servers, message = "Preview only. Other global settings may require restart. Idle-deferred scalar edits are available as leases." };
            }
        });
        return result!;
    }

    private object ApplyCore(JsonArray operations, string? expectedRevision, bool allowSecrets)
    {
        if (operations.Count is < 1 or > 100) throw new ArgumentException("Supply between 1 and 100 patch operations");
        lock (_gate)
        {
            if (ConfigManager.ReadOnly) throw new InvalidOperationException("Settings are read-only in this application mode");
            var original = Snapshot();
            if (expectedRevision != null && !string.Equals(expectedRevision, Revision(original), StringComparison.Ordinal))
                throw new InvalidOperationException("Settings changed since they were read; fetch them again before editing");
            var next = original.DeepClone();
            foreach (var operation in operations)
                ApplyOperation(next, operation as JsonObject ?? throw new ArgumentException("Each operation must be an object"));
            RestoreRedacted(next, original);
            RejectHookChanges(original, next);
            if (!JsonNode.DeepEquals(original["controlApi"]?["security"], next["controlApi"]?["security"]))
                throw new ArgumentException("Automation security settings require the local Automation access window.");
            if (!allowSecrets && SecretValuesChanged(original, next))
                throw new ArgumentException("Changing secrets requires allowSecrets=true");
            ConfigManager.NormalizeLibraryEnable(original, next);
            ValidateCaptureFlag(next);
            var candidate = next.Deserialize<AppConfig>(Json) ?? throw new ArgumentException("Settings must be an object");
            RejectInvalidAttentionChanges(original, next, candidate);
            Validate(candidate);
            var canonical = JsonSerializer.SerializeToNode(candidate, Json)!;
            var changed = !JsonNode.DeepEquals(original, canonical);
            if (changed)
            {
                if (_certificates != null) _certificates.GuardConfiguration(candidate);
                else Tls.CertificateManager.GuardPendingConfiguration(candidate);
                // Save first: a failed disk write must leave the live settings untouched.
                // ConfigManager uses SecureFile's restricted, flushed, atomic rename.
                _persist(candidate);
                canonical = JsonSerializer.SerializeToNode(candidate, Json)!;
                Reconcile(_config, candidate);
                try { _settingsApplied?.Invoke(); }
                catch (Exception ex) { Serilog.Log.Warning(ex, "Settings saved, but scheduling the runtime settings refresh failed"); }
            }
            return new { saved = changed, revision = Revision(canonical), restartRequired = changed,
                message = changed ? "Saved. Server/download runtime refresh scheduled when connected; unrelated settings may require restart. WinFsp fileInfoTimeoutMs applies on the next drive mount." : "No settings changed." };
        }
    }

    private static void RejectInvalidAttentionChanges(JsonNode original, JsonNode next, AppConfig candidate)
    {
        if (candidate.IrcAttention.UnparsedJson != null && !JsonNode.DeepEquals(original["ircAttention"], next["ircAttention"]))
            throw new ArgumentException("Invalid /ircAttention settings. Use booleans and arrays of at most 64 strings, each at most 128 characters.");
        if (candidate.DataStewardship.UnparsedJson != null && !JsonNode.DeepEquals(original["dataStewardship"], next["dataStewardship"]))
            throw new ArgumentException("Invalid /dataStewardship settings. Use the documented boolean switches.");
        if (candidate.Recovery.UnparsedJson != null && !JsonNode.DeepEquals(original["recovery"], next["recovery"]))
            throw new ArgumentException("Invalid /recovery settings. Use a crash threshold of at least three and the documented recovery limits.");
    }

    private static void RejectHookChanges(JsonNode original, JsonNode next)
    {
        if (!JsonNode.DeepEquals(original["hooks"], next["hooks"]))
            throw new ArgumentException("Executable hooks require a direct appsettings edit and restart; automation cannot change them");
    }

    public object Credentials(string action, string kind, string? serverId = null, string? service = null, string? value = null, bool includeValue = false)
    {
        object result;
        lock (_gate) result = CredentialsCore(action, kind, serverId, service, value, includeValue);
        if (action != "get")
        {
            try { _settingsApplied?.Invoke(); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Credential saved, but scheduling the runtime settings refresh failed"); }
        }
        return result;
    }

    private object CredentialsCore(string action, string kind, string? serverId, string? service, string? value, bool includeValue)
    {
        if (action is not ("get" or "set" or "delete")) throw new ArgumentException("action must be get, set, or delete");
        if (action != "get" && (ConfigManager.ReadOnly || CredentialStore.AccessDisabled))
            throw new InvalidOperationException("Credential writes are disabled in this application mode");
        if (action == "set" && string.IsNullOrEmpty(value)) throw new ArgumentException("A nonempty value is required; use delete to clear a credential");
        Func<string?> read;
        Action<string> write;
        Action delete;
        if (kind == "api")
        {
            ArgumentNullException.ThrowIfNull(service);
            if (service is not ("omdb" or "tmdb" or "openrouter" or "plex") && !Regex.IsMatch(service ?? "", "^alerts-[a-zA-Z0-9_-]{1,64}$")) throw new ArgumentException("Unknown API credential service");
            var apiService = service ?? throw new ArgumentException("API service is required");
            read = () => CredentialStore.GetApiKey(apiService);
            write = secret => CredentialStore.SaveApiKey(apiService, secret);
            delete = () => CredentialStore.DeleteApiKey(apiService);
        }
        else
        {
            var server = _config.Servers.SingleOrDefault(s => s.Id == serverId) ?? throw new ArgumentException("Unknown serverId");
            switch (kind)
            {
                case "ftp":
                    var ftp = server.Connection;
                    read = () => CredentialStore.GetPassword(ftp.Host, ftp.Port, ftp.Username);
                    write = secret => CredentialStore.SavePassword(ftp.Host, ftp.Port, ftp.Username, secret);
                    delete = () => CredentialStore.DeletePassword(ftp.Host, ftp.Port, ftp.Username);
                    break;
                case "irc":
                    var irc = server.Irc;
                    read = () => CredentialStore.GetIrcPassword(irc.Host, irc.Port, irc.Nick);
                    write = secret => CredentialStore.SaveIrcPassword(irc.Host, irc.Port, irc.Nick, secret);
                    delete = () => CredentialStore.DeleteIrcPassword(irc.Host, irc.Port, irc.Nick);
                    break;
                case "proxy":
                    var proxy = server.Connection.Proxy ?? throw new ArgumentException("This server has no configured proxy");
                    read = () => CredentialStore.GetProxyPassword(proxy.Host, proxy.Port, proxy.Username);
                    write = secret => CredentialStore.SaveProxyPassword(proxy.Host, proxy.Port, proxy.Username, secret);
                    delete = () => CredentialStore.DeleteProxyPassword(proxy.Host, proxy.Port, proxy.Username);
                    break;
                default: throw new ArgumentException("kind must be ftp, irc, proxy, or api");
            }
        }
        if (action == "set")
        {
            write(value!);
            if (read() != value) throw new InvalidOperationException("Credential Manager did not persist the credential");
        }
        else if (action == "delete")
        {
            delete();
            if (!string.IsNullOrEmpty(read())) throw new InvalidOperationException("Credential Manager did not delete the credential");
        }
        var current = read();
        return new { action, kind, serverId, service, present = !string.IsNullOrEmpty(current), value = action == "get" && includeValue ? current : null,
            restartRequired = action != "get" };
    }

    private JsonNode Snapshot() => JsonSerializer.SerializeToNode(_readCurrent(), Json)!;
    internal JsonNode CurrentSnapshot()
    {
        JsonNode? snapshot = null;
        SecureFile.WithPathLock(ConfigManager.ConfigPath, () => { lock (_gate) snapshot = Snapshot(); });
        return snapshot!;
    }
    private static string Revision(JsonNode node) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString()))).ToLowerInvariant();
    internal static string? String(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    internal static bool Boolean(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.GetBoolean();
    internal static int Integer(JsonElement args, string name, int fallback) => args.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;

    private static string[] ParsePointer(string pointer)
    {
        if (Regex.IsMatch(pointer, "~(?![01])", RegexOptions.None, TimeSpan.FromMilliseconds(100)))
            throw new ArgumentException("Invalid JSON Pointer escape");
        return JsonPointer.Split(pointer);
    }

    internal static JsonNode? ResolveRequired(JsonNode root, string pointer)
    {
        JsonNode? current = root;
        foreach (var part in ParsePointer(pointer))
        {
            if (current is JsonObject obj && obj.TryGetPropertyValue(part, out var child)) current = child;
            else if (current is JsonArray array && TryIndex(part, array.Count, false, out var index)) current = array[index];
            else throw new ArgumentException($"Unknown settings pointer: {pointer}");
        }
        return current;
    }

    private static bool TryIndex(string token, int count, bool append, out int index)
    {
        index = -1;
        if (append && token == "-") { index = count; return true; }
        return token.Length > 0 && (token.Length == 1 || token[0] != '0') && token.All(char.IsAsciiDigit)
            && int.TryParse(token, out index) && index >= 0 && index < count + (append ? 1 : 0);
    }

    private static void ApplyOperation(JsonNode root, JsonObject operation)
    {
        if (operation.Any(p => p.Key is not ("op" or "path" or "value"))) throw new ArgumentException("Unknown patch operation field");
        var op = operation["op"]?.GetValue<string>() ?? throw new ArgumentException("op is required");
        var pointer = operation["path"]?.GetValue<string>() ?? throw new ArgumentException("path is required");
        if (op is not ("add" or "replace" or "remove" or "test")) throw new ArgumentException("Unsupported patch operation");
        if (op != "remove" && !operation.ContainsKey("value")) throw new ArgumentException("value is required");
        var parts = ParsePointer(pointer);
        if (op == "test")
        {
            if (!JsonNode.DeepEquals(ResolveRequired(root, pointer), operation["value"])) throw new InvalidOperationException($"Patch test failed at {pointer}");
            return;
        }
        if (parts.Length == 0) throw new ArgumentException("Patch individual settings or sections; replacing the entire settings root is not supported");
        var parentPointer = pointer[..pointer.LastIndexOf('/')];
        var parent = ResolveRequired(root, parentPointer);
        var leaf = parts[^1];
        if (parent is JsonObject obj)
        {
            if (op != "add" && !obj.ContainsKey(leaf)) throw new ArgumentException($"Unknown settings pointer: {pointer}");
            if (op == "remove") obj.Remove(leaf);
            else obj[leaf] = operation["value"]?.DeepClone();
        }
        else if (parent is JsonArray array && TryIndex(leaf, array.Count, op == "add", out var index))
        {
            if (op == "remove") array.RemoveAt(index);
            else if (op == "add") array.Insert(index, operation["value"]?.DeepClone());
            else array[index] = operation["value"]?.DeepClone();
        }
        else throw new ArgumentException($"Invalid patch target: {pointer}");
    }

    internal static JsonNode Redact(JsonNode node, bool includeSecrets, string pointer = "")
    {
        if (node is JsonObject obj)
            foreach (var (key, value) in obj.ToList())
            {
                var childPointer = pointer + "/" + JsonPointer.Escape(key);
                if (childPointer is "/controlApi/token" or "/hooks" ||
                    childPointer.StartsWith("/controlApi/security/clients/", StringComparison.OrdinalIgnoreCase) && childPointer.EndsWith("/tokenHash", StringComparison.OrdinalIgnoreCase) ||
                    (!includeSecrets && ConfigSecretPointers.IsSecret(childPointer)))
                    obj[key] = value?.ToString() is { Length: > 0 } ? Redacted : "";
                else if (value != null) Redact(value, includeSecrets, childPointer);
            }
        else if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++)
                if (array[i] is { } child) Redact(child, includeSecrets, pointer + "/" + i);
        return node;
    }

    private static void RestoreRedacted(JsonNode? next, JsonNode? original)
    {
        if (next is JsonObject obj)
            foreach (var (key, value) in obj.ToList())
            {
                var previous = (original as JsonObject)?[key];
                if (ConfigSecretPointers.IsSecret(key) && value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text == Redacted)
                    obj[key] = previous?.DeepClone() ?? throw new ArgumentException("A redacted credential placeholder cannot initialize a new credential");
                else RestoreRedacted(value, previous);
            }
        else if (next is JsonArray array)
            for (var i = 0; i < array.Count; i++)
            {
                // Match keyed objects so reordered servers/channels retain their own secrets.
                var oldArray = original as JsonArray;
                JsonNode? previous = oldArray != null && i < oldArray.Count ? oldArray[i] : null;
                if (array[i] is JsonObject item)
                {
                    var identity = item.ContainsKey("id") ? "id" : item.ContainsKey("name") ? "name" : null;
                    if (identity != null && oldArray != null)
                        previous = oldArray.FirstOrDefault(old => old is JsonObject prior && JsonNode.DeepEquals(prior[identity], item[identity]));
                }
                RestoreRedacted(array[i], previous);
            }
    }

    private static bool SecretValuesChanged(JsonNode? before, JsonNode? after, string pointer = "")
    {
        if (ConfigSecretPointers.IsSecret(pointer))
            return !JsonNode.DeepEquals(before, after) && !(string.IsNullOrEmpty(before?.ToString()) && string.IsNullOrEmpty(after?.ToString()));
        if (before is JsonObject || after is JsonObject)
        {
            var keys = ((before as JsonObject)?.Select(p => p.Key) ?? []).Union((after as JsonObject)?.Select(p => p.Key) ?? []);
            return keys.Any(key => SecretValuesChanged((before as JsonObject)?[key], (after as JsonObject)?[key], pointer + "/" + JsonPointer.Escape(key)));
        }
        if (before is JsonArray || after is JsonArray)
        {
            var left = before as JsonArray;
            var right = after as JsonArray;
            return Enumerable.Range(0, Math.Max(left?.Count ?? 0, right?.Count ?? 0)).Any(i =>
                SecretValuesChanged(left != null && i < left.Count ? left[i] : null, right != null && i < right.Count ? right[i] : null, pointer + "/" + i));
        }
        return false;
    }

    // Preserve objects held by running managers, including server identity across reorders.
    internal static void Reconcile(object destination, object source)
    {
        foreach (var property in Properties(source.GetType()))
        {
            var value = property.GetValue(source);
            var existing = property.GetValue(destination);
            // Hook definitions are restart-only snapshots, including retained raw JSON.
            // Recursive POCO reconciliation would lose their private parse state.
            if (property.PropertyType == typeof(HookConfig) || property.PropertyType == typeof(IrcAttentionConfig) || property.PropertyType == typeof(DataStewardshipConfig) || property.PropertyType == typeof(AutomationSecurityConfig) || property.PropertyType == typeof(RecoveryConfig)) property.SetValue(destination, value);
            else if (value is IList sourceList && existing is IList targetList && !property.PropertyType.IsArray)
            {
                var previous = targetList.Cast<object>().ToList();
                var replacements = new List<object?>();
                foreach (var item in sourceList)
                {
                    if (item is ServerConfig server && previous.OfType<ServerConfig>().FirstOrDefault(s => s.Id == server.Id) is { } retained)
                    { Reconcile(retained, server); replacements.Add(retained); }
                    else replacements.Add(item);
                }
                targetList.Clear();
                foreach (var item in replacements) targetList.Add(item);
            }
            else if (value is IDictionary sourceDictionary && existing is IDictionary targetDictionary)
            {
                targetDictionary.Clear();
                foreach (DictionaryEntry entry in sourceDictionary) targetDictionary.Add(entry.Key, entry.Value);
            }
            else if (value != null && existing != null && property.PropertyType.Namespace == typeof(AppConfig).Namespace && !property.PropertyType.IsEnum)
                Reconcile(existing, value);
            else property.SetValue(destination, value);
        }
    }

    private static IEnumerable<PropertyInfo> Properties(Type type) => type.GetProperties().Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() == null);

    private static JsonObject BuildSchema(Type type)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable != null) return new JsonObject { ["anyOf"] = new JsonArray(BuildSchema(nullable), new JsonObject { ["type"] = "null" }) };
        if (type == typeof(string)) return new JsonObject { ["type"] = "string" };
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (type.IsEnum) return new JsonObject { ["type"] = "integer", ["enum"] = new JsonArray(Enum.GetValues(type).Cast<object>().Select(v => (JsonNode?)JsonValue.Create(Convert.ToInt32(v))).ToArray()), ["x-enumNames"] = new JsonArray(Enum.GetNames(type).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
        if (type == typeof(int) || type == typeof(long)) return new JsonObject { ["type"] = "integer" };
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return new JsonObject { ["type"] = "number" };
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)) return new JsonObject { ["type"] = "object", ["additionalProperties"] = BuildSchema(type.GenericTypeArguments[1]) };
        if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))) return new JsonObject { ["type"] = "array", ["items"] = BuildSchema(type.IsArray ? type.GetElementType()! : type.GenericTypeArguments[0]) };
        var properties = new JsonObject();
        foreach (var property in Properties(type))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var schema = BuildSchema(property.PropertyType);
            if (type == typeof(AutomationSecurityConfig))
            {
                schema["description"] = property.Name switch
                {
                    nameof(AutomationSecurityConfig.RequireDangerousApproval) => "Default true for scoped clients. Local approval for secret reveal, remote administration and destructive local actions. Owner calls are affected only when requireLegacyApproval is enabled.",
                    nameof(AutomationSecurityConfig.RequireConfigurationApproval) => "Default true for scoped clients. Review configuration writes locally. Owner calls are affected only when requireLegacyApproval is enabled.",
                    nameof(AutomationSecurityConfig.RequireLegacyApproval) => "Default false. Explicitly opt the owner token into the enabled approval classes through the local Automation access window. Existing clients remain immediate by default.",
                    nameof(AutomationSecurityConfig.ApprovalTimeoutSeconds) => "Default 120 seconds; unanswered or locked-session requests deny without blocking engine threads.",
                    nameof(AutomationSecurityConfig.LedgerEnabled) => "Default true. Passive redacted append-only call evidence; storage failure never refuses a call.",
                    nameof(AutomationSecurityConfig.LedgerCapacity) => "Default 2000. Passive in-memory retention only, evict oldest; never a call or engine rate limit.",
                    _ => "Empty by default. Named scoped clients are managed only in the local Automation access window; hashes, never raw tokens, are stored."
                };
                if (property.Name == nameof(AutomationSecurityConfig.ApprovalTimeoutSeconds)) { schema["minimum"] = 5; schema["maximum"] = 600; }
                if (property.Name == nameof(AutomationSecurityConfig.LedgerCapacity)) { schema["minimum"] = 100; schema["maximum"] = 10000; }
            }
            if (type == typeof(AutomationClientConfig) && property.Name == nameof(AutomationClientConfig.AllowedLocalRoots))
                schema["description"] = "Empty by default: scoped clients cannot use explicit local file paths. Selected existing roots allow canonical descendants without junctions or symbolic links; legacy token is unaffected.";
            if (type == typeof(IrcAttentionConfig))
            {
                schema["description"] = property.Name switch
                {
                    nameof(IrcAttentionConfig.NickHighlightsEnabled) => "Default true. Passive local-only notices for whole-token current/configured nick mentions; no network work.",
                    nameof(IrcAttentionConfig.PrivateMessageAlertsEnabled) => "Default true. Generic local-only PM notices; no message text in alert history or outbound targets.",
                    nameof(IrcAttentionConfig.Keywords) => "Empty by default. Up to 64 literal whole-token highlight words/phrases, each at most 128 characters. Never regex or credentials.",
                    _ => "Empty by default. Up to 64 nicks excluded from channel highlights; PM unread counts are retained."
                };
                if (property.PropertyType == typeof(List<string>))
                {
                    schema["maxItems"] = 64;
                    if (schema["items"] is JsonObject itemSchema) itemSchema["maxLength"] = 128;
                }
            }
            if (type == typeof(DataStewardshipConfig))
                schema["description"] = property.Name == nameof(DataStewardshipConfig.CopyConfigSecrets)
                    ? "Default true. Startup makes and reads back a DPAPI recovery copy. Config remains authoritative and readable by older builds. No login or automation gate."
                    : "Default false. Guard saved feed preferences with last-good generations and a shrink check. View actions stay immediate; explicit removals bypass the shrink check.";
            if (type == typeof(RecoveryConfig))
            {
                schema["description"] = property.Name switch
                {
                    nameof(RecoveryConfig.CrashLoopProtectionEnabled) => "Default true. Safe mode requires a detected loop of explicit crash restarts; ordinary, manual and update launches always start normally. Changes apply on next launch.",
                    nameof(RecoveryConfig.CrashLoopThreshold) => "Default 3 distinct crashed sessions; minimum 3 prevents a single-crash regression.",
                    nameof(RecoveryConfig.CrashLoopWindowMinutes) => "Default 15 minutes. Only explicit crash evidence counts, never service health or a site's availability.",
                    nameof(RecoveryConfig.SafeModeRetryDelaysSeconds) => "Default [60,300,900]. Each entry permits one further safe-mode crash restart after that delay. Empty disables those retries; normal single-crash recovery remains three seconds.",
                    nameof(RecoveryConfig.UpdateProbationEnabled) => "Default true. Holds subsequent automatic installations during 24-hour probation; retained previous-build files remain protected for 24 hours even when disabled.",
                    _ => "Default true preserves existing automatic updates: three-hour poll and idle gate. False disables automatic installation, not manual update checks."
                };
                if (property.Name == nameof(RecoveryConfig.CrashLoopThreshold)) { schema["minimum"] = 3; schema["maximum"] = 20; }
                if (property.Name == nameof(RecoveryConfig.CrashLoopWindowMinutes)) { schema["minimum"] = 1; schema["maximum"] = 60; }
                if (property.Name == nameof(RecoveryConfig.SafeModeRetryDelaysSeconds))
                {
                    schema["maxItems"] = 8;
                    if (schema["items"] is JsonObject item) { item["minimum"] = 3; item["maximum"] = 3600; }
                }
            }
            if (type == typeof(PlexConfig) && property.Name == nameof(PlexConfig.WatchlistSyncEnabled))
                schema["description"] = "Default false. One-way Plex Watchlist import using the existing token. GET only, IDs and current default quality/profile, preserves manual choices. Verified ownership can mark imported items Completed. No Plex writes or FTP traffic.";
            if (type == typeof(PlexConfig) && property.Name == nameof(PlexConfig.WatchlistSyncMinutes))
            {
                schema["description"] = "Default 30 minutes, only while explicitly enabled. Provider pages contain ten items; each sync has a two-minute HTTP budget. Offline or malformed optional metadata leaves items open.";
                schema["minimum"] = 5; schema["maximum"] = 1440;
            }
            if (type == typeof(AppConfig) && property.Name == nameof(AppConfig.Hooks))
                schema["description"] = "No commands by default. Direct appsettings edit and restart only; all automation writes, leases and restore substitutions are forbidden. Commands run after completion with static argument lists, bounded output and a kill-tree timeout.";
            if (type == typeof(NukeWatchConfig))
            {
                schema["description"] = property.Name switch
                {
                    nameof(NukeWatchConfig.Enabled) => "Default true. Passive flags for recently completed/imported owned releases using existing PreDB responses and trusted sitebot observations. No new polling, FTP, file scans or library changes.",
                    nameof(NukeWatchConfig.AutoQueueFixes) => "Default false. Queue same-tier exact-identity PROPER/newer same-group REPACK only for quality nukes with current trusted completed source. Uses normal download staging; never replaces original library files. Independent of wanted decision windows.",
                    nameof(NukeWatchConfig.WatchDays) => "Default 7 days from original completion/import, not last restart. Passive ownership retention only; does not expire or throttle engine work.",
                    nameof(NukeWatchConfig.HistoryCapacity) => "Default 2048 entries per passive collection. Oldest eviction bounds memory/disk only; storage failure never blocks downloads or correction admission.",
                    _ => "Default 256 pending observations with oldest eviction. Bounded passive background work only; no transfer limit. Restart applies capacity changes."
                };
                if (property.Name == nameof(NukeWatchConfig.WatchDays)) { schema["minimum"] = 1; schema["maximum"] = 90; }
                if (property.Name == nameof(NukeWatchConfig.HistoryCapacity)) { schema["minimum"] = 1; schema["maximum"] = 10000; }
                if (property.Name == nameof(NukeWatchConfig.QueueCapacity)) { schema["minimum"] = 1; schema["maximum"] = 4096; }
            }
            if (type == typeof(HookConfig) || type == typeof(HookCommandConfig)) schema["readOnly"] = true;
            if (type == typeof(AlertTargetConfig) && property.Name == nameof(AlertTargetConfig.HookEvents))
                schema["description"] = "Empty by default. Generic HTTPS webhook targets opt in to download.complete, download.failed, race.finished, wishlist.grab and nuke. Reuses bounded alert delivery and redaction; no local paths or server identity in payloads.";
            if (type == typeof(AgentConfig))
            {
                schema["description"] = property.Name switch
                {
                    nameof(AgentConfig.AccountabilityEnabled) => "Default true: passive expected-effect and outcome reporting from local telemetry, with read-only user correction signals. Missing declarations are unscored.",
                    nameof(AgentConfig.NoOpRejectionEnabled) => "Default true: reject proposals whose detached validator projection or effective transfer capacity changes nothing. Existing settings and engine work continue unchanged.",
                    nameof(AgentConfig.CounterfactualReplayEnabled) => "Default true: local replay evidence is required for agent rule, exclusion and mapping proposals; unavailable/unsafe evidence keeps existing settings. Direct user Settings/config.patch edits remain available.",
                    nameof(AgentConfig.AutoRevertRegressions) => "Default false: automatic scalar regression reverts require opt-in, the same validator, unchanged applied value and fresh frozen.json checks at commit. No append/delete inverse is automatic.",
                    nameof(AgentConfig.OutcomeMinimumSamples) => "Default 10 measured samples in each comparison window. Fewer samples are unscored; this is an assessment threshold, not a runtime admission limit.",
                    nameof(AgentConfig.OutcomeTolerancePercent) => "Default 10 percent relative change band for no-effect scores. Outcomes compare the same hours one week earlier and are observational, not causal proof.",
                    _ => schema["description"]?.GetValue<string>() ?? "Agent setting."
                };
                if (property.Name == nameof(AgentConfig.OutcomeMinimumSamples)) { schema["minimum"] = 1; schema["maximum"] = 1000000; }
                if (property.Name == nameof(AgentConfig.OutcomeTolerancePercent)) { schema["minimum"] = 0; schema["maximum"] = 100; }
            }
            if (type == typeof(ConfigHistoryConfig) && property.Name == nameof(ConfigHistoryConfig.RetentionCount))
            {
                schema["minimum"] = 1;
                schema["maximum"] = 10000;
            }
            if (type == typeof(LibraryConfig))
                schema["description"] = property.Name switch
                {
                    nameof(LibraryConfig.Enabled) => "Default false. Explicit staged-file library imports only. Every disabled-to-enabled save forces dryRun=true; a separate later save is required to permit moves.",
                    nameof(LibraryConfig.DryRun) => "Default true. Plan only; never create library folders, move files or request Plex scans. Disabling dry-run requires an already enabled library setting.",
                    nameof(LibraryConfig.ScanPlexAfterImport) => "Default false. After a verified import, scan only its containing folder when it maps uniquely beneath an advertised Plex library root. Never scan a whole library or delete Plex items.",
                    nameof(LibraryConfig.HashVerifyMaxBytes) => "Default 67108864 bytes (64 MiB). Verify size for every import and SHA-256 for files up to this size. Source identity stays locked until destination verification finishes.",
                    _ => "Empty by default. Explicit local absolute directory. Staging and library roots must be separate, with no reparse points; validation repeats before destructive import."
                };
            if (new NullabilityInfoContext().Create(property).WriteState == NullabilityState.Nullable)
                schema = new JsonObject { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) };
            if (type == typeof(DownloadConfig) && property.Name == nameof(DownloadConfig.MaxConcurrentDownloads))
            {
                schema["minimum"] = ConfigurationLimits.MinConcurrentDownloads;
                schema["maximum"] = ConfigurationLimits.MaxConcurrentDownloads;
            }
            if (type == typeof(DownloadConfig) && property.Name == nameof(DownloadConfig.ScoredQualityEnabled))
                schema["description"] = "Opt in to scored wishlist acquisition, pending-copy dedupe and successful-download cutoff completion. Default false preserves existing resolution-only decisions.";
            if (type == typeof(DownloadConfig) && property.Name == nameof(DownloadConfig.BlockLowQualityWishlistSources))
                schema["description"] = "Default true. Wishlist CAM/TS/TC floor; explicit per-item permission or a selected configured profile allowing low sources is preserved. Permitted low sources never satisfy completion cutoff. Does not affect manual downloads or races.";
            if (type == typeof(DownloadConfig) && property.Name == nameof(DownloadConfig.MovieCalendarRegion))
            {
                schema["description"] = "Two-letter region for explicit TMDB movie release-date refresh, default US to match Upcoming. No background polling; dates are advisory and unknown never blocks a grab.";
                schema["pattern"] = "^[a-zA-Z]{2}$";
            }
            if (type == typeof(DownloadConfig) && property.Name == nameof(DownloadConfig.SfvRepairEnabled))
                schema["description"] = "Default false. Re-fetch only CRC-failing files in staging created and recorded by this download, using its normal queue/pool. Persisted budgets survive restarts and manual retries; known bad owned staging remains held from extraction until verification succeeds.";
            if (type == typeof(DownloadConfig) && property.Name == nameof(DownloadConfig.SfvRepairMaxAttempts))
            {
                schema["minimum"] = 1;
                schema["maximum"] = 2;
                schema["description"] = "Default 2 attempts per file per download, only while sfvRepairEnabled. An identical wrong CRC after a re-fetch stops early. Never resets on restart or Retry.";
            }
            if (type == typeof(PoolConfig) && property.Name == nameof(PoolConfig.LoginRateLimitEnabled))
                schema["description"] = "Default true. Persist and enforce an account-wide five-minute budget for new guarded FTP logins, including successful attempts. Existing pooled borrows are unaffected.";
            if (type == typeof(PoolConfig) && property.Name == nameof(PoolConfig.MaxLoginAttemptsPerFiveMinutes))
            {
                schema["minimum"] = 2;
                schema["maximum"] = 100;
                schema["description"] = "Default 8 attempts per account per five minutes; one slot is reserved for the existing bounded ghost recovery. This is separate from the simultaneous login cap.";
            }
            if (type == typeof(HealthConfig))
                schema["description"] = property.Name switch
                {
                    nameof(HealthConfig.Enabled) => "Default true. Persist site outage retry state: existing 30-second to 5-minute ladder for 30 minutes, then 15 minutes, 1 hour and 4 hours. One outage and one recovery notice.",
                    nameof(HealthConfig.TcpProbesEnabled) => "Default false. Optional TCP-only reachability checks for configured unreachable/timed-out sites, no more than once per 15 minutes, using the configured SOCKS5 proxy. Never logs in or scans the LAN.",
                    nameof(HealthConfig.AnnounceSilenceAlertsEnabled) => "Default true. Passive channel-specific hour-of-week silence learning with conservative training and silence floors; no extra FTP activity.",
                    _ => "Default false. Bounded recovery for missing configured announce channels through the existing IRC join/invite path; never tears down a joined but silent session."
                };
            if (type == typeof(DownloadConfig) && property.Name == nameof(DownloadConfig.WantedDecisionWindowEnabled))
                schema["description"] = "Default false. Collect wanted variants across sites; after the fixed window select the best with a trusted sitebot completion hint. The download still verifies completeness. NEW and cached directories alone do not establish completion.";
            if (type == typeof(DownloadConfig) && property.Name == nameof(DownloadConfig.WantedIndexSweepsEnabled))
                schema["description"] = "Default false. Sweep existing search snapshots on wanted-list edits and index publication, without FTP work. Expose bounded suggestions; auto acquisition additionally requires the decision window and a trusted completion hint.";
            if (type == typeof(DownloadConfig) && property.Name == nameof(DownloadConfig.PreferFastestWantedSource))
                schema["description"] = "Default false. Break equal quality choices using measured source-to-local SpeedTracker throughput, never guessed LAN speeds. Applies to wanted windows and season plans.";
            if (type == typeof(DownloadConfig) && property.Name == nameof(DownloadConfig.RaceCompletedPullsEnabled))
                schema["description"] = "Default false. Offer verified complete receiving race destinations to monitored series opted into autoPullCompletedRaces. Requires wantedDecisionWindowEnabled and explicit racePullServerIds. Pulls use existing pools and yield to races.";
            if (type == typeof(DownloadConfig) && property.Name == nameof(DownloadConfig.RacePullServerIds))
                schema["description"] = "Explicit eligible pull-source server ids; empty by default. No automatic LAN discovery or address assumptions.";
            if (type == typeof(DownloadConfig) && property.Name is nameof(DownloadConfig.WantedDecisionWindowMinutes) or nameof(DownloadConfig.WantedCandidateMaxAgeHours) or nameof(DownloadConfig.WantedIndexSweepLimit))
            {
                schema["minimum"] = 1;
                schema["maximum"] = property.Name == nameof(DownloadConfig.WantedDecisionWindowMinutes) ? 60 : property.Name == nameof(DownloadConfig.WantedCandidateMaxAgeHours) ? 168 : 1000;
            }
            if (type == typeof(DownloadConfig) && property.Name == nameof(DownloadConfig.Profiles))
                schema["description"] = "User-defined scored quality profiles selected by wishlist profileId. Empty uses built-in legacy/recommended profiles. Does not delete or replace local files.";
            if (type == typeof(SpreadConfig) && property.Name == nameof(SpreadConfig.UseSitebotCompletionHints))
                schema["description"] = "Opt in to trusted announced-count cross-checks during already scheduled destination listing verification. Default false; no extra FTP traffic and a bot line never establishes ownership.";
            if (type == typeof(SpreadConfig) && property.Name == nameof(SpreadConfig.OwnNukeAlertsEnabled))
                schema["description"] = "Default true: notify once when trusted NUKE or the existing SITE NUKES poll explicitly names this site's configured account as nukee. Passive, exact attribution; disabling alerts does not disable protection of known nuked releases.";
            if (type == typeof(SpreadConfig) && property.Name == nameof(SpreadConfig.UseSitebotMetadata))
                schema["description"] = "Opt in to exact-source/release bot metadata before HTTP lookup only when every active filter has evidence. Default false.";
            if (type == typeof(SpreadConfig) && property.Name == nameof(SpreadConfig.SizeAwareRaceQueue))
                schema["description"] = "Default true: nonpreemptive highest-response-ratio queue using trusted announced sizes, existing speeds and historical medians. Existing cleanup/fresh/resume priorities and due times remain authoritative; no extra slots or FTP traffic. False restores FIFO within those priorities.";
            if (type == typeof(SpreadConfig) && property.Name == nameof(SpreadConfig.MergeRaceAnnounces))
                schema["description"] = "Default true: duplicate announces merge participants and valid source hints into queued/starting/active requests. Existing source/destination roles, pinned active paths, login budgets and cleanup ownership are preserved. False retains first-request-wins behavior.";
            if (type == typeof(SpreadConfig) && property.Name == nameof(SpreadConfig.RaceQueueMaxAgeMinutes))
            {
                schema["minimum"] = 1;
                schema["maximum"] = 1440;
                schema["description"] = "Oldest-first override age within each lifetime priority class. Default 120 minutes. Does not override probe backoff, partial cleanup deadlines or available pools.";
            }
            if (type == typeof(SitebotChannelConfig) && property.Name == nameof(SitebotChannelConfig.BotNick))
                schema["description"] = "Exact trusted nickname on this configured channel. Empty (default) disables passive decoding; optional userHostPattern further restricts identity.";
            if (type == typeof(SpreadConfig) && property.Name == nameof(SpreadConfig.AbsoluteRaceTimeoutMinutes))
            {
                schema["minimum"] = 1;
                schema["maximum"] = 1440;
                schema["description"] = "Absolute active race attempt cap in minutes, including while payload progresses. Default 90.";
            }
            if (type == typeof(SpreadConfig) && property.Name == nameof(SpreadConfig.PartialRaceDeadlineHours))
            {
                schema["minimum"] = 1;
                schema["maximum"] = 11;
                schema["description"] = "Finish or clean GlDrive-created partial releases within this many hours from first upload. Default 10.";
            }
            if (type == typeof(SpreadConfig) && property.Name == nameof(SpreadConfig.HardTimeoutSeconds))
                schema["description"] = "Seconds without payload byte progress before stopping; default 1200. This is an idle timeout, not a wall-clock ceiling.";
            else if (type == typeof(DownloadConfig) && property.Name == nameof(DownloadConfig.CategoryPaths))
                schema["description"] = "Download root precedence: exact raw section key, then case-insensitive logical TV/Movies inferred from the release, then localPath. A logical root suppresses the extra TV/Movies layout folder; exact section mappings retain their existing layout. Explicit destinations are preserved.";
            else if (type == typeof(MultiStreamConfig) && property.Name == nameof(MultiStreamConfig.HttpLanesPerNetwork))
            {
                schema["minimum"] = ConfigurationLimits.MinHttpLanesPerNetwork;
                schema["maximum"] = ConfigurationLimits.MaxHttpLanesPerNetwork;
            }
            if (ConfigSecretPointers.IsSecret(name)) schema["writeOnly"] = true;
            properties[name] = schema;
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
    }

    internal static void Validate(AppConfig config)
    {
        ValidateObject(config, "");
        if (config.IrcAttention.UnparsedJson == null)
        {
            foreach (var words in new[] { config.IrcAttention.Keywords, config.IrcAttention.IgnoredNicks })
                if (words.Count > 64 || words.Any(word => word == null || word.Length > 128))
                    throw new ArgumentException("/ircAttention lists allow at most 64 strings of at most 128 characters");
        }
        Range(config.Plex.WatchlistSyncMinutes, 5, 1440, "/plex/watchlistSyncMinutes");
        Range(config.NukeWatch.WatchDays, 1, 90, "/nukeWatch/watchDays");
        Range(config.NukeWatch.HistoryCapacity, 1, 10000, "/nukeWatch/historyCapacity");
        Range(config.NukeWatch.QueueCapacity, 1, 4096, "/nukeWatch/queueCapacity");
        // Hooks are unchanged by automation and validated independently by their runner.
        // An optional invalid draft must not prevent settings edits or lease expiry.
        if (config.Library.HashVerifyMaxBytes < 0)
            throw new ArgumentException("/library/hashVerifyMaxBytes must be nonnegative");
        Range(config.ConfigHistory.RetentionCount, 1, 10000, "/configHistory/retentionCount");
        Range(config.Alerts.HistoryCapacity, 1, 100000, "/alerts/historyCapacity");
        Range(config.Alerts.QueueCapacity, 1, 100000, "/alerts/queueCapacity");
        Range(config.Alerts.RetryCount, 0, 5, "/alerts/retryCount");
        Range(config.Alerts.CoalesceMinutes, 1, 1440, "/alerts/coalesceMinutes");
        Range(config.Alerts.QuietStartHour, 0, 23, "/alerts/quietStartHour");
        Range(config.Alerts.QuietEndHour, 0, 23, "/alerts/quietEndHour");
        Range(config.Alerts.DigestHourLocal, 0, 23, "/alerts/digestHourLocal");
        Range(config.Alerts.HeartbeatMinutes, 1, 1440, "/alerts/heartbeatMinutes");
        if (config.Alerts.Targets.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != config.Alerts.Targets.Count)
            throw new ArgumentException("Alert target identifiers must be unique");
        foreach (var target in config.Alerts.Targets)
        {
            if (!Regex.IsMatch(target.Id, "^[a-zA-Z0-9_-]{1,64}$") ||
                target.Kind is not ("ntfy" or "pushover" or "telegram" or "discord" or "smtp" or "heartbeat" or "webhook"))
                throw new ArgumentException("Invalid alert target identifier or kind");
            Range(target.MinimumSeverity, 0, 2, "/alerts/targets/minimumSeverity");
            if (target.HookEvents.Any(e => !Hooks.HookEvent.Names.Contains(e))) throw new ArgumentException("Unknown hook event");
        }
        if (config.Servers.Any(s => string.IsNullOrWhiteSpace(s.Id)) || config.Servers.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != config.Servers.Count)
            throw new ArgumentException("Every server needs a nonempty, unique id");
        Range(config.ControlApi.Port, 1, 65535, "/controlApi/port");
        AutomationSecurityConfig.Validate(config.ControlApi.Security);
        RecoveryConfig.Validate(config.Recovery);
        if (config.ControlApi.Enabled && config.ControlApi.Token.Length < 32) throw new ArgumentException("An enabled control API requires a token of at least 32 characters");
        Range(config.Downloads.MaxConcurrentDownloads, ConfigurationLimits.MinConcurrentDownloads, ConfigurationLimits.MaxConcurrentDownloads, "/downloads/maxConcurrentDownloads");
        Range(config.Downloads.SfvRepairMaxAttempts, 1, 2, "/downloads/sfvRepairMaxAttempts");
        if (config.Downloads.MovieCalendarRegion is not { Length: 2 } || !config.Downloads.MovieCalendarRegion.All(char.IsAsciiLetter))
            throw new ArgumentException("/downloads/movieCalendarRegion must be a two-letter region code");
        Range(config.Downloads.ScheduleStartHour, 0, 23, "/downloads/scheduleStartHour");
        Range(config.Downloads.ScheduleEndHour, 0, 23, "/downloads/scheduleEndHour");
        Range(config.Downloads.WantedDecisionWindowMinutes, 1, 60, "/downloads/wantedDecisionWindowMinutes");
        Range(config.Downloads.WantedCandidateMaxAgeHours, 1, 168, "/downloads/wantedCandidateMaxAgeHours");
        Range(config.Downloads.WantedIndexSweepLimit, 1, 1000, "/downloads/wantedIndexSweepLimit");
        if (config.Downloads.RaceCompletedPullsEnabled && !config.Downloads.WantedDecisionWindowEnabled)
            throw new ArgumentException("Race-completed pulls require the wanted decision window.");
        if (config.Downloads.RacePullServerIds.Count > 100 || config.Downloads.RacePullServerIds.Any(id =>
                string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl)))
            throw new ArgumentException("Race pull server ids must be nonempty bounded identifiers (at most 100).");
        if (config.Downloads.Profiles.Count > 100 || config.Downloads.Profiles.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Downloads.Profiles.Count)
            throw new ArgumentException("Quality profiles require unique ids and at most 100 entries.");
        foreach (var profile in config.Downloads.Profiles)
        {
            var errors = ReleaseScorer.ValidateProfile(profile);
            if (errors.Count > 0) throw new ArgumentException("Invalid quality profile: " + string.Join("; ", errors));
        }
        Range(config.Downloads.MultiStream.HttpLanesPerNetwork, ConfigurationLimits.MinHttpLanesPerNetwork, ConfigurationLimits.MaxHttpLanesPerNetwork, "/downloads/multiStream/httpLanesPerNetwork");
        Range(config.Agent.RunHourLocal, 0, 23, "/agent/runHourLocal");
        Range(config.Agent.OutcomeMinimumSamples, 1, 1000000, "/agent/outcomeMinimumSamples");
        Range(config.Agent.OutcomeTolerancePercent, 0, 100, "/agent/outcomeTolerancePercent");
        Range(config.Agent.ConfidenceThreshold_x100, 0, 100, "/agent/confidenceThreshold_x100");
        Range(config.Spread.MaxConcurrentRaces, 1, 100, "/spread/maxConcurrentRaces");
        Range(config.Spread.SpreadPoolSize, 1, 100, "/spread/spreadPoolSize");
        Range(config.Spread.AbsoluteRaceTimeoutMinutes, 1, 1440, "/spread/absoluteRaceTimeoutMinutes");
        Range(config.Spread.PartialRaceDeadlineHours, 1, 11, "/spread/partialRaceDeadlineHours");
        Range(config.Spread.RaceQueueMaxAgeMinutes, 1, 1440, "/spread/raceQueueMaxAgeMinutes");
        if (!new[] { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" }.Contains(config.Logging.Level, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Unknown logging level");
        foreach (var server in config.Servers)
        {
            var prefix = $"server {server.Id}";
            Range(server.Connection.Port, 1, 65535, prefix + " connection port");
            Range(server.Irc.Port, 1, 65535, prefix + " IRC port");
            if (server.Connection.Proxy != null) Range(server.Connection.Proxy.Port, 1, 65535, prefix + " proxy port");
            foreach (var port in server.Connection.PassivePorts) Range(port, 1, 65535, prefix + " passive port");
            if (server.Mount.DriveLetter.Length != 1 || !char.IsAsciiLetter(server.Mount.DriveLetter[0])) throw new ArgumentException(prefix + " driveLetter must be one letter");
            Range(server.Pool.PoolSize, 1, 100, prefix + " poolSize");
            Range(server.Pool.MaxLoginAttemptsPerFiveMinutes, 2, 100, prefix + " maxLoginAttemptsPerFiveMinutes");
            Range(server.Pool.LoginCap, 1, 100, prefix + " loginCap");
            if (server.Pool.LoginHeadroom >= server.Pool.LoginCap) throw new ArgumentException(prefix + " loginHeadroom must be smaller than loginCap");
            foreach (var rule in server.Irc.AnnounceRules) ValidateRegex(rule.Pattern);
            foreach (var channel in server.Irc.Channels)
            {
                var bot = channel.Sitebot;
                if (bot == null || bot.BotNick == null || bot.UserHostPattern == null || bot.Templates == null)
                    throw new ArgumentException("Sitebot settings and their fields cannot be null.");
                if (bot.BotNick.Length > 128 || bot.BotNick.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
                    bot.UserHostPattern.Length > 256 || bot.UserHostPattern.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || bot.Templates.Count > 32)
                    throw new ArgumentException("Invalid sitebot identity or excessive template count.");
                foreach (var template in bot.Templates)
                {
                    var errors = SitebotEventParser.ValidateTemplate(template);
                    if (errors.Count > 0) throw new ArgumentException("Invalid sitebot template: " + string.Join("; ", errors));
                }
            }
            ValidateRegex(server.Irc.RequestFiller.Pattern);
            foreach (var mapping in server.SpreadSite.SectionMappings) ValidateRegex(mapping.TriggerRegex);
        }
    }

    private static void ValidateCaptureFlag(JsonNode next)
    {
        if (next["logging"] is JsonObject logging && logging.TryGetPropertyValue("incidentCaptureEnabled", out var flag) &&
            (flag is not JsonValue value || !value.TryGetValue<bool>(out _)))
            throw new ArgumentException("/logging/incidentCaptureEnabled must be a boolean");
    }

    private static void ValidateObject(object? value, string path)
    {
        if (path == "/hooks") return;
        if (value == null) return;
        if (value is string text) { if (text.Length > 32768) throw new ArgumentException($"Settings string is too long at {path}"); return; }
        if (value is int number && number < 0) throw new ArgumentException($"Negative value is invalid at {path}");
        if (value is double real && (!double.IsFinite(real) || real < 0)) throw new ArgumentException($"Invalid numeric value at {path}");
        if (value.GetType().IsEnum && !Enum.IsDefined(value.GetType(), value)) throw new ArgumentException($"Unknown enum value at {path}");
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value == null) throw new ArgumentException($"Null dictionary value at {path}");
                ValidateObject(entry.Value, path + "/" + entry.Key);
            }
        }
        else if (value is IEnumerable enumerable)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                if (item == null) throw new ArgumentException($"Null array item at {path}/{index}");
                ValidateObject(item, path + "/" + index++);
            }
        }
        else if (value.GetType().Namespace == typeof(AppConfig).Namespace && !value.GetType().IsEnum)
            foreach (var property in Properties(value.GetType()))
            {
                var child = property.GetValue(value);
                var pointer = path + "/" + JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                if (pointer == "/hooks") continue;
                if (child == null && new NullabilityInfoContext().Create(property).WriteState == NullabilityState.NotNull)
                    throw new ArgumentException($"Null is invalid at {pointer}");
                ValidateObject(child, pointer);
            }
    }

    private static void ValidateRegex(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        try { _ = new Regex(value, RegexOptions.None, TimeSpan.FromMilliseconds(250)); }
        catch (ArgumentException) { throw new ArgumentException("A configured regular expression is invalid"); }
    }

    private static void Range(int value, int minimum, int maximum, string path)
    {
        if (value < minimum || value > maximum) throw new ArgumentException($"{path} must be between {minimum} and {maximum}");
    }
}
