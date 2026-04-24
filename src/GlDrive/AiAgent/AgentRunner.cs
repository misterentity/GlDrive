using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using Microsoft.Win32;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class AgentRunner : IDisposable
{
    private readonly LogDigester _digester;
    private readonly AgentMemo _memo;
    private readonly FreezeStore _freeze;
    private readonly ChangeApplier _applier;
    private readonly AuditTrail _audit;
    private readonly SnapshotStore _snapshots;
    private readonly string _configFilePath;
    private readonly Action<AppConfig> _saveConfig;
    private readonly Func<AppConfig> _getConfig;
    private readonly string _aiDataRoot;
    private readonly string _briefsDir;

    private readonly SemaphoreSlim _runGate = new(1, 1);
    private Timer? _timer;
    private DateTime _lastRunUtc = DateTime.MinValue;
    private CancellationTokenSource? _activeRunCts;

    public AgentRunner(
        LogDigester digester,
        AgentMemo memo,
        FreezeStore freeze,
        ChangeApplier applier,
        AuditTrail audit,
        SnapshotStore snapshots,
        string configFilePath,
        Action<AppConfig> saveConfig,
        Func<AppConfig> getConfig,
        string aiDataRoot)
    {
        _digester = digester;
        _memo = memo;
        _freeze = freeze;
        _applier = applier;
        _audit = audit;
        _snapshots = snapshots;
        _configFilePath = configFilePath;
        _saveConfig = saveConfig;
        _getConfig = getConfig;
        _aiDataRoot = aiDataRoot;
        _briefsDir = Path.Combine(aiDataRoot, "ai-briefs");
        Directory.CreateDirectory(_briefsDir);

        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.TimeChanged += OnTimeChanged;

        LoadLastRun();
    }

    public void Start()
    {
        if (!_getConfig().Agent.Enabled) return;
        ScheduleNext();
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _activeRunCts?.Cancel();
    }

    public Task RunNowAsync() => RunOnceAsync(manualTrigger: true);

    private void OnPower(object? _, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) ScheduleNext();
    }

    private void OnTimeChanged(object? _, EventArgs e) => ScheduleNext();

    private void ScheduleNext()
    {
        _timer?.Dispose();
        var cfg = _getConfig().Agent;
        if (!cfg.Enabled) return;

        var now = DateTime.Now;
        if (_lastRunUtc != DateTime.MinValue && (DateTime.UtcNow - _lastRunUtc).TotalHours >= 23)
        {
            // Catch-up: schedule immediate run
            _timer = new Timer(_ => _ = RunOnceAsync(), null,
                TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
            Log.Information("AgentRunner catch-up scheduled in 1 min");
            return;
        }

        var nextRun = new DateTime(now.Year, now.Month, now.Day, cfg.RunHourLocal, 0, 0, DateTimeKind.Local);
        if (nextRun <= now) nextRun = nextRun.AddDays(1);
        var delay = nextRun - now;
        _timer = new Timer(_ => _ = RunOnceAsync(), null, delay, Timeout.InfiniteTimeSpan);
        Log.Information("AgentRunner next run in {Delay}", delay);
    }

    private async Task RunOnceAsync(bool manualTrigger = false)
    {
        if (!await _runGate.WaitAsync(0))
        {
            Log.Information("AgentRunner: run already in progress; skipping trigger (manual={Manual})", manualTrigger);
            return;
        }

        _activeRunCts = new CancellationTokenSource();
        var ct = _activeRunCts.Token;
        var runId = Guid.NewGuid().ToString();
        var started = DateTime.Now;
        var briefPath = Path.Combine(_briefsDir, $"{started:yyyyMMdd-HHmmss}-{runId[..8]}.md");
        string status = "ok";

        Log.Information("AgentRunner run {Id} STARTED (manual={Manual})", runId, manualTrigger);

        try
        {
            var cfg = _getConfig();
            if (!cfg.Agent.Enabled && !manualTrigger)
            {
                status = "disabled";
                return;
            }

            string snapshotPath;
            try { snapshotPath = _snapshots.Save(_configFilePath, runId); }
            catch (Exception ex)
            {
                status = "failed-pre-run-snapshot";
                try { File.WriteAllText(briefPath, $"# Agent run failed — snapshot\n\n```\n{ex}\n```\n"); } catch { }
                Log.Warning(ex, "AgentRunner pre-run snapshot failed");
                return;
            }

            var digest = _digester.Build(cfg.Agent.WindowDays);
            var memoText = _memo.Load();
            var frozenPaths = _freeze.All.Select(e => e.Path).ToList();

            JsonNode? configNode;
            try { configNode = JsonNode.Parse(File.ReadAllText(_configFilePath)); }
            catch (Exception ex)
            {
                status = "failed-config-read";
                try { File.WriteAllText(briefPath, $"# Agent run failed — config read\n\n```\n{ex}\n```\n"); } catch { }
                return;
            }
            if (configNode is null)
            {
                status = "failed-config-parse";
                try { File.WriteAllText(briefPath, "# Agent run failed — config is null\n"); } catch { }
                return;
            }

            var redacted = AgentPrompt.RedactFrozen(configNode, frozenPaths);

            var lastSummaries = _audit.ReadAll().Reverse()
                .GroupBy(r => r.RunId)
                .Take(3)
                .Select(g => $"run {g.Key[..Math.Min(8, g.Key.Length)]}: applied={g.Count(r => r.Applied)} rejected={g.Count(r => !r.Applied)}")
                .ToList();

            var composer = new AgentPrompt();
            var userPrompt = composer.Compose(digest, memoText, frozenPaths, redacted, lastSummaries);
            var apiKey = cfg.Downloads.ResolveOpenRouterKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                status = "no-api-key";
                try
                {
                    File.WriteAllText(briefPath,
                        "# Agent run skipped\n\nNo OpenRouter API key configured. Set one via Settings → Downloads → OpenRouter API key.\n");
                }
                catch { }
                return;
            }

            using var client = new AgentClient(apiKey, cfg.ResolveAgentModel());
            var outcome = await client.RunAsync(AgentPrompt.SystemPrompt, userPrompt, ct);
            if (outcome.Result is null)
            {
                status = outcome.ErrorMessage ?? "model-failure";
                try { File.WriteAllText(briefPath, $"# Agent run failed\n\nReason: {status}\n"); } catch { }
                return;
            }

            bool dryRun = cfg.Agent.DryRunsRemaining > 0;

            var applyReport = _applier.Apply(outcome.Result.Changes, cfg, cfg.Agent, runId, dryRun);
            var suggestionReport = _applier.Apply(outcome.Result.Suggestions, cfg, cfg.Agent, runId, dryRun: true);

            if (!dryRun) _saveConfig(cfg);

            _memo.Save(outcome.Result.MemoUpdate);

            if (cfg.Agent.DryRunsRemaining > 0)
            {
                cfg.Agent.DryRunsRemaining -= 1;
                _saveConfig(cfg);
            }

            var footer =
                $"\n\n---\n_Tokens: {outcome.InputTokens} in / {outcome.OutputTokens} out — est. ${outcome.EstimatedCostUsd:F3}_\n" +
                $"_Applied: {applyReport.Applied} / Rejected: {applyReport.Rejected} ({(dryRun ? "DRY RUN" : "live")})_\n" +
                $"_Suggestions: {suggestionReport.Applied + suggestionReport.Rejected}_\n";
            try { File.WriteAllText(briefPath, (outcome.Result.BriefMarkdown ?? "# (no brief)") + footer); } catch { }

            _lastRunUtc = DateTime.UtcNow;
            SaveLastRun();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AgentRunner partial-failure");
            status = "partial-failure";
            try { File.WriteAllText(briefPath, $"# Agent partial failure\n\n```\n{ex}\n```\n"); } catch { }
        }
        finally
        {
            _runGate.Release();
            _activeRunCts?.Dispose();
            _activeRunCts = null;
            try { ScheduleNext(); } catch (Exception ex) { Log.Warning(ex, "ScheduleNext failed"); }
            Log.Information("AgentRunner run {Id} finished status={Status}", runId, status);
        }
    }

    private string LastRunPath => Path.Combine(_aiDataRoot, "last-run.json");

    private void LoadLastRun()
    {
        try
        {
            if (File.Exists(LastRunPath))
            {
                var node = JsonNode.Parse(File.ReadAllText(LastRunPath));
                if (node != null && DateTime.TryParse(node["utc"]?.ToString(), out var t))
                    _lastRunUtc = t;
            }
        }
        catch { }
    }

    private void SaveLastRun()
    {
        try
        {
            var obj = new JsonObject { ["utc"] = _lastRunUtc.ToString("O") };
            File.WriteAllText(LastRunPath, obj.ToJsonString());
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        SystemEvents.PowerModeChanged -= OnPower;
        SystemEvents.TimeChanged -= OnTimeChanged;
    }
}
