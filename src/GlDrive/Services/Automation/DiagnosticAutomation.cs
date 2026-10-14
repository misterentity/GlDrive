using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GlDrive.Config;

namespace GlDrive.Services.Automation;

/// <summary>Bounded reads of app-owned diagnostics; never an arbitrary filesystem API.</summary>
public sealed class DiagnosticAutomation : IAutomationModule
{
    private readonly AppConfig _config;
    private readonly string _root;
    public DiagnosticAutomation(AppConfig config, string? appDataRoot = null)
    {
        _config = config;
        _root = Path.GetFullPath(appDataRoot ?? ConfigManager.AppDataPath);
    }

    public IReadOnlyList<AutomationToolDefinition> Tools { get; } =
    [
        AutomationToolDefinition.Create("diagnostics_files", "List bounded app log, telemetry, or agent brief files. Returns names accepted by diagnostics_read; credentials are never included.",
            new { type = "object", properties = new { kind = new { type = "string", @enum = new[] { "logs", "telemetry", "briefs" } }, limit = new { type = "integer", minimum = 1, maximum = 200 } }, additionalProperties = false }, readOnly: true),
        AutomationToolDefinition.Create("diagnostics_read", "Read a bounded tail of one app-owned log, telemetry, or agent brief. Omit name for the newest file. Pass nextBeforeByte back as beforeByte to page backwards through history. Known credentials and token/password assignments are redacted. Does not accept paths.",
            new { type = "object", properties = new { kind = new { type = "string", @enum = new[] { "logs", "telemetry", "briefs" } }, name = new { type = "string" }, lines = new { type = "integer", minimum = 1, maximum = 1000 }, maxBytes = new { type = "integer", minimum = 1024, maximum = 524288 }, beforeByte = new { type = "integer", minimum = 0 } }, additionalProperties = false }, readOnly: true)
    ];

    public Task<object?> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var kind = ConfigurationAutomation.String(arguments, "kind") ?? "logs";
        return Task.FromResult<object?>(operation switch
        {
            "diagnostics_files" => ListFiles(kind, ConfigurationAutomation.Integer(arguments, "limit", 50)),
            "diagnostics_read" => Read(kind, ConfigurationAutomation.String(arguments, "name"), ConfigurationAutomation.Integer(arguments, "lines", 100), ConfigurationAutomation.Integer(arguments, "maxBytes", 131072), arguments.TryGetProperty("beforeByte", out var before) ? before.GetInt64() : null),
            _ => throw new ArgumentException("Unknown diagnostics operation")
        });
    }

    public object ListFiles(string kind = "logs", int limit = 50)
    {
        var (directory, pattern) = Location(kind);
        EnsureDirectory(directory);
        var files = !Directory.Exists(directory) ? [] : new DirectoryInfo(directory).EnumerateFiles(pattern)
            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(f => f.LastWriteTimeUtc).Take(Math.Clamp(limit, 1, 200))
            .Select(f => new { name = f.Name, sizeBytes = f.Length, modifiedUtc = f.LastWriteTimeUtc }).ToArray();
        return new { kind, files };
    }

    public object Read(string kind = "logs", string? name = null, int lines = 100, int maxBytes = 131072, long? beforeByte = null)
    {
        var (directory, pattern) = Location(kind);
        EnsureDirectory(directory);
        if (name == null)
            name = Directory.Exists(directory) ? new DirectoryInfo(directory).EnumerateFiles(pattern)
                .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.Name : null;
        if (name == null) return new { kind, name, text = "", truncated = false, redacted = true };
        ValidateName(name);
        if (!System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
            throw new ArgumentException("This file is not an allowed diagnostics file");
        var file = Path.Combine(directory, name);
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Linked diagnostics files are not supported");
        var tail = ReadTail(file, Math.Clamp(lines, 1, 1000), Math.Clamp(maxBytes, 1024, 524288), beforeByte);
        return new { kind, name, text = RedactText(tail.Text), tail.Truncated, tail.SizeBytes, nextBeforeByte = tail.NextBeforeByte, redacted = true };
    }

    private (string Directory, string Pattern) Location(string kind) => kind switch
    {
        "logs" => (Path.Combine(_root, "logs"), "gldrive-*.log"),
        "telemetry" => (Path.Combine(_root, "ai-data"), "*.jsonl"),
        "briefs" => (Path.Combine(_root, "ai-data", "ai-briefs"), "*.md"),
        _ => throw new ArgumentException("kind must be logs, telemetry, or briefs")
    };

    internal void EnsureDirectory(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current != null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Linked diagnostics directories are not supported");
            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), _root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
            current = current.Parent;
        }
        throw new ArgumentException("Diagnostics path escaped the application data directory");
    }

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.IndexOfAny(['/', '\\', ':']) >= 0 || name is "." or "..")
            throw new ArgumentException("Supply a file name from diagnostics_files, not a path");
    }

    internal sealed record Tail(string Text, bool Truncated, long SizeBytes, long NextBeforeByte = 0);
    internal static Tail ReadTail(string path, int lines, int maxBytes, long? beforeByte = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var size = stream.Length;
        var end = beforeByte ?? size;
        if (end < 0 || end > size) throw new ArgumentException("beforeByte is outside the current file; restart pagination if the file rotated");
        var count = (int)Math.Min(end, maxBytes);
        var offset = end - count;
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, total, count - total);
            if (read == 0) break;
            total += read;
        }
        var discard = 0;
        if (offset > 0)
        {
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, total);
            if (newline < 0) return new Tail("", true, size, offset);
            discard = newline + 1;
        }
        else if (total >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) discard = 3;
        var text = Encoding.UTF8.GetString(buffer, discard, total - discard);
        var all = text.TrimEnd('\r', '\n').Split('\n');
        var truncated = offset > 0 || all.Length > lines;
        var skipped = all.Length - Math.Min(lines, all.Length);
        var next = truncated ? offset + discard + (skipped > 0 ? Encoding.UTF8.GetByteCount(string.Join("\n", all.Take(skipped)) + "\n") : 0) : 0;
        return new Tail(string.Join("\n", all.TakeLast(lines)), truncated, size, next);
    }

    internal string RedactText(string text) => CreateTextRedactor()(text);

    internal Func<string, string> CreateTextRedactor()
    {
        // Strip known current credentials even when a third-party log message lacks labels.
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? value) { if (!string.IsNullOrEmpty(value)) secrets.Add(value); }
        Add(_config.ControlApi.Token);
        foreach (var server in _config.Servers)
        {
            foreach (var channel in server.Irc.Channels) Add(channel.Key);
            Add(CredentialStore.GetPassword(server.Connection.Host, server.Connection.Port, server.Connection.Username));
            Add(CredentialStore.GetIrcPassword(server.Irc.Host, server.Irc.Port, server.Irc.Nick));
            if (server.Connection.Proxy is { } proxy) Add(CredentialStore.GetProxyPassword(proxy.Host, proxy.Port, proxy.Username));
        }
        foreach (var service in new[] { "omdb", "tmdb", "openrouter", "plex" }) Add(CredentialStore.GetApiKey(service));
        var ordered = secrets.OrderByDescending(s => s.Length).ToArray();
        return text =>
        {
            foreach (var secret in ordered) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
            return Regex.Replace(text, "(?i)(authorization\\s*[:=]\\s*bearer\\s+|(?:password|passphrase|token|api[_-]?key|secret|key)\\s*[\\\"']?\\s*[:=]\\s*[\\\"']?)[^\\s\\\"',;}]+", "$1[redacted]", RegexOptions.None, TimeSpan.FromMilliseconds(250));
        };
    }

    internal JsonNode? RedactJson(JsonNode? node, Func<string, string>? redactText = null)
    {
        if (node == null) return null;
        ConfigurationAutomation.Redact(node, false);
        return RedactStrings(node, redactText ?? CreateTextRedactor());
    }

    private static JsonNode? RedactStrings(JsonNode? node, Func<string, string> redactText)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return JsonValue.Create(redactText(text));
        if (node is JsonObject obj)
            foreach (var key in obj.Select(p => p.Key).ToList()) obj[key] = RedactStrings(obj[key], redactText)?.DeepClone();
        else if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++) array[i] = RedactStrings(array[i], redactText)?.DeepClone();
        return node;
    }
}
