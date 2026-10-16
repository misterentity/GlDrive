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
using GlDrive.Util;

namespace GlDrive.Services.Automation;

/// <summary>Typed, transactional settings edits for explicitly requested external automation.</summary>
public sealed class ConfigurationAutomation : IAutomationModule
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
    private readonly object _gate = new();
    private const string Redacted = "[redacted]";

    public ConfigurationAutomation(AppConfig config, Action<AppConfig>? persist = null, Func<AppConfig>? readCurrent = null,
        Action? settingsApplied = null)
    {
        _config = config;
        _settingsApplied = settingsApplied;
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
        AutomationToolDefinition.Create("credentials_manage", "Read presence, explicitly reveal, set, or delete app credentials in Windows Credential Manager. FTP/proxy/IRC credentials require serverId; API credentials require service (omdb, tmdb, openrouter, plex). Read returns presence only unless includeValue=true. Values are never returned on writes.",
            new { type = "object", properties = new { action = new { type = "string", @enum = new[] { "get", "set", "delete" } }, kind = new { type = "string", @enum = new[] { "ftp", "proxy", "irc", "api" } }, serverId = new { type = "string" }, service = new { type = "string", @enum = new[] { "omdb", "tmdb", "openrouter", "plex" } }, value = new { type = "string" }, includeValue = new { type = "boolean" } }, required = new[] { "action", "kind" }, additionalProperties = false }, destructive: true)
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
        runtime = "Settings are persisted atomically and shared configuration objects are updated. Restart GlDrive to fully apply connections, listeners, logging, scheduling and UI state.",
        secrets = "[redacted] is a read-only placeholder; explicit allowSecrets is required to change secrets. The control API token cannot be read; it can be rotated by a secret-enabled patch followed by restart. External credentials use credentials_manage."
    };

    public object Apply(JsonArray operations, string? expectedRevision = null, bool allowSecrets = false)
    {
        object? result = null;
        SecureFile.WithPathLock(ConfigManager.ConfigPath, () => result = ApplyCore(operations, expectedRevision, allowSecrets));
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
            if (!allowSecrets && SecretValuesChanged(original, next))
                throw new ArgumentException("Changing secrets requires allowSecrets=true");
            var candidate = next.Deserialize<AppConfig>(Json) ?? throw new ArgumentException("Settings must be an object");
            Validate(candidate);
            var canonical = JsonSerializer.SerializeToNode(candidate, Json)!;
            var changed = !JsonNode.DeepEquals(original, canonical);
            if (changed)
            {
                // Save first: a failed disk write must leave the live settings untouched.
                // ConfigManager uses SecureFile's restricted, flushed, atomic rename.
                _persist(candidate);
                Reconcile(_config, candidate);
                try { _settingsApplied?.Invoke(); }
                catch (Exception ex) { Serilog.Log.Warning(ex, "Settings saved, but applying live download rate settings failed"); }
            }
            return new { saved = changed, revision = Revision(canonical), restartRequired = changed,
                message = changed ? "Saved. Restart GlDrive to fully apply runtime settings." : "No settings changed." };
        }
    }

    public object Credentials(string action, string kind, string? serverId = null, string? service = null, string? value = null, bool includeValue = false)
    {
        lock (_gate) return CredentialsCore(action, kind, serverId, service, value, includeValue);
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
            if (service is not ("omdb" or "tmdb" or "openrouter" or "plex")) throw new ArgumentException("Unknown API credential service");
            read = () => CredentialStore.GetApiKey(service);
            write = secret => CredentialStore.SaveApiKey(service, secret);
            delete = () => CredentialStore.DeleteApiKey(service);
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
                if (childPointer == "/controlApi/token" || (!includeSecrets && ConfigSecretPointers.IsSecret(childPointer)))
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
            if (value is IList sourceList && existing is IList targetList && !property.PropertyType.IsArray)
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
            if (new NullabilityInfoContext().Create(property).WriteState == NullabilityState.Nullable)
                schema = new JsonObject { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) };
            if (type == typeof(DownloadConfig) && property.Name == nameof(DownloadConfig.MaxConcurrentDownloads))
            {
                schema["minimum"] = ConfigurationLimits.MinConcurrentDownloads;
                schema["maximum"] = ConfigurationLimits.MaxConcurrentDownloads;
            }
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
        if (config.Servers.Any(s => string.IsNullOrWhiteSpace(s.Id)) || config.Servers.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != config.Servers.Count)
            throw new ArgumentException("Every server needs a nonempty, unique id");
        Range(config.ControlApi.Port, 1, 65535, "/controlApi/port");
        if (config.ControlApi.Enabled && config.ControlApi.Token.Length < 32) throw new ArgumentException("An enabled control API requires a token of at least 32 characters");
        Range(config.Downloads.MaxConcurrentDownloads, ConfigurationLimits.MinConcurrentDownloads, ConfigurationLimits.MaxConcurrentDownloads, "/downloads/maxConcurrentDownloads");
        Range(config.Downloads.ScheduleStartHour, 0, 23, "/downloads/scheduleStartHour");
        Range(config.Downloads.ScheduleEndHour, 0, 23, "/downloads/scheduleEndHour");
        Range(config.Downloads.MultiStream.HttpLanesPerNetwork, ConfigurationLimits.MinHttpLanesPerNetwork, ConfigurationLimits.MaxHttpLanesPerNetwork, "/downloads/multiStream/httpLanesPerNetwork");
        Range(config.Agent.RunHourLocal, 0, 23, "/agent/runHourLocal");
        Range(config.Agent.ConfidenceThreshold_x100, 0, 100, "/agent/confidenceThreshold_x100");
        Range(config.Spread.MaxConcurrentRaces, 1, 100, "/spread/maxConcurrentRaces");
        Range(config.Spread.SpreadPoolSize, 1, 100, "/spread/spreadPoolSize");
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
            Range(server.Pool.LoginCap, 1, 100, prefix + " loginCap");
            if (server.Pool.LoginHeadroom >= server.Pool.LoginCap) throw new ArgumentException(prefix + " loginHeadroom must be smaller than loginCap");
            foreach (var rule in server.Irc.AnnounceRules) ValidateRegex(rule.Pattern);
            ValidateRegex(server.Irc.RequestFiller.Pattern);
            foreach (var mapping in server.SpreadSite.SectionMappings) ValidateRegex(mapping.TriggerRegex);
        }
    }

    private static void ValidateObject(object? value, string path)
    {
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
