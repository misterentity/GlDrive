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
    // Consecutive transient run failures (model HTTP errors / exceptions). Drives
    // exponential retry backoff in ScheduleNext — the old flat 1-min catch-up retry
    // hammered OpenRouter while rate-limited (18 runs in 21 min on 2026-07-01, all
    // HTTP 429), and a failed scheduled 04:00 run previously waited a full day.
    private int _consecutiveFailedRuns;
    /// <summary>Failure count past which the loop is stuck, not merely unlucky, and must be logged at ERR.</summary>
    internal const int PersistentFailureThreshold = 5;
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

    /// <summary>
    /// Stops future scheduled runs. Does NOT abort a currently-executing run — that lets
    /// a ~30-second-remaining HTTP round-trip finish naturally instead of losing the work
    /// when the user toggles Enabled off (or WPF re-fires the setter during Settings save).
    /// Call Dispose() to fully shut down including active-run cancellation.
    /// </summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        // Intentionally NOT cancelling _activeRunCts here — see doc comment above.
    }

    /// <summary>
    /// Forcibly cancels the in-flight run (if any) and stops the scheduler. Only called
    /// from Dispose() or an explicit "kill switch" path that genuinely needs to abort.
    /// </summary>
    public void Abort()
    {
        Stop();
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
        var needCatchUp = NeedsCatchUp(_lastRunUtc, DateTime.UtcNow);
        if (needCatchUp || _consecutiveFailedRuns > 0)
        {
            // Catch up a missed run, or retry a transiently-failed one, with
            // exponential backoff: 1, 2, 4 ... 64 min cap. Retrying keeps a failed
            // scheduled run from losing the whole day; the backoff keeps the retry
            // from hammering a rate-limited API every minute.
            var minutes = Math.Min(64, 1 << Math.Min(6, _consecutiveFailedRuns));
            _timer = new Timer(_ => _ = RunOnceAsync(), null,
                TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);
            Log.Information("AgentRunner {Kind} scheduled in {Minutes} min (consecutive failures: {Failures})",
                needCatchUp ? "catch-up" : "retry", minutes, _consecutiveFailedRuns);
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
                _consecutiveFailedRuns++; // transient (429/5xx/parse) — retry with backoff
                // A stuck loop used to be invisible: every failure logged at INF, so a retired
                // model slug kept the agent from applying anything for days without one ERR line.
                // Escalate once the failures stop looking transient, then periodically.
                if (_consecutiveFailedRuns == PersistentFailureThreshold
                    || (_consecutiveFailedRuns > PersistentFailureThreshold && _consecutiveFailedRuns % 20 == 0))
                {
                    // Name the cause that actually applies. This line blamed the model slug and
                    // the credit balance for 40+ consecutive runs while every attempt was really
                    // being refused for an oversized prompt — both suggestions were dead ends.
                    Log.Error("AgentRunner: AI self-tuning has applied nothing for {Failures} consecutive runs " +
                              "(last reason: {Reason}) — {Guidance}",
                        _consecutiveFailedRuns, status,
                        AgentClient.DescribeFailureForOperator(outcome.ErrorMessage, outcome.ErrorBody));
                }
                try
                {
                    File.WriteAllText(briefPath,
                        $"# Agent run failed\n\nReason: {status}\n\n" +
                        AgentClient.DescribeFailureForOperator(outcome.ErrorMessage, outcome.ErrorBody) + "\n");
                }
                catch { }
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
            _consecutiveFailedRuns = 0;
            SaveLastRun();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AgentRunner partial-failure");
            _consecutiveFailedRuns++;
            status = "partial-failure";
            try { File.WriteAllText(briefPath, $"# Agent partial failure\n\n```\n{ex}\n```\n"); } catch { }
        }
        finally
        {
            _runGate.Release();
            _activeRunCts?.Dispose();
            _activeRunCts = null;
            // Persist on EVERY exit, not just the success path. The failure count is the state
            // that has to survive a restart (that is the whole point of persisting it), and
            // the success path was the one place it never needed saving urgently.
            try { SaveLastRun(); } catch (Exception ex) { Log.Debug(ex, "SaveLastRun failed"); }
            try { ScheduleNext(); } catch (Exception ex) { Log.Warning(ex, "ScheduleNext failed"); }
            // Prune ai-briefs: one .md is written per run and nothing ever deleted them
            // (4150+ files / 7MB observed). Keep the newest 60. Non-recursive GetFiles so
            // the ai-briefs/issues subdir is untouched. Runs on EVERY path (incl. the
            // early-return failure briefs above), since it's in the finally.
            try
            {
                foreach (var old in Directory.GetFiles(_briefsDir, "*.md")
                                            .OrderByDescending(f => f).Skip(60).ToList())
                {
                    try { File.Delete(old); } catch { }
                }
            }
            catch (Exception ex) { Log.Debug(ex, "ai-briefs prune failed"); }
            Log.Information("AgentRunner run {Id} finished status={Status}", runId, status);
        }
    }

    private string LastRunPath => Path.Combine(_aiDataRoot, "last-run.json");

    /// <summary>
    /// Parses the persisted last-run stamp back into a genuine UTC instant.
    ///
    /// A bare <c>DateTime.TryParse</c> is WRONG here. Given an ISO-8601 string carrying a zone
    /// designator ("...Z" or "...-07:00") the default styles CONVERT the value to the machine's
    /// local time and hand back Kind=Local. That result was then subtracted from
    /// <c>DateTime.UtcNow</c> — mixing a local wall-clock reading with a UTC one and inflating
    /// the elapsed time by the whole UTC offset (7h on this box).
    ///
    /// Effect: every process restart re-read the stamp 7h "older" than it was, so the >=23h
    /// catch-up predicate fired on a gap of only ~22h and the agent ran a second, unwanted time
    /// at ~02:00 — burning an extra LLM call, an extra change budget and an extra DryRunsRemaining
    /// decrement. Observed 2026-08-25..28: exactly one run/day at 04:00 while the process was
    /// stable (08-18..08-24), then two runs every day across the restart-heavy release window.
    /// East of UTC the sign flips and a legitimately-missed run is SUPPRESSED instead.
    ///
    /// RoundtripKind preserves the offset instead of applying it; ToUniversalTime then normalises
    /// both the "Z" form and the "-07:00" form that an already-corrupted file was saved with, so
    /// this also self-heals state written by the buggy build. This is the same
    /// RoundtripKind + ToUniversalTime pairing HeartbeatMonitor and UpdateChecker's marker
    /// readers already used — this was the one sibling in the codebase that missed it.
    /// </summary>
    internal static bool TryParseLastRunUtc(string? raw, out DateTime utc)
    {
        utc = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)) return false;
        utc = parsed.ToUniversalTime();
        return true;
    }

    /// <summary>
    /// Whether a scheduled run was missed and should be caught up. Both arguments must be UTC;
    /// see <see cref="TryParseLastRunUtc"/> for why that is not a formality.
    /// </summary>
    internal static bool NeedsCatchUp(DateTime lastRunUtc, DateTime nowUtc) =>
        lastRunUtc != DateTime.MinValue && (nowUtc - lastRunUtc).TotalHours >= 23;

    private void LoadLastRun()
    {
        try
        {
            if (File.Exists(LastRunPath))
            {
                var node = JsonNode.Parse(File.ReadAllText(LastRunPath));
                if (node != null && TryParseLastRunUtc(node["utc"]?.ToString(), out var t))
                    _lastRunUtc = t;
                // A give-up counter that a restart zeroes is not a counter. This drives the
                // ERR that tells the operator the loop is stuck, and that ERR fires at the
                // 5th consecutive failure: with the count in a field alone, every restart
                // (crash, watchdog, auto-update) bought the stuck loop five more silent runs.
                // Observed 2026-08-17 21:14 — the log printed "consecutive failures: 0"
                // immediately after a run that had reached 7.
                if (node != null && int.TryParse(node["consecutiveFailures"]?.ToString(), out var f) && f >= 0)
                    _consecutiveFailedRuns = f;
            }
        }
        catch { }
    }

    private void SaveLastRun()
    {
        try
        {
            var obj = new JsonObject
            {
                ["utc"] = _lastRunUtc.ToString("O"),
                ["consecutiveFailures"] = _consecutiveFailedRuns,
            };
            File.WriteAllText(LastRunPath, obj.ToJsonString());
        }
        catch { }
    }

    public void Dispose()
    {
        Abort();  // app is shutting down — full cancel is OK
        SystemEvents.PowerModeChanged -= OnPower;
        SystemEvents.TimeChanged -= OnTimeChanged;
    }
}
