using System.Text.Json;

namespace GlDrive.Services.Automation;

public interface IAutomationTargetDescription
{
    string? ApprovalTargetName(string tool, JsonElement arguments);
}

internal static class AutomationApprovalDescription
{
    private static readonly HashSet<string> FtpVerbs = ["SITE", "DELE", "RMD", "MKD", "RNFR", "RNTO", "STOR", "APPE", "RETR", "LIST", "USER", "PASS", "QUIT", "CWD", "CDUP", "PWD", "NOOP", "TYPE", "MODE", "PORT", "EPRT", "PASV", "EPSV"];
    private static readonly HashSet<string> SiteVerbs = ["WIPE", "CHPASS", "PASSWD", "ADDUSER", "DELUSER", "CHUSER", "ADDIP", "DELIP", "NUKE", "UNNUKE", "RENUKE", "REQUEST", "GADDUSER", "GDELUSER", "KICK", "KILL", "RESTART", "SHUTDOWN", "WHO", "USERS", "USER", "STATS", "NUKES", "HELP", "INVITE"];

    internal static string Describe(string tool, JsonElement args, string reason, Func<string, string?>? serverName,
        string? targetName = null)
    {
        var details = new List<string> { reason };
        foreach (var field in new[] { "serverId", "sourceServerId", "destinationServerId" })
            if (Text(args, field) is { } id)
            {
                var label = serverName?.Invoke(id);
                details.Add(label == null ? field + ": " + Display(id) : field + ": " + Display(label) + " (" + Display(id) + ")");
            }
        foreach (var field in new[] { "path", "localPath", "destinationPath", "sourcePath", "root", "target", "pointer", "kind", "service", "name", "id", "releaseName", "section", "status" })
        {
            if (Text(args, field) is { } value) details.Add(field + ": " + Display(value));
        }
        if (targetName != null) details.Add("selected item: " + Display(targetName));
        if (tool == "downloads.clear_finished" && Text(args, "status") == null) details.Add("status: all finished downloads");
        if (tool == "irc.raw")
        {
            var words = (Text(args, "line") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var start = words.FirstOrDefault()?.StartsWith(':') == true ? 1 : 0;
            var verb = words.ElementAtOrDefault(start)?.ToUpperInvariant() ?? "";
            var hasTarget = verb is "PRIVMSG" or "NOTICE" or "JOIN" or "PART" or "KICK" or "MODE" or "TOPIC" or "INVITE" or "WHO" or "WHOIS" or "NAMES" or "NICK";
            details.Add("IRC command: " + (hasTarget || verb is "PASS" or "AUTHENTICATE" or "QUIT" or "CAP" or "PING" or "PONG" ? verb : "unrecognized") + " (body omitted)");
            if (hasTarget && words.ElementAtOrDefault(start + 1) is { } target && !target.StartsWith(':'))
                details.Add("IRC target: " + Display(target));
            if (verb == "INVITE" && words.ElementAtOrDefault(start + 2) is { } channel && !channel.StartsWith(':'))
                details.Add("IRC channel: " + Display(channel));
        }
        if (tool == "ftp.command")
        {
            var words = (Text(args, "command") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var verb = words.FirstOrDefault()?.ToUpperInvariant() ?? "";
            var command = FtpVerbs.Contains(verb) ? verb : "unrecognized FTP command";
            if (verb == "SITE") command += words.Length > 1 && SiteVerbs.Contains(words[1].ToUpperInvariant()) ? " " + words[1].ToUpperInvariant() : " (unrecognized subcommand)";
            details.Add("command: " + command + " (arguments omitted)");
        }
        if (tool == "config_patch" && args.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array)
            foreach (var operation in operations.EnumerateArray().Take(100))
            {
                var action = Text(operation, "op");
                details.Add((action is "add" or "replace" or "remove" or "test" ? action : "change") + " " + Display(Text(operation, "path") ?? "[unknown pointer]") + " (value omitted)");
            }
        if (tool == "installer.profile_set" && args.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object)
            details.Add("Installer profile fields will be changed; credential values are omitted.");
        if (tool == "cleanup.delete" && args.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            details.Add("files: " + files.GetArrayLength());
            foreach (var file in files.EnumerateArray().Take(10)) if (file.ValueKind == JsonValueKind.String) details.Add(Display(file.GetString()!));
            if (files.GetArrayLength() > 10) details.Add("Additional selected files omitted; deny if this selection is unexpected.");
        }
        return string.Join("\n", details);
    }
    private static string Display(string value) => value.Length > 1024 ? "[value exceeds display limit]" : new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    private static string? Text(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
