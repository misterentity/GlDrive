using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GlDrive.Config;

namespace GlDrive.Services;

public sealed record HealthProblem(string Id, string Severity, string Title, string Evidence, string Remedy, bool FixAvailable)
{
    internal DoctorFix? Fix { get; init; }
}

internal sealed record DoctorFix(string Path, JsonNode? Before, JsonNode? After, string Impact);
public sealed record ProblemSnapshot(IReadOnlyList<HealthProblem> Problems, DateTimeOffset CheckedAtUtc, bool Partial, bool Bounded = false)
{
    internal string StatusText(bool applied = false)
    {
        var text = applied ? "The previewed change was applied. The checks have been refreshed."
            : Problems.Count == 0 ? "No standing problems were found in the available state."
            : $"{Problems.Count} conditions found. Select one to review its evidence.";
        if (Partial) text += " Some checks were unavailable or incomplete.";
        if (Bounded) text += " Frozen-decision evidence covers recent audit history only.";
        return text;
    }
}
internal sealed record DoctorServer(string Id, SiteHealthSnapshot? Health, bool Connected,
    bool MonitorEmpty, string MonitorPath, DateTime? MonitorCheckedUtc,
    bool IrcConnected, IReadOnlyList<string> JoinedChannels, IReadOnlyList<AnnounceFeedSnapshot> Feeds,
    bool FishRecoveryRequired = false);
internal sealed record DoctorDrive(string Root, string State);
internal sealed record FrozenDoctorDecision(string Id, string Target, string Timestamp, string Note);
internal sealed record DoctorCertificate(string Id, string Endpoint, DateTime DetectedAtUtc, bool Rejected);
internal sealed record DoctorContext(AppConfig Config, IReadOnlyList<DoctorServer> Servers,
    IReadOnlyList<DoctorDrive> Drives, IReadOnlyList<FrozenDoctorDecision> FrozenDecisions,
    IReadOnlyList<DoctorCertificate> Certificates, bool Partial = false, bool Bounded = false,
    bool LocalFishRecoveryRequired = false);

/// <summary>Standing conditions only. None of these rules probes a service or changes a setting.</summary>
internal static class HealthDoctor
{
    internal const int MaxProblems = 256;
    internal const int MaxServers = 128;
    internal const int MaxInputsPerServer = 256;

    internal static ProblemSnapshot Evaluate(DoctorContext context, DateTimeOffset now)
    {
        var problems = new List<HealthProblem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var partial = context.Partial || context.Config.Servers.Count > MaxServers;
        void Add(string kind, string server, string subject, string severity, string title, string evidence, string remedy, DoctorFix? fix = null)
        {
            if (problems.Count == MaxProblems) { partial = true; return; }
            var id = Id(kind, server, subject);
            if (ids.Add(id)) problems.Add(new(id, severity, title, evidence, remedy, fix != null) { Fix = fix });
        }

        var logging = context.Config.Logging.Level;
        if (context.LocalFishRecoveryRequired)
            Add("fish-storage-recovery", "", "profile", "Warning", "Local FiSH key storage needs review",
                "The startup storage check found preserved or unreadable key files, including stores for disabled or removed servers. Available keys remain usable.",
                "Open the Recovery Center in Settings to review preserved files and restore missing keys. No connections are started by the storage check.");
        if (string.Equals(logging, "Verbose", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(logging, "Debug", StringComparison.OrdinalIgnoreCase))
            Add("verbose", "", "disk", "Warning", "Verbose left on", $"Disk logging is explicitly set to {logging}; how long it has been enabled is unknown.",
                "Preview Information logging. Verbose detail remains available in the bounded flight recorder.",
                new("/logging/level", JsonValue.Create(logging), JsonValue.Create("Information"), "Changes disk logging immediately."));

        foreach (var drive in context.Drives.Take(MaxInputsPerServer))
        {
            if (drive.State == "Missing")
                Add("download-drive", "", drive.Root, "Error", "Download drive is missing", $"Configured download drive {drive.Root} is absent from the local drive list.",
                    "Reconnect the drive or choose the intended download folder in Settings. The doctor cannot choose a replacement safely.");
            else if (drive.State != "Ready")
            {
                partial = true;
                Add("download-drive-unknown", "", drive.Root, "Information", "Download drive status unknown", $"The local readiness of {drive.Root} could not be established ({drive.State}).",
                    "Check the download target in Settings. Network paths and mapped network drives are deliberately not probed.");
            }
        }

        foreach (var group in context.Config.Servers.Take(MaxServers).Where(s => s.Enabled && s.Mount.MountDrive)
            .GroupBy(s => (s.Mount.DriveLetter ?? "").TrimEnd(':').ToUpperInvariant()).Where(g => g.Key.Length == 1 && g.Count() > 1))
            Add("mount-drive-conflict", "", group.Key, "Warning", "Duplicate mount drive letter",
                $"Drive {group.Key}: is assigned to {group.Count()} enabled drive mounts.",
                "Choose distinct drive letters in Settings. Connected servers that do not mount a drive are excluded.");

        for (var index = 0; index < Math.Min(context.Config.Servers.Count, MaxServers); index++)
        {
            var server = context.Config.Servers[index];
            if (!server.Enabled) continue;
            var state = context.Servers.FirstOrDefault(s => s.Id == server.Id);
            var name = string.IsNullOrWhiteSpace(server.Name) ? server.Id : server.Name;
            if (state?.FishRecoveryRequired == true)
                Add("fish-recovery", server.Id, "keys", "Warning", $"FiSH key storage needs review: {name}",
                    "Invalid entries were skipped or unreadable key storage was preserved in quarantine. Available keys continue to work.",
                    "Open the Recovery Center in Settings to review preserved files and restore missing keys from a backup or set them again.");
            if (context.Config.Health.Enabled &&
                state is { Connected: false, Health: { FirstFailureUtc: not null, EscalationStep: > 0, State: SiteHealthState.Down or SiteHealthState.Quarantined } health } &&
                now - health.FirstFailureUtc.Value >= TimeSpan.FromMinutes(30) &&
                health.FailureClass is SiteFailureKind.Unreachable or SiteFailureKind.Timeout)
                Add("site-outage", server.Id, "ftp", "Warning", $"FTP unavailable: {name}",
                    $"Recorded {health.FailureClass} since {health.FirstFailureUtc:O}; attempts {health.Attempts}; next retry {health.NextRetryUtc:O}. This is existing login evidence, not a new probe.",
                    "Review the connection settings and recorded failure. The existing retry ladder continues automatically; disabling automatic mounting can also stop this site's racing after restart.");

            if (server.Notifications.Enabled && state is { MonitorEmpty: true, MonitorCheckedUtc: not null } &&
                state.MonitorPath == server.Notifications.WatchPath)
                Add("monitor-empty", server.Id, state.MonitorPath, "Warning", $"Monitor has zero categories: {name}",
                    $"The existing monitor observed zero categories under {state.MonitorPath} at {state.MonitorCheckedUtc:O}.",
                    "Review the notification watch path and exclusions in Settings. A replacement path requires site knowledge; no listing is requested here.");

            if (!server.SpreadSite.DownloadOnly)
            {
                var paths = server.SpreadSite.Sections.Values.Concat(server.SpreadSite.SectionMappings.Where(m => m.Enabled).Select(m =>
                    !string.IsNullOrWhiteSpace(m.Path) ? m.Path : server.SpreadSite.Sections.GetValueOrDefault(m.RemoteSection, "")))
                    .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).Take(MaxInputsPerServer + 1).ToArray();
                if (paths.Length > MaxInputsPerServer) partial = true;
                foreach (var path in paths.Take(MaxInputsPerServer))
                {
                    if (!path.StartsWith('/') || path.Length > 1024) { partial = true; continue; }
                    if (server.Search.SearchPaths.Any(root => Covers(root, path, server.Search.MaxDepth))) continue;
                    var before = new JsonArray(server.Search.SearchPaths.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
                    var after = (JsonArray)before.DeepClone();
                    after.Add(path);
                    Add("unindexed-destination", server.Id, path, "Warning", $"Destination outside index coverage: {name}",
                        $"Destination {path} is outside the configured search roots/depth. This checks configuration coverage; it does not claim the directory is absent or the index is current.",
                        "Preview adding this exact destination as a search root.",
                        new($"/servers/{index}/search/searchPaths", before, after,
                            "Applying restarts this server's crawler and immediately rebuilds the whole index across all configured search roots to the configured depth. LIST commands use the main pool shared with downloads and the release monitor; this preview issues none."));
                }
            }

            if (server.Irc.Enabled)
            {
                if (server.Irc.AnnounceRules.Count > MaxInputsPerServer) partial = true;
                foreach (var channel in server.Irc.AnnounceRules.Take(MaxInputsPerServer).Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Channel))
                    .Select(r => r.Channel).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var channelIndex = server.Irc.Channels.FindIndex(c => c.Name.Equals(channel, StringComparison.OrdinalIgnoreCase));
                    var configured = channelIndex >= 0 ? server.Irc.Channels[channelIndex] : null;
                    var joined = state?.JoinedChannels.Contains(channel, StringComparer.OrdinalIgnoreCase) == true;
                    if (joined) continue;
                    // Offline IRC has no current channel-membership evidence. Config omissions are still certain.
                    if (configured?.AutoJoin == true && state?.IrcConnected != true) continue;
                    Add("announce-channel", server.Id, channel.ToUpperInvariant(), "Warning", $"Announce channel unavailable: {name}",
                        configured == null ? $"Enabled announce rules target {channel}, which is not configured."
                            : configured.AutoJoin ? $"IRC is connected but {channel} is absent from its current channel snapshot."
                            : $"Enabled announce rules target {channel}; AutoJoin is disabled and the channel is not currently joined.",
                        configured is { AutoJoin: false } ? "Preview enabling AutoJoin on the existing configured channel."
                            : "Review channels, keys, invites and the existing IRC recovery controls in Settings. No channel is joined by this check.",
                        configured is { AutoJoin: false } ? new($"/servers/{index}/irc/channels/{channelIndex}/autoJoin", JsonValue.Create(false), JsonValue.Create(true),
                            "Restarts this server's IRC configuration and enables joining this channel through existing invite/recovery paths. May cause IRC and SITE INVITE traffic after applying.") : null);
                }
                foreach (var feed in (state?.Feeds ?? []).Take(MaxInputsPerServer).Where(f => f.BaselineReady && f.State == "Down" && f.OutageStartedAtUtc != null))
                    Add("announce-silence", server.Id, feed.Channel.ToUpperInvariant(), "Warning", $"Expected announce activity is absent: {name}",
                        $"Existing learned baseline for {feed.Channel}: expected {feed.ExpectedAnnounces:F1} announcements during {feed.ObservedSilenceMinutes:F0} minutes of silence. {feed.Diagnosis}",
                        "Review the channel's existing health evidence and IRC settings. This sentinel neither polls nor reconnects.");
            }
        }

        foreach (var decision in context.FrozenDecisions.Take(64))
            Add("frozen-decision", "", decision.Id, "Information", "Frozen decision still pending",
                $"A recorded proposal for {decision.Target} was rejected as frozen at {decision.Timestamp}. {decision.Note}",
                "Review the freeze and proposal in Agent. A freeze is an intentional restriction; this panel never removes it or applies the proposal.");
        foreach (var decision in context.Certificates.Take(MaxServers))
            Add("certificate-decision", "", decision.Id, "Warning", "Certificate decision still pending",
                $"A changed certificate for {decision.Endpoint} was observed at {decision.DetectedAtUtc:O}. " + (decision.Rejected ? "The local decision is to keep rejecting it." : "Local review is required."),
                "Open the local Certificate decisions window from Settings. This panel and MCP cannot accept, remove or replace a certificate pin.");
        return new(problems, now, partial, context.Bounded);
    }

    internal static bool Covers(string? root, string destination, int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(root) || !root.StartsWith('/')) return false;
        root = root.TrimEnd('/');
        destination = destination.TrimEnd('/');
        if (destination == root) return true;
        if (!destination.StartsWith(root + "/", StringComparison.Ordinal)) return false;
        return destination[(root.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries).Length <= Math.Max(0, maxDepth);
    }

    private static string Id(string check, string server, string subject) => check + ":" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(server + "\n" + subject)))[..24];
}
