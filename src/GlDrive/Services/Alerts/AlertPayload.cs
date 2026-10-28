using System.Text.Json;
using System.Text.RegularExpressions;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Irc;

namespace GlDrive.Services.Alerts;

internal static class AlertPayload
{
    // Free-form local messages and IRC text are never part of the outbound contract.
    internal static string Build(AlertEvent alert, AlertTargetConfig target, AppConfig config, IEnumerable<string> secrets)
    {
        var secretValues = secrets.SelectMany(ExpandCredential).ToArray();
        var privateValues = config.Servers.SelectMany(s => new[] { s.Connection.Host, s.Connection.Username,
            s.Connection.Proxy?.Host ?? "", s.Connection.Proxy?.Username ?? "", s.Irc.Host, s.Irc.Nick,
            s.Irc.AltNick, s.Irc.InviteNick, s.Irc.RealName }.Concat(s.Irc.Channels.Select(c => c.Name)))
            .Concat(secretValues).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderByDescending(s => s.Length).ToArray();
        string Clean(string value)
        {
            value = IrcLineRedactor.Redact(value);
            foreach (var secret in privateValues) value = value.Replace(secret, "[redacted]", StringComparison.OrdinalIgnoreCase);
            value = Regex.Replace(value, @"https?://\S+|(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])|[\w]*[#&]\S+|\b[\da-fA-F]*:[\da-fA-F:]+\b", "[redacted]");
            return value;
        }
        var displayName = config.Servers.FirstOrDefault(s => s.Id == alert.ServerId)?.Name ?? "";
        // A display label can itself contain an endpoint or an old alias.
        displayName = Regex.Replace(displayName, @"\b(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}\b", "[redacted]");
        var payload = new Dictionary<string, string>
        {
            ["severity"] = alert.Severity.ToString(), ["kind"] = alert.Kind.ToString(),
            ["server"] = Clean(displayName)
        };
        if (target.IncludeReleaseNames && alert.Release.Length > 0) payload["release"] = Clean(alert.Release);
        return OutboundPayloadRedactor.Prepare(JsonSerializer.Serialize(payload), secretValues);
    }

    private static IEnumerable<string> ExpandCredential(string value)
    {
        var values = new List<string> { value };
        try
        {
            using var document = JsonDocument.Parse(value);
            void Walk(JsonElement node)
            {
                if (node.ValueKind == JsonValueKind.String) values.Add(node.GetString()!);
                else if (node.ValueKind == JsonValueKind.Object) foreach (var child in node.EnumerateObject()) Walk(child.Value);
                else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) Walk(child);
            }
            Walk(document.RootElement);
        }
        catch (JsonException) { }
        return values;
    }
}
