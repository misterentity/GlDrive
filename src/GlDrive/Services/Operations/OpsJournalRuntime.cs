using System.Text.Json.Nodes;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Services.Alerts;

namespace GlDrive.Services.Operations;

/// <summary>Scalar-only observers. Journal work cannot delay an engine event or configuration save.</summary>
internal sealed class OpsJournalRuntime : IDisposable
{
    private readonly OpsJournal _journal;
    private readonly ServerManager _servers;
    private readonly UpdateChecker _updates;
    private readonly AlertRouter _alerts;
    private readonly AgentRunner? _agent;
    internal OpsJournalRuntime(OpsJournal journal, ServerManager servers, UpdateChecker updates, AlertRouter alerts, AgentRunner? agent)
    {
        _journal = journal; _servers = servers; _updates = updates; _alerts = alerts; _agent = agent;
        servers.ServerStateChanged += State;
        servers.IrcStateChanged += Irc;
        servers.BncRateLimitDetected += Bnc;
        servers.AuthenticationRejected += Authentication;
        servers.SiteHealthNotice += Health;
        updates.UpdateAvailable += Update;
        updates.UpdateInstallStalled += UpdateStalled;
        updates.RestartRequested += Restart;
        alerts.Observed += Alert;
        if (agent != null) agent.RunCompleted += Agent;
        ConfigManager.SettingsSaved += Settings;
    }
    private void Record(string kind, string severity = "Information", string server = "", string subject = "", string detail = "", string actor = "application", string correlation = "")
        => _journal.TryRecord(new(DateTimeOffset.UtcNow, kind, severity, server, subject, detail, actor, correlation));
    private void State(string id, string name, MountState state) => Record("mount-state", state == MountState.Error ? "Error" : "Information", server: id, detail: state.ToString());
    private void Irc(string id, string name, IrcServiceState state) => Record("irc-state", state == IrcServiceState.Reconnecting ? "Warning" : "Information", server: id, detail: state.ToString());
    private void Bnc(string name, string message) => Record("login-throttle", "Warning", detail: "An existing connection reported a login throttle.");
    private void Authentication(GlDrive.Ftp.FtpAuthenticationNotice notice) => Record("authentication-hold", "Warning", detail: "An existing login reported rejected credentials.");
    private void Health(SiteHealthNotice notice) => Record("health-episode", notice.Recovered ? "Information" : "Warning",
        server: notice.ServerId, detail: notice.Recovered ? "Recovered" : notice.State.ToString());
    private void Update(GitHubRelease release) => Record("update-available", detail: release.TagName);
    private void UpdateStalled(GitHubRelease release, TimeSpan wait) => Record("update-deferred", "Warning", detail: release.TagName + "; pending install");
    private void Restart() => Record("update-restart-requested");
    private void Agent(string runId, string status) => Record("agent-run", status is "ok" or "disabled" or "cancelled" ? "Information" : "Warning", detail: status, actor: "agent", correlation: runId);
    private void Alert(AlertEvent alert)
    {
        // IRC chat/attention belongs to its existing private local surfaces; never copy text or identifiers here.
        if (alert.Kind is AlertKind.IrcMention or AlertKind.IrcPrivateMessage or AlertKind.Heartbeat or AlertKind.RaceComplete ||
            alert.Severity is not (AlertSeverity.Warning or AlertSeverity.Error)) return;
        Record("alert", alert.Severity.ToString(), alert.ServerId, alert.Release, alert.Kind.ToString());
    }
    private void Settings(string path, JsonNode? before, JsonNode after, ConfigWriteContext attribution)
    {
        if (!string.Equals(path, ConfigManager.ConfigPath, StringComparison.OrdinalIgnoreCase)) return;
        // No raw graph or setting values cross the producer boundary. Pointer-level details remain in Overrides telemetry.
        Record("config-saved", detail: "Configuration saved; a bounded pointer change set follows when existing diff evidence is available.", actor: attribution.Actor, correlation: attribution.AuditRef ?? "");
    }
    public void Dispose()
    {
        _servers.ServerStateChanged -= State; _servers.IrcStateChanged -= Irc;
        _servers.BncRateLimitDetected -= Bnc; _servers.AuthenticationRejected -= Authentication;
        _servers.SiteHealthNotice -= Health;
        _updates.UpdateAvailable -= Update; _updates.UpdateInstallStalled -= UpdateStalled; _updates.RestartRequested -= Restart;
        _alerts.Observed -= Alert; if (_agent != null) _agent.RunCompleted -= Agent; ConfigManager.SettingsSaved -= Settings;
    }
}
