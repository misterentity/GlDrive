# AI Nightly Agent Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a BYOK OpenRouter-powered nightly agent that reviews 7 days of structured telemetry + a persistent memo, autonomously applies config changes within ten bounded categories respecting safety rails (budget, dry-run, audit, kill-switch, snapshots, freeze markers, confidence threshold), and surfaces a Daily Brief in a new Dashboard "AI Agent" tab.

**Architecture:** New `GlDrive.AiAgent` namespace hosts: `TelemetryRecorder` (ten streams), `LogDigester` (local pre-digest), `AgentMemo` (long-running beliefs), `AgentPrompt`+`AgentClient` (LLM round trip), `ChangeApplier`+ten validators, `AuditTrail`, `SnapshotStore`, `FreezeStore`, `NukePoller`, `AgentRunner` (scheduler). UI: new Dashboard tab with five sub-tabs (Brief, Audit, Suggestions, Frozen, Memo), Settings tab, and tray submenu. Each of the 11 phases is independently shippable.

**Tech Stack:** .NET 10 WPF (win-x64), FluentFTP, Serilog, `System.Threading.Channels`, `System.IO.Compression.GZipStream`, `System.Text.Json`. No test project — verification is `dotnet build` + manual runtime exercise per `CLAUDE.md`.

**Reference spec:** `docs/superpowers/specs/2026-04-23-ai-log-review-design.md`

**Verification convention per task:** Since there is no test runner:
- After code changes: run `dotnet build src/GlDrive/GlDrive.csproj` → expect `Build succeeded` with 0 errors.
- After build green: run the app (`dotnet run --project src/GlDrive/GlDrive.csproj`) and exercise the specific behavior the task added. Check the Serilog log at `%AppData%\GlDrive\logs\gldrive-{YYYYMMDD}.log` for expected log lines.

**Commit convention:** Each task commits independently. Use the repo's `MANDATORY pre-commit check` from `CLAUDE.md` before every commit:
```bash
git status --short | grep -v "^??"
```
If any `D` (deleted) entries appear that you didn't delete: `git reset --mixed HEAD`, re-stage by name.

**Versioning:** After each phase completes successfully, bump `<Version>` in `src/GlDrive/GlDrive.csproj` minor/patch by user convention. Don't auto-bump mid-phase.

---

## Phase 1 — AgentConfig + Settings UI scaffolding

Ships: `AgentConfig` class, Settings tab binding, first-run consent dialog hook (lazy — actual dialog in Phase 11).

### Task 1.1: Add `AgentConfig` class and `AppConfig.Agent` property

**Files:**
- Modify: `src/GlDrive/Config/AppConfig.cs`

- [ ] **Step 1: Add `AgentConfig` class and property**

In `src/GlDrive/Config/AppConfig.cs`, add the `AgentConfig` class near the bottom (after existing config classes) and a property on `AppConfig`:

```csharp
public class AgentConfig
{
    public bool Enabled { get; set; } = false;
    public int RunHourLocal { get; set; } = 4;
    public int ConfidenceThreshold_x100 { get; set; } = 70;
    public int MaxChangesPerRun { get; set; } = 20;
    public int MaxChangesPerCategory { get; set; } = 5;
    public int DryRunsRemaining { get; set; } = 3;
    public int WindowDays { get; set; } = 7;
    public int GzipAfterDays { get; set; } = 30;
    public int DeleteAfterDays { get; set; } = 90;
    public int SnapshotRetentionCount { get; set; } = 30;
    public int NukePollIntervalHours { get; set; } = 6;
    public string ModelId { get; set; } = "anthropic/claude-sonnet-4-6";
    public int TelemetryMaxFileMB { get; set; } = 100;
    public bool HasAcceptedConsent { get; set; } = false;
}
```

Add to `AppConfig`:
```csharp
public AgentConfig Agent { get; set; } = new();
```

Add resolver helper (follows existing `ResolveOpenRouterKey` pattern):
```csharp
public string ResolveAgentModel() => string.IsNullOrWhiteSpace(Agent.ModelId)
    ? "anthropic/claude-sonnet-4-6" : Agent.ModelId;
```

- [ ] **Step 2: Build**

Run: `dotnet build src/GlDrive/GlDrive.csproj`
Expected: `Build succeeded` with 0 errors.

- [ ] **Step 3: Runtime check**

Launch app, change a setting elsewhere, close. Open `%AppData%\GlDrive\appsettings.json`. Confirm `"agent": { "enabled": false, "runHourLocal": 4, ... }` block now exists.

- [ ] **Step 4: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/Config/AppConfig.cs
git commit -m "feat(agent): add AgentConfig class + AppConfig.Agent property"
```

---

### Task 1.2: Scaffold "AI Agent" Settings tab

**Files:**
- Modify: `src/GlDrive/UI/SettingsWindow.xaml`
- Modify: `src/GlDrive/UI/SettingsViewModel.cs`
- Modify: `src/GlDrive/UI/SettingsWindow.xaml.cs`

- [ ] **Step 1: Add AgentConfig bindings to `SettingsViewModel`**

In `src/GlDrive/UI/SettingsViewModel.cs`, add properties mirroring `AgentConfig` (follow the pattern of existing config-bound properties in this file):

```csharp
public bool AgentEnabled
{
    get => _config.Agent.Enabled;
    set { _config.Agent.Enabled = value; OnPropertyChanged(); OnAgentEnabledChanged?.Invoke(value); }
}
public int AgentRunHourLocal
{
    get => _config.Agent.RunHourLocal;
    set { _config.Agent.RunHourLocal = Math.Clamp(value, 0, 23); OnPropertyChanged(); }
}
public int AgentConfidenceThresholdPercent
{
    get => _config.Agent.ConfidenceThreshold_x100;
    set { _config.Agent.ConfidenceThreshold_x100 = Math.Clamp(value, 50, 99); OnPropertyChanged(); }
}
public int AgentMaxChangesPerRun
{
    get => _config.Agent.MaxChangesPerRun;
    set { _config.Agent.MaxChangesPerRun = Math.Max(1, value); OnPropertyChanged(); }
}
public int AgentMaxChangesPerCategory
{
    get => _config.Agent.MaxChangesPerCategory;
    set { _config.Agent.MaxChangesPerCategory = Math.Max(1, value); OnPropertyChanged(); }
}
public int AgentDryRunsRemaining
{
    get => _config.Agent.DryRunsRemaining;
    set { _config.Agent.DryRunsRemaining = Math.Max(0, value); OnPropertyChanged(); }
}
public int AgentWindowDays
{
    get => _config.Agent.WindowDays;
    set { _config.Agent.WindowDays = Math.Clamp(value, 1, 30); OnPropertyChanged(); }
}
public int AgentNukePollIntervalHours
{
    get => _config.Agent.NukePollIntervalHours;
    set { _config.Agent.NukePollIntervalHours = Math.Clamp(value, 1, 24); OnPropertyChanged(); }
}
public string AgentModelId
{
    get => _config.Agent.ModelId;
    set { _config.Agent.ModelId = value ?? ""; OnPropertyChanged(); }
}
public int AgentGzipAfterDays
{
    get => _config.Agent.GzipAfterDays;
    set { _config.Agent.GzipAfterDays = Math.Max(1, value); OnPropertyChanged(); }
}
public int AgentDeleteAfterDays
{
    get => _config.Agent.DeleteAfterDays;
    set { _config.Agent.DeleteAfterDays = Math.Max(1, value); OnPropertyChanged(); }
}
public int AgentSnapshotRetentionCount
{
    get => _config.Agent.SnapshotRetentionCount;
    set { _config.Agent.SnapshotRetentionCount = Math.Max(1, value); OnPropertyChanged(); }
}

public event Action<bool>? OnAgentEnabledChanged;

public ICommand ResetDryRunsCommand => new RelayCommand(() => { AgentDryRunsRemaining = 3; });
public ICommand OpenAiDataFolderCommand => new RelayCommand(() =>
{
    var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlDrive", "ai-data");
    System.IO.Directory.CreateDirectory(path);
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
});
```

- [ ] **Step 2: Add the new tab to `SettingsWindow.xaml`**

Find the `<TabControl>` in `SettingsWindow.xaml`. Append a new `<TabItem Header="AI Agent">` after the existing last tab. Inside, add a scrolling StackPanel with grouped controls (follow the pattern of the existing tabs — use `DynamicResource` brushes):

```xml
<TabItem Header="AI Agent">
  <ScrollViewer VerticalScrollBarVisibility="Auto">
    <StackPanel Margin="12">
      <TextBlock Text="Nightly AI Agent" FontSize="18" FontWeight="Bold" Margin="0,0,0,8"/>
      <TextBlock TextWrapping="Wrap" Foreground="{DynamicResource SecondaryTextBrush}" Margin="0,0,0,12">
        Reviews telemetry overnight and applies config tweaks within safety rails. BYOK via OpenRouter.
      </TextBlock>

      <CheckBox Content="Enabled" IsChecked="{Binding AgentEnabled}" Margin="0,0,0,8"/>

      <TextBlock Text="Run hour (local, 0–23)" Margin="0,6,0,2"/>
      <TextBox Text="{Binding AgentRunHourLocal, UpdateSourceTrigger=PropertyChanged}" Width="80" HorizontalAlignment="Left"/>

      <TextBlock Text="Model (OpenRouter id)" Margin="0,10,0,2"/>
      <Border BorderBrush="{DynamicResource AccentBrush}" BorderThickness="1" CornerRadius="4" Padding="8" Margin="0,0,0,4">
        <TextBlock TextWrapping="Wrap" FontSize="11" Foreground="{DynamicResource SecondaryTextBrush}">
          Suggested: anthropic/claude-sonnet-4-6 — best long-context reasoning for log review. Free tier models work but can miss trends.
        </TextBlock>
      </Border>
      <TextBox Text="{Binding AgentModelId, UpdateSourceTrigger=PropertyChanged}" Width="320" HorizontalAlignment="Left"/>

      <TextBlock Text="Confidence threshold (percent)" Margin="0,10,0,2"/>
      <TextBox Text="{Binding AgentConfidenceThresholdPercent, UpdateSourceTrigger=PropertyChanged}" Width="80" HorizontalAlignment="Left"/>

      <TextBlock Text="Max changes per run / per category" Margin="0,10,0,2"/>
      <StackPanel Orientation="Horizontal">
        <TextBox Text="{Binding AgentMaxChangesPerRun, UpdateSourceTrigger=PropertyChanged}" Width="80"/>
        <TextBlock Text="  /  " VerticalAlignment="Center"/>
        <TextBox Text="{Binding AgentMaxChangesPerCategory, UpdateSourceTrigger=PropertyChanged}" Width="80"/>
      </StackPanel>

      <TextBlock Text="Dry runs remaining" Margin="0,10,0,2"/>
      <StackPanel Orientation="Horizontal">
        <TextBox Text="{Binding AgentDryRunsRemaining, UpdateSourceTrigger=PropertyChanged}" Width="80"/>
        <Button Content="Reset to 3" Command="{Binding ResetDryRunsCommand}" Margin="8,0,0,0"/>
      </StackPanel>

      <TextBlock Text="Window (days)" Margin="0,10,0,2"/>
      <TextBox Text="{Binding AgentWindowDays, UpdateSourceTrigger=PropertyChanged}" Width="80" HorizontalAlignment="Left"/>

      <TextBlock Text="Nuke poll interval (hours)" Margin="0,10,0,2"/>
      <TextBox Text="{Binding AgentNukePollIntervalHours, UpdateSourceTrigger=PropertyChanged}" Width="80" HorizontalAlignment="Left"/>

      <TextBlock Text="Retention" FontWeight="Bold" Margin="0,16,0,4"/>
      <StackPanel Orientation="Horizontal">
        <TextBlock Text="Gzip after (days)" Width="160" VerticalAlignment="Center"/>
        <TextBox Text="{Binding AgentGzipAfterDays, UpdateSourceTrigger=PropertyChanged}" Width="80"/>
      </StackPanel>
      <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
        <TextBlock Text="Delete after (days)" Width="160" VerticalAlignment="Center"/>
        <TextBox Text="{Binding AgentDeleteAfterDays, UpdateSourceTrigger=PropertyChanged}" Width="80"/>
      </StackPanel>
      <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
        <TextBlock Text="Snapshots kept" Width="160" VerticalAlignment="Center"/>
        <TextBox Text="{Binding AgentSnapshotRetentionCount, UpdateSourceTrigger=PropertyChanged}" Width="80"/>
      </StackPanel>

      <Button Content="Open ai-data folder" Command="{Binding OpenAiDataFolderCommand}" Margin="0,20,0,0" HorizontalAlignment="Left"/>
    </StackPanel>
  </ScrollViewer>
</TabItem>
```

- [ ] **Step 3: Build**

`dotnet build src/GlDrive/GlDrive.csproj` — expect green.

- [ ] **Step 4: Runtime check**

Launch app → open Settings → click "AI Agent" tab. All fields visible and editable. Toggle Enabled, change model id, save. Re-open Settings — values persisted. Open `appsettings.json` — `agent` block reflects edits.

- [ ] **Step 5: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/UI/SettingsWindow.xaml src/GlDrive/UI/SettingsViewModel.cs
git commit -m "feat(agent): add AI Agent settings tab with AgentConfig bindings"
```

---

## Phase 2 — TelemetryRecorder + 10 emit points

Ships: `TelemetryRecorder` infrastructure, 10 structured event streams wired into existing emit points, daily rotation + retention.

### Task 2.1: TelemetryEvent record types

**Files:**
- Create: `src/GlDrive/AiAgent/TelemetryEvents.cs`

- [ ] **Step 1: Define all 10 event records**

Create `src/GlDrive/AiAgent/TelemetryEvents.cs`:

```csharp
using System.Text.Json.Serialization;

namespace GlDrive.AiAgent;

public abstract record TelemetryEnvelope
{
    [JsonPropertyName("ts")] public string Ts { get; init; } = DateTime.UtcNow.ToString("O");
    [JsonPropertyName("v")]  public int V   { get; init; } = 1;
}

public record RaceParticipant(
    [property: JsonPropertyName("serverId")] string ServerId,
    [property: JsonPropertyName("role")]     string Role,        // "src" | "dst"
    [property: JsonPropertyName("bytes")]    long Bytes,
    [property: JsonPropertyName("files")]    int Files,
    [property: JsonPropertyName("avgKbps")]  double AvgKbps,
    [property: JsonPropertyName("abortReason")] string? AbortReason);

public record RaceOutcomeEvent : TelemetryEnvelope
{
    [JsonPropertyName("raceId")]     public string RaceId { get; init; } = "";
    [JsonPropertyName("section")]    public string Section { get; init; } = "";
    [JsonPropertyName("release")]    public string Release { get; init; } = "";
    [JsonPropertyName("startedAt")]  public string StartedAt { get; init; } = "";
    [JsonPropertyName("endedAt")]    public string EndedAt { get; init; } = "";
    [JsonPropertyName("participants")] public List<RaceParticipant> Participants { get; init; } = [];
    [JsonPropertyName("winner")]     public string? Winner { get; init; }
    [JsonPropertyName("fxpMode")]    public string FxpMode { get; init; } = "";
    [JsonPropertyName("scoreBreakdown")] public Dictionary<string, int> ScoreBreakdown { get; init; } = new();
    [JsonPropertyName("result")]     public string Result { get; init; } = "";  // complete|aborted|blacklisted
    [JsonPropertyName("filesExpected")] public int FilesExpected { get; init; }
    [JsonPropertyName("filesTotal")]    public int FilesTotal { get; init; }
}

public record NukeDetectedEvent : TelemetryEnvelope
{
    [JsonPropertyName("serverId")] public string ServerId { get; init; } = "";
    [JsonPropertyName("section")]  public string Section { get; init; } = "";
    [JsonPropertyName("release")]  public string Release { get; init; } = "";
    [JsonPropertyName("nukedAt")]  public string NukedAt { get; init; } = "";
    [JsonPropertyName("nuker")]    public string Nuker { get; init; } = "";
    [JsonPropertyName("reason")]   public string Reason { get; init; } = "";
    [JsonPropertyName("multiplier")] public int Multiplier { get; init; }
    [JsonPropertyName("ourRaceRef")] public string? OurRaceRef { get; init; }
}

public record SiteHealthEvent : TelemetryEnvelope
{
    [JsonPropertyName("serverId")]         public string ServerId { get; init; } = "";
    [JsonPropertyName("windowStart")]      public string WindowStart { get; init; } = "";
    [JsonPropertyName("windowEnd")]        public string WindowEnd { get; init; } = "";
    [JsonPropertyName("avgConnectMs")]     public double AvgConnectMs { get; init; }
    [JsonPropertyName("p99ConnectMs")]     public double P99ConnectMs { get; init; }
    [JsonPropertyName("disconnects")]      public int Disconnects { get; init; }
    [JsonPropertyName("tlsHandshakeMs")]   public double TlsHandshakeMs { get; init; }
    [JsonPropertyName("poolExhaustCount")] public int PoolExhaustCount { get; init; }
    [JsonPropertyName("ghostKills")]       public int GhostKills { get; init; }
    [JsonPropertyName("errors5xx")]        public int Errors5xx { get; init; }
    [JsonPropertyName("reinitCount")]      public int ReinitCount { get; init; }
}

public record AnnounceNoMatchEvent : TelemetryEnvelope
{
    [JsonPropertyName("serverId")]             public string ServerId { get; init; } = "";
    [JsonPropertyName("channel")]              public string Channel { get; init; } = "";
    [JsonPropertyName("botNick")]              public string BotNick { get; init; } = "";
    [JsonPropertyName("message")]              public string Message { get; init; } = "";
    [JsonPropertyName("nearestRulePattern")]   public string? NearestRulePattern { get; init; }
    [JsonPropertyName("nearestRuleDistance")]  public int? NearestRuleDistance { get; init; }
}

public record WishlistAttemptEvent : TelemetryEnvelope
{
    [JsonPropertyName("wishlistItemId")] public string WishlistItemId { get; init; } = "";
    [JsonPropertyName("release")]        public string Release { get; init; } = "";
    [JsonPropertyName("score")]          public double Score { get; init; }
    [JsonPropertyName("matched")]        public bool Matched { get; init; }
    [JsonPropertyName("missReason")]     public string? MissReason { get; init; }
    [JsonPropertyName("section")]        public string Section { get; init; } = "";
    [JsonPropertyName("serverId")]       public string ServerId { get; init; } = "";
}

public record ConfigOverrideEvent : TelemetryEnvelope
{
    [JsonPropertyName("jsonPointer")]  public string JsonPointer { get; init; } = "";
    [JsonPropertyName("beforeValue")]  public string? BeforeValue { get; init; }
    [JsonPropertyName("afterValue")]   public string? AfterValue { get; init; }
    [JsonPropertyName("aiAuditRef")]   public string? AiAuditRef { get; init; }
}

public record DownloadOutcomeEvent : TelemetryEnvelope
{
    [JsonPropertyName("downloadId")]   public string DownloadId { get; init; } = "";
    [JsonPropertyName("serverId")]     public string ServerId { get; init; } = "";
    [JsonPropertyName("remotePath")]   public string RemotePath { get; init; } = "";
    [JsonPropertyName("result")]       public string Result { get; init; } = ""; // complete|failed|retried|resumed
    [JsonPropertyName("bytes")]        public long Bytes { get; init; }
    [JsonPropertyName("elapsedMs")]    public long ElapsedMs { get; init; }
    [JsonPropertyName("retryCount")]   public int RetryCount { get; init; }
    [JsonPropertyName("failureClass")] public string? FailureClass { get; init; }
}

public record FileTransferEvent : TelemetryEnvelope
{
    [JsonPropertyName("raceId")]        public string RaceId { get; init; } = "";
    [JsonPropertyName("srcServer")]     public string SrcServer { get; init; } = "";
    [JsonPropertyName("dstServer")]     public string DstServer { get; init; } = "";
    [JsonPropertyName("file")]          public string File { get; init; } = "";
    [JsonPropertyName("bytes")]         public long Bytes { get; init; }
    [JsonPropertyName("elapsedMs")]     public long ElapsedMs { get; init; }
    [JsonPropertyName("ttfbMs")]        public long TtfbMs { get; init; }
    [JsonPropertyName("pasvLatencyMs")] public long PasvLatencyMs { get; init; }
    [JsonPropertyName("abortReason")]   public string? AbortReason { get; init; }
}

public record SectionActivityEvent : TelemetryEnvelope
{
    [JsonPropertyName("serverId")]  public string ServerId { get; init; } = "";
    [JsonPropertyName("section")]   public string Section { get; init; } = "";
    [JsonPropertyName("filesIn")]   public int FilesIn { get; init; }
    [JsonPropertyName("bytesIn")]   public long BytesIn { get; init; }
    [JsonPropertyName("ourRaces")]  public int OurRaces { get; init; }
    [JsonPropertyName("ourWins")]   public int OurWins { get; init; }
    [JsonPropertyName("dayOfWeek")] public int DayOfWeek { get; init; }
}

public record ErrorSignatureEvent : TelemetryEnvelope
{
    [JsonPropertyName("component")]         public string Component { get; init; } = "";
    [JsonPropertyName("exceptionType")]     public string ExceptionType { get; init; } = "";
    [JsonPropertyName("normalizedMessage")] public string NormalizedMessage { get; init; } = "";
    [JsonPropertyName("stackTopFrame")]     public string StackTopFrame { get; init; } = "";
    [JsonPropertyName("count")]             public int Count { get; init; }
    [JsonPropertyName("firstAt")]           public string FirstAt { get; init; } = "";
    [JsonPropertyName("lastAt")]            public string LastAt { get; init; } = "";
}

public enum TelemetryStream
{
    Races, Nukes, SiteHealth, AnnouncesNoMatch, WishlistAttempts,
    Overrides, Downloads, Transfers, SectionActivity, Errors
}
```

- [ ] **Step 2: Build**

`dotnet build src/GlDrive/GlDrive.csproj` — expect green.

- [ ] **Step 3: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/TelemetryEvents.cs
git commit -m "feat(agent): define 10 telemetry event record types"
```

---

### Task 2.2: TelemetryRecorder core with bounded channels

**Files:**
- Create: `src/GlDrive/AiAgent/TelemetryRecorder.cs`

- [ ] **Step 1: Write TelemetryRecorder**

```csharp
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class TelemetryRecorder : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly string _root;
    private readonly int _maxFileMB;
    private readonly Dictionary<TelemetryStream, StreamWriterTask> _writers = new();
    private readonly Dictionary<TelemetryStream, int> _drops = new();
    private DateTime _lastDropWarnUtc = DateTime.MinValue;

    public TelemetryRecorder(string appDataRoot, int maxFileMB)
    {
        _root = Path.Combine(appDataRoot, "ai-data");
        Directory.CreateDirectory(_root);
        _maxFileMB = maxFileMB;
        foreach (TelemetryStream s in Enum.GetValues<TelemetryStream>())
        {
            _writers[s] = new StreamWriterTask(s, _root);
            _drops[s] = 0;
        }
    }

    public void Record<T>(TelemetryStream stream, T evt) where T : TelemetryEnvelope
    {
        try
        {
            var json = JsonSerializer.Serialize(evt, evt.GetType(), JsonOpts);
            if (!_writers[stream].TryEnqueue(json))
            {
                Interlocked.Increment(ref CollectionsMarshal_GetValueRef(_drops, stream));
                WarnDropsOnce();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TelemetryRecorder serialize failed for {Stream}", stream);
        }
    }

    private void WarnDropsOnce()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastDropWarnUtc).TotalMinutes < 5) return;
        _lastDropWarnUtc = now;
        Log.Warning("Telemetry drops: {Drops}", string.Join(",", _drops.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    public Dictionary<TelemetryStream, int> GetDropCounts() => new(_drops);

    public void Dispose()
    {
        foreach (var w in _writers.Values) w.Dispose();
    }

    // Dictionary ref helper (ok because we only increment)
    private static ref int CollectionsMarshal_GetValueRef(Dictionary<TelemetryStream, int> dict, TelemetryStream key)
        => ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dict, key, out _);

    private sealed class StreamWriterTask : IDisposable
    {
        private readonly Channel<string> _channel = Channel.CreateBounded<string>(
            new BoundedChannelOptions(2048) { FullMode = BoundedChannelFullMode.DropNewest });
        private readonly Task _pump;
        private readonly CancellationTokenSource _cts = new();
        private readonly TelemetryStream _stream;
        private readonly string _root;

        public StreamWriterTask(TelemetryStream stream, string root)
        {
            _stream = stream; _root = root;
            _pump = Task.Run(PumpAsync);
        }

        public bool TryEnqueue(string line) => _channel.Writer.TryWrite(line);

        private async Task PumpAsync()
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(_cts.Token))
                {
                    while (_channel.Reader.TryRead(out var line))
                    {
                        try
                        {
                            var path = Path.Combine(_root, FileName(DateTime.Now));
                            await File.AppendAllTextAsync(path, line + "\n", Encoding.UTF8, _cts.Token);
                        }
                        catch (Exception ex) { Log.Debug(ex, "telemetry write fail {Stream}", _stream); }
                    }
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
        }

        private string FileName(DateTime d)
        {
            var prefix = _stream switch
            {
                TelemetryStream.Races => "races",
                TelemetryStream.Nukes => "nukes",
                TelemetryStream.SiteHealth => "site-health",
                TelemetryStream.AnnouncesNoMatch => "announces-nomatch",
                TelemetryStream.WishlistAttempts => "wishlist-attempts",
                TelemetryStream.Overrides => "overrides",
                TelemetryStream.Downloads => "downloads",
                TelemetryStream.Transfers => "transfers",
                TelemetryStream.SectionActivity => "section-activity",
                TelemetryStream.Errors => "errors",
                _ => "unknown"
            };
            return $"{prefix}-{d:yyyyMMdd}.jsonl";
        }

        public void Dispose()
        {
            _channel.Writer.TryComplete();
            try { _cts.Cancel(); _pump.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }
}
```

- [ ] **Step 2: Build**

`dotnet build` — expect green.

- [ ] **Step 3: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/TelemetryRecorder.cs
git commit -m "feat(agent): TelemetryRecorder with bounded per-stream channels"
```

---

### Task 2.3: Wire TelemetryRecorder singleton into App startup

**Files:**
- Modify: `src/GlDrive/App.xaml.cs`

- [ ] **Step 1: Construct singleton at startup, dispose on shutdown**

In `src/GlDrive/App.xaml.cs`, locate the startup sequence after `ConfigManager.Load`. Add:

```csharp
// After config is loaded
var appDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlDrive");
TelemetryRecorder = new GlDrive.AiAgent.TelemetryRecorder(appDataRoot, _config.Agent.TelemetryMaxFileMB);
```

Add public property:
```csharp
public static GlDrive.AiAgent.TelemetryRecorder? TelemetryRecorder { get; private set; }
```

In `OnExit` (or existing shutdown handler), add:
```csharp
TelemetryRecorder?.Dispose();
TelemetryRecorder = null;
```

- [ ] **Step 2: Build**

`dotnet build` — expect green.

- [ ] **Step 3: Runtime check**

Launch app, close. Confirm `%AppData%\GlDrive\ai-data\` directory exists (empty is fine).

- [ ] **Step 4: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/App.xaml.cs
git commit -m "feat(agent): register TelemetryRecorder singleton in App lifecycle"
```

---

### Task 2.4: Emit point — RaceOutcome at `SpreadJob.RunAsync` completion

**Files:**
- Modify: `src/GlDrive/Spread/SpreadJob.cs`

- [ ] **Step 1: Emit on both completion and abort paths**

Find `SpreadJob.RunAsync`'s completion and abort paths. At the end of the method (after IsComplete is set or an abort path finalizes), emit the event. Add private helper:

```csharp
private void EmitRaceOutcome(string result)
{
    var recorder = ((App)Application.Current).GetType()
        .GetProperty("TelemetryRecorder")?.GetValue(null) as GlDrive.AiAgent.TelemetryRecorder;
    if (recorder is null) return;

    var participants = _sites?.Select(s => new GlDrive.AiAgent.RaceParticipant(
        ServerId: s.ServerId,
        Role: s.IsSource ? "src" : "dst",
        Bytes: s.BytesTransferred,
        Files: s.FilesOwned,
        AvgKbps: s.AvgKbps,
        AbortReason: s.AbortReason
    )).ToList() ?? new List<GlDrive.AiAgent.RaceParticipant>();

    recorder.Record(GlDrive.AiAgent.TelemetryStream.Races, new GlDrive.AiAgent.RaceOutcomeEvent
    {
        RaceId = RaceId,
        Section = Section ?? "",
        Release = Release ?? "",
        StartedAt = StartedAt.ToUniversalTime().ToString("O"),
        EndedAt = DateTime.UtcNow.ToString("O"),
        Participants = participants,
        Winner = Winner,
        FxpMode = FxpMode.ToString(),
        ScoreBreakdown = ScoreBreakdown ?? new Dictionary<string, int>(),
        Result = result,
        FilesExpected = FilesExpected,
        FilesTotal = _fileInfos?.Count ?? 0
    });
}
```

At each terminal path (complete, abort, blacklisted), call `EmitRaceOutcome("complete"|"aborted"|"blacklisted")`.

**Note:** If any field above (`_sites`, `BytesTransferred`, `AvgKbps`, `AbortReason`, `ScoreBreakdown`, `Winner`) doesn't exist on `SpreadJob` yet, use `""`/`0`/`null` placeholders and leave a `// TODO: plumb once available` comment — but prefer wiring real values if they're trivially available from existing fields. (Cross-check `SpreadJob.cs` contents before finalizing the code.)

- [ ] **Step 2: Build**

`dotnet build` — expect green.

- [ ] **Step 3: Runtime check**

Start app with at least one spread server enabled. Trigger (manually or via IRC) one race. After race completes, confirm `ai-data/races-{today}.jsonl` exists with one JSON line containing the race id + result.

- [ ] **Step 4: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/Spread/SpreadJob.cs
git commit -m "feat(agent): emit RaceOutcome telemetry on spread job completion"
```

---

### Task 2.5: Emit point — FileTransferTiming at `FxpTransfer.ExecuteAsync`

**Files:**
- Modify: `src/GlDrive/Spread/FxpTransfer.cs`

- [ ] **Step 1: Measure + emit per file**

In `FxpTransfer.ExecuteAsync`, add a `Stopwatch` from the moment `CPSV`/`PASV` is issued, capture `pasvLatencyMs` separately (the time from issuing the command until first response), capture `ttfbMs` (from data-channel established to first byte transferred), elapsed = total file time.

After successful transfer (or catch on abort):
```csharp
var recorder = ((App)Application.Current).GetType()
    .GetProperty("TelemetryRecorder")?.GetValue(null) as GlDrive.AiAgent.TelemetryRecorder;
recorder?.Record(GlDrive.AiAgent.TelemetryStream.Transfers, new GlDrive.AiAgent.FileTransferEvent
{
    RaceId = raceId,
    SrcServer = srcServerId,
    DstServer = dstServerId,
    File = remoteFilePath,
    Bytes = bytesTransferred,
    ElapsedMs = totalSw.ElapsedMilliseconds,
    TtfbMs = ttfbMs,
    PasvLatencyMs = pasvLatencyMs,
    AbortReason = abortReason   // null on success
});
```

The method signature for `FxpTransfer.ExecuteAsync` likely needs raceId + srcServerId + dstServerId passed through. If not already present, add them as parameters and thread them from `SpreadJob.ExecuteTransfer`.

- [ ] **Step 2: Build**

`dotnet build` — expect green.

- [ ] **Step 3: Runtime check**

Trigger one race, confirm `transfers-{today}.jsonl` has one row per file transferred with non-zero ElapsedMs/TtfbMs/PasvLatencyMs.

- [ ] **Step 4: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/Spread/FxpTransfer.cs src/GlDrive/Spread/SpreadJob.cs
git commit -m "feat(agent): emit per-file FileTransfer telemetry with TTFB + PASV latency"
```

---

### Task 2.6: Emit point — AnnounceNoMatch at `IrcAnnounceListener`

**Files:**
- Modify: `src/GlDrive/Spread/IrcAnnounceListener.cs`

- [ ] **Step 1: Detect "looks announce-y but didn't match"**

After all rules attempted and none matched, add a heuristic:
```csharp
private static bool LooksAnnouncey(string message)
{
    if (string.IsNullOrWhiteSpace(message)) return false;
    var m = message;
    return (m.Contains('[') && m.Contains(']')) ||
           m.Contains("NEW ", StringComparison.OrdinalIgnoreCase) ||
           m.Contains(" pre", StringComparison.OrdinalIgnoreCase) ||
           m.Contains("Release", StringComparison.OrdinalIgnoreCase);
}

private static (string? pattern, int? distance) NearestRule(
    string message, IReadOnlyList<IrcAnnounceRule> rules)
{
    if (rules.Count == 0) return (null, null);
    string? best = null; int bestDist = int.MaxValue;
    foreach (var r in rules)
    {
        var d = LevenshteinLimited(message, r.Pattern, 200);
        if (d < bestDist) { bestDist = d; best = r.Pattern; }
    }
    return (best, bestDist == int.MaxValue ? null : bestDist);
}

private static int LevenshteinLimited(string a, string b, int max)
{
    if (a.Length == 0) return Math.Min(b.Length, max);
    if (b.Length == 0) return Math.Min(a.Length, max);
    var dp = new int[b.Length + 1];
    for (int j = 0; j <= b.Length; j++) dp[j] = j;
    for (int i = 1; i <= a.Length; i++)
    {
        var prev = dp[0]; dp[0] = i; var rowMin = dp[0];
        for (int j = 1; j <= b.Length; j++)
        {
            var tmp = dp[j];
            dp[j] = a[i - 1] == b[j - 1]
                ? prev
                : 1 + Math.Min(prev, Math.Min(dp[j], dp[j - 1]));
            prev = tmp; rowMin = Math.Min(rowMin, dp[j]);
        }
        if (rowMin >= max) return max;
    }
    return dp[b.Length];
}
```

At the end of the match loop, where no rule matched:
```csharp
if (LooksAnnouncey(message))
{
    var (near, dist) = NearestRule(message, rules);
    var recorder = ((App)Application.Current).GetType()
        .GetProperty("TelemetryRecorder")?.GetValue(null) as GlDrive.AiAgent.TelemetryRecorder;
    recorder?.Record(GlDrive.AiAgent.TelemetryStream.AnnouncesNoMatch,
        new GlDrive.AiAgent.AnnounceNoMatchEvent
        {
            ServerId = serverId,
            Channel = channel,
            BotNick = botNick ?? "",
            Message = message.Length > 512 ? message[..512] : message,
            NearestRulePattern = near,
            NearestRuleDistance = dist
        });
}
```

- [ ] **Step 2: Build + runtime check**

`dotnet build` green. Connect to IRC with at least one channel where bot messages appear. Confirm `announces-nomatch-{today}.jsonl` populates with messages that look announce-y but didn't match any rule.

- [ ] **Step 3: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/Spread/IrcAnnounceListener.cs
git commit -m "feat(agent): emit AnnounceNoMatch telemetry for unmatched announce-y IRC msgs"
```

---

### Task 2.7: Emit point — WishlistMatchAttempt at `WishlistMatcher`

**Files:**
- Modify: `src/GlDrive/Downloads/WishlistMatcher.cs`

- [ ] **Step 1: Emit per-comparison**

At every comparison point in `WishlistMatcher`, after computing the score + match decision, emit:
```csharp
var recorder = ((App)Application.Current).GetType()
    .GetProperty("TelemetryRecorder")?.GetValue(null) as GlDrive.AiAgent.TelemetryRecorder;
recorder?.Record(GlDrive.AiAgent.TelemetryStream.WishlistAttempts,
    new GlDrive.AiAgent.WishlistAttemptEvent
    {
        WishlistItemId = wishItem.Id,
        Release = release,
        Score = score,
        Matched = matched,
        MissReason = matched ? null : missReason,
        Section = section ?? "",
        ServerId = serverId ?? ""
    });
```

`missReason` classifications (derive from the matcher's rejection path — add as needed):
- `"title-fuzzy-below-threshold"` when title match fails
- `"year-mismatch"` when year differs
- `"quality-tag-mismatch"` when release doesn't carry requested quality tags
- `"tvmaze-episode-already-have"` when episode already downloaded (if tracked)
- `"other"` fallback

- [ ] **Step 2: Build + runtime check**

`dotnet build` green. Add a couple of wishlist items. Announce arrival of matching + non-matching releases (via IRC or `DashboardViewModel.AddNotification`). Confirm `wishlist-attempts-{today}.jsonl` has rows for both matched and unmatched tries.

- [ ] **Step 3: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/Downloads/WishlistMatcher.cs
git commit -m "feat(agent): emit WishlistAttempt telemetry per comparison with miss reason"
```

---

### Task 2.8: Emit point — ConfigOverride at `ConfigManager.Save`

**Files:**
- Modify: `src/GlDrive/Config/ConfigManager.cs`
- Create: `src/GlDrive/AiAgent/ConfigDiff.cs`

- [ ] **Step 1: Write `ConfigDiff` helper**

Create `src/GlDrive/AiAgent/ConfigDiff.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlDrive.AiAgent;

public static class ConfigDiff
{
    /// <summary>Emits (jsonPointer, beforeValue, afterValue) for every scalar-leaf change.</summary>
    public static IEnumerable<(string pointer, string? before, string? after)> Diff(JsonNode? before, JsonNode? after, string pointer = "")
    {
        if (before is null && after is null) yield break;
        if (before is null) { yield return (pointer, null, after!.ToJsonString()); yield break; }
        if (after is null)  { yield return (pointer, before.ToJsonString(), null); yield break; }

        if (before is JsonObject bo && after is JsonObject ao)
        {
            var keys = new HashSet<string>(bo.Select(kv => kv.Key).Concat(ao.Select(kv => kv.Key)));
            foreach (var k in keys)
                foreach (var d in Diff(bo.ContainsKey(k) ? bo[k] : null, ao.ContainsKey(k) ? ao[k] : null, $"{pointer}/{EscapePointer(k)}"))
                    yield return d;
            yield break;
        }
        if (before is JsonArray ba && after is JsonArray aa)
        {
            var max = Math.Max(ba.Count, aa.Count);
            for (int i = 0; i < max; i++)
                foreach (var d in Diff(i < ba.Count ? ba[i] : null, i < aa.Count ? aa[i] : null, $"{pointer}/{i}"))
                    yield return d;
            yield break;
        }

        var beforeStr = before.ToJsonString();
        var afterStr  = after.ToJsonString();
        if (beforeStr != afterStr)
            yield return (pointer, beforeStr, afterStr);
    }

    private static string EscapePointer(string s) => s.Replace("~", "~0").Replace("/", "~1");
}
```

- [ ] **Step 2: Emit in `ConfigManager.Save`**

In `ConfigManager.Save`, before overwriting the file: read current on-disk JSON, compute diff against the new config, emit one `ConfigOverrideEvent` per changed leaf.

```csharp
public void Save(AppConfig config)
{
    try
    {
        string? beforeJson = File.Exists(_path) ? File.ReadAllText(_path) : null;
        var newJson = JsonSerializer.Serialize(config, _opts);
        File.WriteAllText(_path, newJson);

        if (beforeJson != null)
        {
            var beforeNode = JsonNode.Parse(beforeJson);
            var afterNode  = JsonNode.Parse(newJson);
            var recorder = ((App)System.Windows.Application.Current).GetType()
                .GetProperty("TelemetryRecorder")?.GetValue(null) as GlDrive.AiAgent.TelemetryRecorder;
            if (recorder != null)
            {
                foreach (var (ptr, b, a) in GlDrive.AiAgent.ConfigDiff.Diff(beforeNode, afterNode))
                    recorder.Record(GlDrive.AiAgent.TelemetryStream.Overrides,
                        new GlDrive.AiAgent.ConfigOverrideEvent
                        {
                            JsonPointer = ptr,
                            BeforeValue = b,
                            AfterValue = a
                        });
            }
        }
    }
    catch (Exception ex) { Log.Error(ex, "ConfigManager.Save failed"); throw; }
}
```

(Adapt to the real `Save` signature/return type.)

- [ ] **Step 3: Build + runtime check**

`dotnet build` green. Launch, open Settings, change a value, Save. Confirm `overrides-{today}.jsonl` has at least one row with `jsonPointer` matching the change path.

- [ ] **Step 4: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/ConfigDiff.cs src/GlDrive/Config/ConfigManager.cs
git commit -m "feat(agent): emit ConfigOverride telemetry diffing before/after config saves"
```

---

### Task 2.9: Emit point — DownloadOutcome at DownloadManager transitions

**Files:**
- Modify: `src/GlDrive/Downloads/DownloadManager.cs`

- [ ] **Step 1: Emit on each terminal state**

In `DownloadManager`'s status-transition handler (or wherever `DownloadStatus` moves to `Complete`/`Failed` and each retry/resume), emit:
```csharp
var recorder = ((App)Application.Current).GetType()
    .GetProperty("TelemetryRecorder")?.GetValue(null) as GlDrive.AiAgent.TelemetryRecorder;
recorder?.Record(GlDrive.AiAgent.TelemetryStream.Downloads,
    new GlDrive.AiAgent.DownloadOutcomeEvent
    {
        DownloadId = item.Id,
        ServerId = item.ServerId,
        RemotePath = item.RemotePath,
        Result = result,   // "complete" | "failed" | "retried" | "resumed"
        Bytes = item.TotalBytes,
        ElapsedMs = (long)(item.EndedAt - item.StartedAt).TotalMilliseconds,
        RetryCount = item.RetryCount,
        FailureClass = ClassifyFailure(item.ErrorMessage)
    });

static string? ClassifyFailure(string? err)
{
    if (string.IsNullOrEmpty(err)) return null;
    var low = err.ToLowerInvariant();
    if (low.Contains("timeout")) return "timeout";
    if (low.Contains("tls") || low.Contains("handshake")) return "tls";
    if (low.Contains("550"))  return "ftp-550";
    if (low.Contains("421"))  return "ftp-421";
    if (low.Contains("disk") || low.Contains("space")) return "disk";
    if (low.Contains("sfv"))  return "sfv-mismatch";
    return "other";
}
```

- [ ] **Step 2: Build + runtime check**

`dotnet build` green. Queue a small download; confirm `downloads-{today}.jsonl` gets a `complete` row. Cancel one to confirm a `failed` row. (Or temporarily corrupt a remote path to induce a failure.)

- [ ] **Step 3: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/Downloads/DownloadManager.cs
git commit -m "feat(agent): emit DownloadOutcome telemetry with failure classification"
```

---

### Task 2.10: `ErrorSignatureSink` Serilog sink

**Files:**
- Create: `src/GlDrive/AiAgent/ErrorSignatureSink.cs`
- Modify: `src/GlDrive/Logging/SerilogSetup.cs`

- [ ] **Step 1: Write the sink**

Create `src/GlDrive/AiAgent/ErrorSignatureSink.cs`:
```csharp
using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace GlDrive.AiAgent;

public sealed class ErrorSignatureSink : ILogEventSink
{
    private sealed record Sig(string Component, string ExceptionType, string NormalizedMessage, string StackTopFrame);

    private readonly Dictionary<Sig, (int count, DateTime first, DateTime last)> _agg = new();
    private readonly object _lock = new();
    private readonly Timer _flushTimer;
    private readonly TelemetryRecorder _recorder;

    public ErrorSignatureSink(TelemetryRecorder recorder)
    {
        _recorder = recorder;
        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    public void Emit(LogEvent ev)
    {
        if (ev.Level < LogEventLevel.Error) return;
        var sig = Signature(ev);
        lock (_lock)
        {
            if (_agg.TryGetValue(sig, out var v))
                _agg[sig] = (v.count + 1, v.first, DateTime.UtcNow);
            else
                _agg[sig] = (1, DateTime.UtcNow, DateTime.UtcNow);
        }
    }

    public void Flush()
    {
        Dictionary<Sig, (int count, DateTime first, DateTime last)> snapshot;
        lock (_lock) { snapshot = new(_agg); _agg.Clear(); }
        foreach (var (sig, (count, first, last)) in snapshot)
        {
            _recorder.Record(TelemetryStream.Errors, new ErrorSignatureEvent
            {
                Component = sig.Component,
                ExceptionType = sig.ExceptionType,
                NormalizedMessage = sig.NormalizedMessage,
                StackTopFrame = sig.StackTopFrame,
                Count = count,
                FirstAt = first.ToString("O"),
                LastAt = last.ToString("O")
            });
        }
    }

    private static Sig Signature(LogEvent ev)
    {
        var component = ev.Properties.TryGetValue("SourceContext", out var sc) ? sc.ToString().Trim('"') : "";
        var exType = ev.Exception?.GetType().FullName ?? "";
        var msg = Normalize(ev.Exception?.Message ?? ev.MessageTemplate.Text);
        var frame = ev.Exception?.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return new Sig(component, exType, msg, frame);
    }

    private static string Normalize(string s)
    {
        s = Regex.Replace(s, @"\d+", "N");
        s = Regex.Replace(s, @"[A-Z]:\\[^\s""']+", "<path>");
        s = Regex.Replace(s, @"[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}", "<guid>");
        return s.Length > 200 ? s[..200] : s;
    }
}
```

- [ ] **Step 2: Attach the sink in `SerilogSetup`**

In `src/GlDrive/Logging/SerilogSetup.cs`, after the existing sink chain, add:
```csharp
if (App.TelemetryRecorder != null)
    loggerConfig.WriteTo.Sink(new GlDrive.AiAgent.ErrorSignatureSink(App.TelemetryRecorder));
```
(The sink only activates when recorder is ready. If setup order runs before recorder, add a setter that can be called post-init.)

- [ ] **Step 3: Build + runtime check**

`dotnet build` green. Temporarily throw a test exception from a button handler — confirm after up to an hour (or reduce flush interval for verification) that `errors-{today}.jsonl` has a row with the exception type + message.

- [ ] **Step 4: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/ErrorSignatureSink.cs src/GlDrive/Logging/SerilogSetup.cs
git commit -m "feat(agent): ErrorSignatureSink clusters error-level logs into hourly rollups"
```

---

### Task 2.11: `HealthRollup` — hourly site-health telemetry

**Files:**
- Create: `src/GlDrive/AiAgent/HealthRollup.cs`
- Modify: `src/GlDrive/App.xaml.cs`

- [ ] **Step 1: Write rollup service**

```csharp
using Serilog;

namespace GlDrive.AiAgent;

public sealed class HealthRollup : IDisposable
{
    private readonly TelemetryRecorder _recorder;
    private readonly Services.ServerManager _servers;
    private readonly Timer _timer;
    private DateTime _windowStart = DateTime.UtcNow;

    public HealthRollup(TelemetryRecorder recorder, Services.ServerManager servers)
    {
        _recorder = recorder;
        _servers = servers;
        _timer = new Timer(_ => RollUp(), null, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60));
    }

    public void RollUp()
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var ms in _servers.GetAllMountServices())
            {
                var pool = ms.FtpConnectionPool;
                if (pool is null) continue;

                _recorder.Record(TelemetryStream.SiteHealth, new SiteHealthEvent
                {
                    ServerId = ms.ServerId,
                    WindowStart = _windowStart.ToString("O"),
                    WindowEnd = now.ToString("O"),
                    AvgConnectMs = pool.AvgConnectMs,
                    P99ConnectMs = pool.P99ConnectMs,
                    Disconnects = pool.DisconnectsSinceFlush,
                    TlsHandshakeMs = pool.AvgTlsHandshakeMs,
                    PoolExhaustCount = pool.ExhaustCountSinceFlush,
                    GhostKills = pool.GhostKillsSinceFlush,
                    Errors5xx = pool.Errors5xxSinceFlush,
                    ReinitCount = pool.ReinitCountSinceFlush
                });
                pool.FlushHealthCounters();
            }
            _windowStart = now;
        }
        catch (Exception ex) { Log.Warning(ex, "HealthRollup failed"); }
    }

    public void Dispose() => _timer.Dispose();
}
```

Counters `AvgConnectMs`, `P99ConnectMs`, `DisconnectsSinceFlush`, `AvgTlsHandshakeMs`, `ExhaustCountSinceFlush`, `GhostKillsSinceFlush`, `Errors5xxSinceFlush`, `ReinitCountSinceFlush` must exist on `FtpConnectionPool`. If any are missing, add them in Task 2.11-b (below) before completing this task.

- [ ] **Step 2: Add counters to `FtpConnectionPool`**

Add `public` fields/properties on `src/GlDrive/Ftp/FtpConnectionPool.cs`:
```csharp
public double AvgConnectMs { get; private set; }
public double P99ConnectMs { get; private set; }
public int DisconnectsSinceFlush { get; private set; }
public double AvgTlsHandshakeMs { get; private set; }
public int ExhaustCountSinceFlush { get; private set; }
public int GhostKillsSinceFlush { get; private set; }
public int Errors5xxSinceFlush { get; private set; }
public int ReinitCountSinceFlush { get; private set; }

private readonly List<double> _connectMsSamples = new();
private readonly List<double> _tlsMsSamples = new();

internal void RecordConnect(double ms) { lock (_connectMsSamples) _connectMsSamples.Add(ms); }
internal void RecordTlsHandshake(double ms) { lock (_tlsMsSamples) _tlsMsSamples.Add(ms); }
internal void IncrementDisconnect() => Interlocked.Increment(ref _disconnects);
internal void IncrementExhaust() => Interlocked.Increment(ref _exhaustCount);
internal void IncrementGhostKill() => Interlocked.Increment(ref _ghostKills);
internal void IncrementError5xx() => Interlocked.Increment(ref _errors5xx);
internal void IncrementReinit() => Interlocked.Increment(ref _reinitCount);

private int _disconnects, _exhaustCount, _ghostKills, _errors5xx, _reinitCount;

public void FlushHealthCounters()
{
    lock (_connectMsSamples)
    {
        AvgConnectMs = _connectMsSamples.Count == 0 ? 0 : _connectMsSamples.Average();
        P99ConnectMs = _connectMsSamples.Count == 0 ? 0 : Percentile(_connectMsSamples, 0.99);
        _connectMsSamples.Clear();
    }
    lock (_tlsMsSamples)
    {
        AvgTlsHandshakeMs = _tlsMsSamples.Count == 0 ? 0 : _tlsMsSamples.Average();
        _tlsMsSamples.Clear();
    }
    DisconnectsSinceFlush = Interlocked.Exchange(ref _disconnects, 0);
    ExhaustCountSinceFlush = Interlocked.Exchange(ref _exhaustCount, 0);
    GhostKillsSinceFlush = Interlocked.Exchange(ref _ghostKills, 0);
    Errors5xxSinceFlush = Interlocked.Exchange(ref _errors5xx, 0);
    ReinitCountSinceFlush = Interlocked.Exchange(ref _reinitCount, 0);
}

private static double Percentile(List<double> sorted, double p)
{
    var copy = new List<double>(sorted); copy.Sort();
    var idx = (int)Math.Clamp(Math.Round(p * (copy.Count - 1)), 0, copy.Count - 1);
    return copy[idx];
}
```

Then wire the increments at existing emit points in the pool (where connect time is measured, where ghost-kill runs, where exhaust occurs, where reinit completes, where 5xx response is classified).

- [ ] **Step 3: Register HealthRollup in App startup**

In `App.xaml.cs`, after `TelemetryRecorder` is constructed AND `ServerManager` is ready:
```csharp
HealthRollup = new GlDrive.AiAgent.HealthRollup(TelemetryRecorder, _serverManager);
```
Add `public static HealthRollup? HealthRollup { get; private set; }`.

In OnExit: `HealthRollup?.Dispose();`.

- [ ] **Step 4: Build + runtime check**

`dotnet build` green. Launch, connect a server, wait ~60 min (or temporarily reduce the timer to 60s for verification). Confirm `site-health-{today}.jsonl` has rows.

- [ ] **Step 5: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/HealthRollup.cs src/GlDrive/Ftp/FtpConnectionPool.cs src/GlDrive/App.xaml.cs
git commit -m "feat(agent): HealthRollup hourly site-health telemetry from pool counters"
```

---

### Task 2.12: `SectionActivityRollup` — end-of-day per-section activity

**Files:**
- Create: `src/GlDrive/AiAgent/SectionActivityRollup.cs`
- Modify: `src/GlDrive/App.xaml.cs`

- [ ] **Step 1: Write the rollup**

```csharp
using System.IO;
using System.Text.Json;

namespace GlDrive.AiAgent;

public sealed class SectionActivityRollup : IDisposable
{
    private readonly TelemetryRecorder _recorder;
    private readonly string _aiDataRoot;
    private readonly Timer _timer;

    public SectionActivityRollup(TelemetryRecorder recorder, string aiDataRoot)
    {
        _recorder = recorder; _aiDataRoot = aiDataRoot;
        var nextMidnight = DateTime.Today.AddDays(1) - DateTime.Now;
        _timer = new Timer(_ => RollUp(DateTime.Today.AddDays(-1)),
            null, nextMidnight, TimeSpan.FromDays(1));
    }

    public void RollUp(DateTime forDate)
    {
        try
        {
            var racesFile = Path.Combine(_aiDataRoot, $"races-{forDate:yyyyMMdd}.jsonl");
            if (!File.Exists(racesFile)) return;
            var agg = new Dictionary<(string server, string section), SectionActivityEvent>();
            foreach (var line in File.ReadLines(racesFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                RaceOutcomeEvent? r;
                try { r = JsonSerializer.Deserialize<RaceOutcomeEvent>(line); }
                catch { continue; }
                if (r is null) continue;

                foreach (var p in r.Participants)
                {
                    var key = (p.ServerId, r.Section);
                    if (!agg.TryGetValue(key, out var cur))
                        cur = new SectionActivityEvent
                        {
                            ServerId = p.ServerId,
                            Section = r.Section,
                            DayOfWeek = (int)forDate.DayOfWeek
                        };
                    cur = cur with
                    {
                        FilesIn  = cur.FilesIn  + p.Files,
                        BytesIn  = cur.BytesIn  + p.Bytes,
                        OurRaces = cur.OurRaces + 1,
                        OurWins  = cur.OurWins  + (r.Winner == p.ServerId ? 1 : 0)
                    };
                    agg[key] = cur;
                }
            }
            foreach (var ev in agg.Values)
                _recorder.Record(TelemetryStream.SectionActivity, ev);
        }
        catch { /* swallow — rollup is best-effort */ }
    }

    public void Dispose() => _timer.Dispose();
}
```

Records are `record` with `with`, so add `init` on the existing `SectionActivityEvent` (already done in Task 2.1 since every field is `init`).

- [ ] **Step 2: Register in App startup**

In `App.xaml.cs`:
```csharp
var aiData = Path.Combine(appDataRoot, "ai-data");
SectionActivityRollup = new GlDrive.AiAgent.SectionActivityRollup(TelemetryRecorder, aiData);
```
Add `public static SectionActivityRollup? SectionActivityRollup { get; private set; }`. Dispose on exit.

- [ ] **Step 3: Build + runtime check**

`dotnet build` green. After a day with at least one race, confirm `section-activity-{yesterday}.jsonl` created at next midnight. (Verification shortcut: temporarily run `SectionActivityRollup.RollUp(DateTime.Today)` after a race by a debug menu item; produces today's rows immediately.)

- [ ] **Step 4: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/SectionActivityRollup.cs src/GlDrive/App.xaml.cs
git commit -m "feat(agent): SectionActivityRollup nightly aggregation from race outcomes"
```

---

### Task 2.13: Retention — rotation, gzip, delete

**Files:**
- Create: `src/GlDrive/AiAgent/TelemetryRetention.cs`
- Modify: `src/GlDrive/App.xaml.cs`

- [ ] **Step 1: Write retention service**

```csharp
using System.IO;
using System.IO.Compression;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class TelemetryRetention : IDisposable
{
    private readonly string _root;
    private readonly int _gzipAfterDays;
    private readonly int _deleteAfterDays;
    private readonly Timer _timer;

    public TelemetryRetention(string aiDataRoot, int gzipAfterDays, int deleteAfterDays)
    {
        _root = aiDataRoot;
        _gzipAfterDays = gzipAfterDays;
        _deleteAfterDays = deleteAfterDays;
        var nextMidnight = DateTime.Today.AddDays(1) - DateTime.Now + TimeSpan.FromMinutes(5);
        _timer = new Timer(_ => Sweep(), null, nextMidnight, TimeSpan.FromDays(1));
    }

    public void Sweep()
    {
        try
        {
            if (!Directory.Exists(_root)) return;
            var now = DateTime.Now;
            foreach (var path in Directory.GetFiles(_root, "*-*.jsonl"))
            {
                var d = ParseDate(path); if (!d.HasValue) continue;
                var age = (now.Date - d.Value.Date).TotalDays;
                if (age >= _deleteAfterDays) { File.Delete(path); continue; }
                if (age >= _gzipAfterDays) Gzip(path);
            }
            foreach (var path in Directory.GetFiles(_root, "*-*.jsonl.gz"))
            {
                var d = ParseDate(path); if (!d.HasValue) continue;
                if ((now.Date - d.Value.Date).TotalDays >= _deleteAfterDays)
                    File.Delete(path);
            }
        }
        catch (Exception ex) { Log.Warning(ex, "TelemetryRetention sweep failed"); }
    }

    private static DateTime? ParseDate(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).Replace(".jsonl", "");
        var idx = name.LastIndexOf('-');
        if (idx < 0) return null;
        return DateTime.TryParseExact(name[(idx + 1)..], "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;
    }

    private static void Gzip(string path)
    {
        var gz = path + ".gz";
        using (var fs = File.OpenRead(path))
        using (var outFs = File.Create(gz))
        using (var gzStream = new GZipStream(outFs, CompressionLevel.SmallestSize))
            fs.CopyTo(gzStream);
        File.Delete(path);
    }

    public void Dispose() => _timer.Dispose();
}
```

- [ ] **Step 2: Register in App startup**

```csharp
TelemetryRetention = new GlDrive.AiAgent.TelemetryRetention(
    aiData, _config.Agent.GzipAfterDays, _config.Agent.DeleteAfterDays);
```
Dispose on exit.

- [ ] **Step 3: Build + runtime check**

`dotnet build` green. Manually drop a dummy `races-20250101.jsonl` into `ai-data` and call `.Sweep()` via a temp debug hook — verify it gets gzipped (or deleted if beyond retention).

- [ ] **Step 4: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/TelemetryRetention.cs src/GlDrive/App.xaml.cs
git commit -m "feat(agent): TelemetryRetention gzips old jsonl + deletes past retention"
```

---

**Phase 2 complete.** Ten telemetry streams flowing + retention in place. No agent logic yet — just signal.

---

## Phase 3 — NukePoller

Ships: `SITE NUKES` polling per server, parser for glftpd nuke output, cursor store, correlation with races.

### Task 3.1: `NukeCursorStore`

**Files:**
- Create: `src/GlDrive/AiAgent/NukeCursorStore.cs`

- [ ] **Step 1: Write store**

```csharp
using System.IO;
using System.Text.Json;

namespace GlDrive.AiAgent;

public sealed class NukeCursorStore
{
    private readonly string _path;
    private Dictionary<string, DateTime> _cursors = new();
    private readonly object _lock = new();

    public NukeCursorStore(string aiDataRoot)
    {
        _path = Path.Combine(aiDataRoot, "nuke-cursors.json");
        Load();
    }

    public DateTime Get(string serverId)
    {
        lock (_lock)
            return _cursors.TryGetValue(serverId, out var v) ? v : DateTime.MinValue;
    }

    public void Set(string serverId, DateTime cursor)
    {
        lock (_lock)
        {
            _cursors[serverId] = cursor;
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _cursors = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_path))
                           ?? new();
        }
        catch { _cursors = new(); }
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_cursors)); } catch { }
    }
}
```

- [ ] **Step 2: Build + commit**

`dotnet build` green.
```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/NukeCursorStore.cs
git commit -m "feat(agent): NukeCursorStore persists per-server last-seen nuke timestamp"
```

---

### Task 3.2: `NukeParser` for `SITE NUKES` output

**Files:**
- Create: `src/GlDrive/AiAgent/NukeParser.cs`

- [ ] **Step 1: Write parser**

glftpd `SITE NUKES` output varies but commonly looks like:
```
NUKED on 2026-04-22 14:30 by Foo: Some.Release.Name (3x) - dupe
```
Handle multiple known formats leniently — unknown lines go to telemetry as-is for later agent-driven parser improvement (Task 3.5).

```csharp
using System.Text.RegularExpressions;

namespace GlDrive.AiAgent;

public sealed record ParsedNuke(DateTime NukedAt, string Nuker, string Release, int Multiplier, string Reason, string Section);

public static class NukeParser
{
    private static readonly Regex[] Patterns =
    {
        new(@"(?<ts>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}(:\d{2})?)\s+by\s+(?<nuker>\S+)\s*[:\-]\s*(?<release>\S+)\s*\((?<mult>\d+)x\)\s*[\-:]\s*(?<reason>.+)", RegexOptions.IgnoreCase),
        new(@"Nuke\s+(?<release>\S+)\s+\((?<mult>\d+)x\)\s+by\s+(?<nuker>\S+)\s+at\s+(?<ts>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}(:\d{2})?)\s*[\-:]\s*(?<reason>.+)", RegexOptions.IgnoreCase),
        new(@"(?<release>\S+)\s+\((?<mult>\d+)x\)\s+(?<nuker>\S+)\s+(?<ts>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}(:\d{2})?)\s*(?<reason>.+)", RegexOptions.IgnoreCase),
    };

    public static IEnumerable<ParsedNuke> Parse(string siteNukesOutput, string fallbackSection = "")
    {
        foreach (var raw in siteNukesOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            foreach (var rx in Patterns)
            {
                var m = rx.Match(line);
                if (!m.Success) continue;
                if (!DateTime.TryParse(m.Groups["ts"].Value, out var ts)) continue;
                yield return new ParsedNuke(
                    NukedAt: ts,
                    Nuker: m.Groups["nuker"].Value,
                    Release: m.Groups["release"].Value,
                    Multiplier: int.TryParse(m.Groups["mult"].Value, out var mult) ? mult : 1,
                    Reason: m.Groups["reason"].Value.Trim(),
                    Section: fallbackSection
                );
                break;
            }
        }
    }
}
```

- [ ] **Step 2: Build + commit**

`dotnet build` green.
```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/NukeParser.cs
git commit -m "feat(agent): NukeParser handles common glftpd SITE NUKES formats"
```

---

### Task 3.3: `NukePoller` service with per-site circuit breaker

**Files:**
- Create: `src/GlDrive/AiAgent/NukePoller.cs`
- Modify: `src/GlDrive/App.xaml.cs`

- [ ] **Step 1: Write poller**

```csharp
using System.IO;
using System.Text.Json;
using System.Text;
using FluentFTP;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class NukePoller : IDisposable
{
    private readonly TelemetryRecorder _recorder;
    private readonly Services.ServerManager _servers;
    private readonly NukeCursorStore _cursors;
    private readonly string _aiDataRoot;
    private readonly int _intervalHours;
    private readonly Timer _timer;
    private readonly Dictionary<string, int> _failCount = new();
    private const int BreakerThreshold = 3;

    public NukePoller(TelemetryRecorder recorder, Services.ServerManager servers,
                      NukeCursorStore cursors, string aiDataRoot, int intervalHours)
    {
        _recorder = recorder; _servers = servers; _cursors = cursors;
        _aiDataRoot = aiDataRoot; _intervalHours = Math.Max(1, intervalHours);
        _timer = new Timer(_ => _ = PollAllAsync(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromHours(_intervalHours));
    }

    public async Task PollAllAsync()
    {
        foreach (var ms in _servers.GetAllMountServices())
        {
            var serverId = ms.ServerId;
            if (_failCount.TryGetValue(serverId, out var f) && f >= BreakerThreshold)
            {
                Log.Debug("NukePoller circuit open for {Server}", serverId);
                continue;
            }
            try
            {
                var pool = ms.FtpConnectionPool;
                if (pool is null || pool.IsExhausted) continue;

                using var lease = await pool.BorrowAsync(TimeSpan.FromSeconds(30));
                if (lease?.Client is null) continue;
                var client = lease.Client;

                var reply = await client.Execute("SITE NUKES");
                if (!reply.Success) { BumpFail(serverId); continue; }

                var nukes = NukeParser.Parse(reply.InfoMessages ?? "").ToList();
                var cursor = _cursors.Get(serverId);
                var newCursor = cursor;

                foreach (var n in nukes)
                {
                    if (n.NukedAt <= cursor) continue;
                    var ourRef = TryCorrelateRace(n.Release);
                    _recorder.Record(TelemetryStream.Nukes, new NukeDetectedEvent
                    {
                        ServerId = serverId,
                        Section = n.Section,
                        Release = n.Release,
                        NukedAt = n.NukedAt.ToString("O"),
                        Nuker = n.Nuker,
                        Reason = n.Reason,
                        Multiplier = n.Multiplier,
                        OurRaceRef = ourRef
                    });
                    if (n.NukedAt > newCursor) newCursor = n.NukedAt;
                }
                if (newCursor > cursor) _cursors.Set(serverId, newCursor);
                _failCount[serverId] = 0;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "NukePoller failed for {Server}", ms.ServerId);
                BumpFail(serverId);
            }
        }
    }

    private void BumpFail(string serverId)
    {
        _failCount[serverId] = _failCount.GetValueOrDefault(serverId) + 1;
        if (_failCount[serverId] >= BreakerThreshold)
            Log.Warning("NukePoller breaker opened for {Server}", serverId);
    }

    private string? TryCorrelateRace(string release)
    {
        try
        {
            var racesFile = Path.Combine(_aiDataRoot, $"races-{DateTime.Now:yyyyMMdd}.jsonl");
            if (!File.Exists(racesFile)) return null;
            foreach (var line in File.ReadLines(racesFile))
            {
                if (!line.Contains($"\"release\":\"{release}\"", StringComparison.OrdinalIgnoreCase)) continue;
                var r = JsonSerializer.Deserialize<RaceOutcomeEvent>(line);
                if (r is not null && r.Release == release) return r.RaceId;
            }
        }
        catch { }
        return null;
    }

    public void Dispose() => _timer.Dispose();
}
```

If `FtpConnectionPool.BorrowAsync` signature differs, adapt to the real pool method; the goal is: grab a connection, issue `SITE NUKES`, parse reply, return.

- [ ] **Step 2: Register in App startup**

In `App.xaml.cs`:
```csharp
var cursors = new GlDrive.AiAgent.NukeCursorStore(aiData);
NukePoller = new GlDrive.AiAgent.NukePoller(TelemetryRecorder, _serverManager, cursors, aiData, _config.Agent.NukePollIntervalHours);
```
Add `public static NukePoller? NukePoller { get; private set; }`. Dispose on exit.

- [ ] **Step 3: Build + runtime check**

`dotnet build` green. Connect a server; trigger `PollAllAsync()` manually via a debug menu or wait for timer. Confirm `nukes-{today}.jsonl` populates with any new nukes (empty is valid if the site hasn't nuked anything new since cursor).

- [ ] **Step 4: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/NukePoller.cs src/GlDrive/App.xaml.cs
git commit -m "feat(agent): NukePoller polls SITE NUKES, correlates with our races"
```

---

**Phase 3 complete.** Nuke signal now flows into telemetry — the gold input for skiplist learning.

---

## Phase 4 — LogDigester

Ships: compact per-stream digests that compress 7 days of telemetry into a ≤30KB prompt payload.

### Task 4.1: `DigestTypes` records

**Files:**
- Create: `src/GlDrive/AiAgent/DigestTypes.cs`

- [ ] **Step 1: Define digest shapes**

```csharp
using System.Text.Json.Serialization;

namespace GlDrive.AiAgent;

public sealed class DigestBundle
{
    [JsonPropertyName("windowStart")] public string WindowStart { get; set; } = "";
    [JsonPropertyName("windowEnd")]   public string WindowEnd { get; set; } = "";
    [JsonPropertyName("races")]       public RacesDigest Races { get; set; } = new();
    [JsonPropertyName("nukes")]       public NukesDigest Nukes { get; set; } = new();
    [JsonPropertyName("siteHealth")]  public SiteHealthDigest SiteHealth { get; set; } = new();
    [JsonPropertyName("announces")]   public AnnouncesDigest Announces { get; set; } = new();
    [JsonPropertyName("wishlist")]    public WishlistDigest Wishlist { get; set; } = new();
    [JsonPropertyName("overrides")]   public OverridesDigest Overrides { get; set; } = new();
    [JsonPropertyName("downloads")]   public DownloadsDigest Downloads { get; set; } = new();
    [JsonPropertyName("transfers")]   public TransfersDigest Transfers { get; set; } = new();
    [JsonPropertyName("sectionActivity")] public SectionActivityDigest SectionActivity { get; set; } = new();
    [JsonPropertyName("errors")]      public ErrorsDigest Errors { get; set; } = new();
    [JsonPropertyName("evidencePointers")] public Dictionary<string, string> EvidencePointers { get; set; } = new();
}

public sealed class RacesDigest
{
    [JsonPropertyName("totalRaces")] public int TotalRaces { get; set; }
    [JsonPropertyName("winRateByServer")] public Dictionary<string, double> WinRateByServer { get; set; } = new();
    [JsonPropertyName("kbpsByRoute")] public Dictionary<string, double> KbpsByRoute { get; set; } = new(); // "src->dst"
    [JsonPropertyName("abortReasonHistogram")] public Dictionary<string, int> AbortReasonHistogram { get; set; } = new();
    [JsonPropertyName("completionRateBySection")] public Dictionary<string, double> CompletionRateBySection { get; set; } = new();
}

public sealed class NukesDigest
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("correlatedWithOurUploads")] public int Correlated { get; set; }
    [JsonPropertyName("topNukedReleases")] public List<NukeTop> TopNukedReleases { get; set; } = [];
    [JsonPropertyName("nukeRateBySection")] public Dictionary<string, double> NukeRateBySection { get; set; } = new();
    public sealed class NukeTop
    {
        [JsonPropertyName("release")] public string Release { get; set; } = "";
        [JsonPropertyName("count")]   public int Count { get; set; }
        [JsonPropertyName("reason")]  public string Reason { get; set; } = "";
        [JsonPropertyName("section")] public string Section { get; set; } = "";
    }
}

public sealed class SiteHealthDigest
{
    [JsonPropertyName("serverDeltas")] public Dictionary<string, HealthDelta> ServerDeltas { get; set; } = new();
    public sealed class HealthDelta
    {
        [JsonPropertyName("avgConnectMs_pct_change")] public double AvgConnectMsPctChange { get; set; }
        [JsonPropertyName("disconnects_total")] public int DisconnectsTotal { get; set; }
        [JsonPropertyName("poolExhaustCount_total")] public int PoolExhaustTotal { get; set; }
        [JsonPropertyName("ghostKills_total")] public int GhostKillsTotal { get; set; }
        [JsonPropertyName("tlsHandshake_pct_change")] public double TlsHandshakePctChange { get; set; }
        [JsonPropertyName("flagged")] public List<string> Flagged { get; set; } = [];
    }
}

public sealed class AnnouncesDigest
{
    [JsonPropertyName("clusters")] public List<AnnounceCluster> Clusters { get; set; } = [];
    public sealed class AnnounceCluster
    {
        [JsonPropertyName("representative")] public string Representative { get; set; } = "";
        [JsonPropertyName("count")]          public int Count { get; set; }
        [JsonPropertyName("channel")]        public string Channel { get; set; } = "";
        [JsonPropertyName("botNick")]        public string BotNick { get; set; } = "";
        [JsonPropertyName("nearestRule")]    public string? NearestRule { get; set; }
        [JsonPropertyName("nearestRuleDistance")] public int? NearestRuleDistance { get; set; }
    }
}

public sealed class WishlistDigest
{
    [JsonPropertyName("deadItems")] public List<DeadItem> DeadItems { get; set; } = [];
    [JsonPropertyName("nearMissPatterns")] public List<NearMiss> NearMissPatterns { get; set; } = [];
    public sealed class DeadItem
    {
        [JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
        [JsonPropertyName("daysSinceLastMatch")] public int DaysSinceLastMatch { get; set; }
        [JsonPropertyName("attemptsInWindow")] public int AttemptsInWindow { get; set; }
    }
    public sealed class NearMiss
    {
        [JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
        [JsonPropertyName("missReason")] public string MissReason { get; set; } = "";
        [JsonPropertyName("count")] public int Count { get; set; }
    }
}

public sealed class OverridesDigest
{
    [JsonPropertyName("paths")] public List<string> Paths { get; set; } = [];
    [JsonPropertyName("revertedAiPaths")] public List<string> RevertedAiPaths { get; set; } = [];
}

public sealed class DownloadsDigest
{
    [JsonPropertyName("totalComplete")] public int TotalComplete { get; set; }
    [JsonPropertyName("totalFailed")] public int TotalFailed { get; set; }
    [JsonPropertyName("failureClassHistogram")] public Dictionary<string, int> FailureClassHistogram { get; set; } = new();
}

public sealed class TransfersDigest
{
    [JsonPropertyName("kbpsMatrix")] public Dictionary<string, double> KbpsMatrix { get; set; } = new(); // "src->dst"
    [JsonPropertyName("ttfbP99Ms")]  public double TtfbP99Ms { get; set; }
}

public sealed class SectionActivityDigest
{
    [JsonPropertyName("perServerSection")] public List<Row> PerServerSection { get; set; } = [];
    public sealed class Row
    {
        [JsonPropertyName("serverId")] public string ServerId { get; set; } = "";
        [JsonPropertyName("section")]  public string Section { get; set; } = "";
        [JsonPropertyName("filesIn")]  public int FilesIn { get; set; }
        [JsonPropertyName("ourRaces")] public int OurRaces { get; set; }
        [JsonPropertyName("ourWinRate")] public double OurWinRate { get; set; }
    }
}

public sealed class ErrorsDigest
{
    [JsonPropertyName("topSignatures")] public List<Sig> TopSignatures { get; set; } = [];
    public sealed class Sig
    {
        [JsonPropertyName("component")] public string Component { get; set; } = "";
        [JsonPropertyName("exceptionType")] public string ExceptionType { get; set; } = "";
        [JsonPropertyName("normalizedMessage")] public string NormalizedMessage { get; set; } = "";
        [JsonPropertyName("count")]   public int Count { get; set; }
        [JsonPropertyName("trendVsPrior")] public string TrendVsPrior { get; set; } = ""; // "up" | "down" | "flat"
    }
}
```

- [ ] **Step 2: Build + commit**

`dotnet build` green.
```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/DigestTypes.cs
git commit -m "feat(agent): DigestBundle shapes for compact telemetry summaries"
```

---

### Task 4.2: `LogDigester` core + stream reader

**Files:**
- Create: `src/GlDrive/AiAgent/LogDigester.cs`

- [ ] **Step 1: Write core with per-stream reader + gz support**

```csharp
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class LogDigester
{
    private readonly string _aiDataRoot;

    public LogDigester(string aiDataRoot) { _aiDataRoot = aiDataRoot; }

    public DigestBundle Build(int windowDays, DateTime? nowOverride = null)
    {
        var now = nowOverride ?? DateTime.Now;
        var windowStart = now.AddDays(-windowDays).Date;
        var bundle = new DigestBundle
        {
            WindowStart = windowStart.ToString("O"),
            WindowEnd = now.ToString("O")
        };

        bundle.Races           = new RacesDigester().Build(ReadStream<RaceOutcomeEvent>("races", windowStart, now));
        bundle.Nukes           = new NukesDigester().Build(ReadStream<NukeDetectedEvent>("nukes", windowStart, now));
        bundle.SiteHealth      = new SiteHealthDigester().Build(ReadStream<SiteHealthEvent>("site-health", windowStart, now));
        bundle.Announces       = new AnnouncesDigester().Build(ReadStream<AnnounceNoMatchEvent>("announces-nomatch", windowStart, now));
        bundle.Wishlist        = new WishlistDigester().Build(ReadStream<WishlistAttemptEvent>("wishlist-attempts", windowStart, now));
        bundle.Overrides       = new OverridesDigester().Build(ReadStream<ConfigOverrideEvent>("overrides", windowStart, now));
        bundle.Downloads       = new DownloadsDigester().Build(ReadStream<DownloadOutcomeEvent>("downloads", windowStart, now));
        bundle.Transfers       = new TransfersDigester().Build(ReadStream<FileTransferEvent>("transfers", windowStart, now));
        bundle.SectionActivity = new SectionActivityDigester().Build(ReadStream<SectionActivityEvent>("section-activity", windowStart, now));
        bundle.Errors          = new ErrorsDigester().Build(ReadStream<ErrorSignatureEvent>("errors", windowStart, now));

        bundle.EvidencePointers = new Dictionary<string, string>
        {
            ["races"] = $"races-{now:yyyyMMdd}.jsonl",
            ["nukes"] = $"nukes-{now:yyyyMMdd}.jsonl",
            ["announcesNoMatch"] = $"announces-nomatch-{now:yyyyMMdd}.jsonl",
            ["wishlistAttempts"] = $"wishlist-attempts-{now:yyyyMMdd}.jsonl"
        };

        return bundle;
    }

    public IEnumerable<T> ReadStream<T>(string prefix, DateTime fromInclusive, DateTime toInclusive)
    {
        for (var d = fromInclusive.Date; d <= toInclusive.Date; d = d.AddDays(1))
        {
            foreach (var ev in ReadFile<T>(Path.Combine(_aiDataRoot, $"{prefix}-{d:yyyyMMdd}.jsonl")))
                yield return ev;
            foreach (var ev in ReadFile<T>(Path.Combine(_aiDataRoot, $"{prefix}-{d:yyyyMMdd}.jsonl.gz")))
                yield return ev;
        }
    }

    private static IEnumerable<T> ReadFile<T>(string path)
    {
        if (!File.Exists(path)) yield break;
        Stream? raw = null; StreamReader? reader = null;
        try
        {
            raw = File.OpenRead(path);
            Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? new GZipStream(raw, CompressionMode.Decompress)
                : raw;
            reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                T? v;
                try { v = JsonSerializer.Deserialize<T>(line); }
                catch (Exception ex) { Log.Debug(ex, "digester parse skip"); continue; }
                if (v != null) yield return v;
            }
        }
        finally { reader?.Dispose(); raw?.Dispose(); }
    }
}
```

- [ ] **Step 2: Build + commit**

`dotnet build` green.
```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/LogDigester.cs
git commit -m "feat(agent): LogDigester core reads jsonl + gz across window"
```

---

### Task 4.3: `RacesDigester`

**Files:**
- Create: `src/GlDrive/AiAgent/Digesters/RacesDigester.cs`

- [ ] **Step 1: Write digester**

```csharp
namespace GlDrive.AiAgent;

public sealed class RacesDigester
{
    public RacesDigest Build(IEnumerable<RaceOutcomeEvent> events)
    {
        var list = events.ToList();
        var d = new RacesDigest { TotalRaces = list.Count };
        if (list.Count == 0) return d;

        var racesByServer = list
            .SelectMany(r => r.Participants.Select(p => (p.ServerId, won: r.Winner == p.ServerId)))
            .GroupBy(x => x.ServerId);
        foreach (var g in racesByServer)
        {
            var wins = g.Count(x => x.won);
            d.WinRateByServer[g.Key] = g.Count() == 0 ? 0 : (double)wins / g.Count();
        }

        var routeKbps = new Dictionary<string, List<double>>();
        foreach (var r in list)
        {
            var src = r.Participants.FirstOrDefault(p => p.Role == "src")?.ServerId;
            foreach (var p in r.Participants.Where(p => p.Role == "dst"))
            {
                if (string.IsNullOrEmpty(src)) continue;
                var key = $"{src}->{p.ServerId}";
                if (!routeKbps.TryGetValue(key, out var bag)) routeKbps[key] = bag = new List<double>();
                if (p.AvgKbps > 0) bag.Add(p.AvgKbps);
            }
        }
        foreach (var (k, v) in routeKbps)
            d.KbpsByRoute[k] = v.Count == 0 ? 0 : v.Average();

        foreach (var g in list.SelectMany(r => r.Participants)
                              .Where(p => !string.IsNullOrEmpty(p.AbortReason))
                              .GroupBy(p => p.AbortReason!))
            d.AbortReasonHistogram[g.Key] = g.Count();

        foreach (var g in list.GroupBy(r => r.Section))
        {
            var complete = g.Count(r => r.Result == "complete");
            d.CompletionRateBySection[g.Key] = g.Count() == 0 ? 0 : (double)complete / g.Count();
        }

        return d;
    }
}
```

- [ ] **Step 2: Build + commit**

`dotnet build` green.
```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/Digesters/RacesDigester.cs
git commit -m "feat(agent): RacesDigester win rates, kbps matrix, abort histogram"
```

---

### Task 4.4: Remaining digesters (Nukes, SiteHealth, Announces, Wishlist, Overrides, Downloads, Transfers, SectionActivity, Errors)

**Files:**
- Create: `src/GlDrive/AiAgent/Digesters/NukesDigester.cs`
- Create: `src/GlDrive/AiAgent/Digesters/SiteHealthDigester.cs`
- Create: `src/GlDrive/AiAgent/Digesters/AnnouncesDigester.cs`
- Create: `src/GlDrive/AiAgent/Digesters/WishlistDigester.cs`
- Create: `src/GlDrive/AiAgent/Digesters/OverridesDigester.cs`
- Create: `src/GlDrive/AiAgent/Digesters/DownloadsDigester.cs`
- Create: `src/GlDrive/AiAgent/Digesters/TransfersDigester.cs`
- Create: `src/GlDrive/AiAgent/Digesters/SectionActivityDigester.cs`
- Create: `src/GlDrive/AiAgent/Digesters/ErrorsDigester.cs`

- [ ] **Step 1: NukesDigester**

```csharp
namespace GlDrive.AiAgent;

public sealed class NukesDigester
{
    public NukesDigest Build(IEnumerable<NukeDetectedEvent> events)
    {
        var list = events.ToList();
        var d = new NukesDigest
        {
            Total = list.Count,
            Correlated = list.Count(n => !string.IsNullOrEmpty(n.OurRaceRef))
        };
        d.TopNukedReleases = list
            .GroupBy(n => n.Release)
            .OrderByDescending(g => g.Count())
            .Take(20)
            .Select(g => new NukesDigest.NukeTop
            {
                Release = g.Key,
                Count = g.Count(),
                Reason = g.First().Reason,
                Section = g.First().Section
            }).ToList();
        foreach (var g in list.GroupBy(n => n.Section))
            d.NukeRateBySection[g.Key] = g.Count();
        return d;
    }
}
```

- [ ] **Step 2: SiteHealthDigester**

```csharp
namespace GlDrive.AiAgent;

public sealed class SiteHealthDigester
{
    public SiteHealthDigest Build(IEnumerable<SiteHealthEvent> events)
    {
        var d = new SiteHealthDigest();
        foreach (var g in events.GroupBy(e => e.ServerId))
        {
            var rows = g.OrderBy(e => e.WindowStart).ToList();
            if (rows.Count == 0) continue;
            var first = rows.Take(Math.Max(1, rows.Count / 4)).ToList();
            var last  = rows.Skip(Math.Max(0, 3 * rows.Count / 4)).ToList();
            double PctChange(Func<SiteHealthEvent, double> sel)
            {
                var a = first.Average(sel); var b = last.Average(sel);
                return a == 0 ? 0 : (b - a) / a;
            }
            var delta = new SiteHealthDigest.HealthDelta
            {
                AvgConnectMsPctChange = PctChange(e => e.AvgConnectMs),
                TlsHandshakePctChange = PctChange(e => e.TlsHandshakeMs),
                DisconnectsTotal = rows.Sum(e => e.Disconnects),
                PoolExhaustTotal = rows.Sum(e => e.PoolExhaustCount),
                GhostKillsTotal  = rows.Sum(e => e.GhostKills)
            };
            if (delta.AvgConnectMsPctChange > 0.5) delta.Flagged.Add("connect-latency-regression");
            if (delta.PoolExhaustTotal > 5)         delta.Flagged.Add("pool-exhaustion");
            if (delta.DisconnectsTotal > 20)        delta.Flagged.Add("frequent-disconnects");
            d.ServerDeltas[g.Key] = delta;
        }
        return d;
    }
}
```

- [ ] **Step 3: AnnouncesDigester** (cluster by normalized message, representative = longest common shape)

```csharp
using System.Text.RegularExpressions;

namespace GlDrive.AiAgent;

public sealed class AnnouncesDigester
{
    public AnnouncesDigest Build(IEnumerable<AnnounceNoMatchEvent> events)
    {
        var d = new AnnouncesDigest();
        string Normalize(string m) => Regex.Replace(m, @"[A-Z0-9][A-Za-z0-9\.\-_]{3,}", "<rel>");
        var clusters = events.GroupBy(e => (e.Channel, e.BotNick, Normalize(e.Message)));
        foreach (var g in clusters.OrderByDescending(g => g.Count()).Take(30))
        {
            var rep = g.First();
            d.Clusters.Add(new AnnouncesDigest.AnnounceCluster
            {
                Representative = rep.Message,
                Count = g.Count(),
                Channel = rep.Channel,
                BotNick = rep.BotNick,
                NearestRule = rep.NearestRulePattern,
                NearestRuleDistance = rep.NearestRuleDistance
            });
        }
        return d;
    }
}
```

- [ ] **Step 4: WishlistDigester**

```csharp
namespace GlDrive.AiAgent;

public sealed class WishlistDigester
{
    public WishlistDigest Build(IEnumerable<WishlistAttemptEvent> events)
    {
        var d = new WishlistDigest();
        var byItem = events.GroupBy(e => e.WishlistItemId).ToList();
        foreach (var g in byItem)
        {
            var matched = g.Where(e => e.Matched).ToList();
            if (matched.Count == 0)
                d.DeadItems.Add(new WishlistDigest.DeadItem
                {
                    ItemId = g.Key,
                    AttemptsInWindow = g.Count(),
                    DaysSinceLastMatch = 60
                });
        }
        foreach (var g in events.Where(e => !e.Matched && !string.IsNullOrEmpty(e.MissReason))
                                .GroupBy(e => (e.WishlistItemId, e.MissReason!))
                                .OrderByDescending(g => g.Count()).Take(25))
            d.NearMissPatterns.Add(new WishlistDigest.NearMiss
            {
                ItemId = g.Key.WishlistItemId,
                MissReason = g.Key.Item2,
                Count = g.Count()
            });
        return d;
    }
}
```

- [ ] **Step 5: OverridesDigester**

```csharp
namespace GlDrive.AiAgent;

public sealed class OverridesDigester
{
    public OverridesDigest Build(IEnumerable<ConfigOverrideEvent> events)
    {
        var d = new OverridesDigest();
        d.Paths = events.Select(e => e.JsonPointer).Distinct().Take(200).ToList();
        d.RevertedAiPaths = events.Where(e => !string.IsNullOrEmpty(e.AiAuditRef))
                                  .Select(e => e.JsonPointer).Distinct().Take(100).ToList();
        return d;
    }
}
```

- [ ] **Step 6: DownloadsDigester**

```csharp
namespace GlDrive.AiAgent;

public sealed class DownloadsDigester
{
    public DownloadsDigest Build(IEnumerable<DownloadOutcomeEvent> events)
    {
        var d = new DownloadsDigest();
        d.TotalComplete = events.Count(e => e.Result == "complete");
        d.TotalFailed   = events.Count(e => e.Result == "failed");
        foreach (var g in events.Where(e => !string.IsNullOrEmpty(e.FailureClass))
                                .GroupBy(e => e.FailureClass!))
            d.FailureClassHistogram[g.Key] = g.Count();
        return d;
    }
}
```

- [ ] **Step 7: TransfersDigester**

```csharp
namespace GlDrive.AiAgent;

public sealed class TransfersDigester
{
    public TransfersDigest Build(IEnumerable<FileTransferEvent> events)
    {
        var list = events.ToList();
        var d = new TransfersDigest();
        foreach (var g in list.GroupBy(e => $"{e.SrcServer}->{e.DstServer}"))
        {
            var bytes = g.Sum(e => e.Bytes);
            var ms = g.Sum(e => e.ElapsedMs);
            d.KbpsMatrix[g.Key] = ms == 0 ? 0 : bytes * 8.0 / ms;
        }
        if (list.Count > 0)
        {
            var ttfbs = list.Select(e => (double)e.TtfbMs).OrderBy(x => x).ToList();
            var idx = (int)Math.Clamp(Math.Round(0.99 * (ttfbs.Count - 1)), 0, ttfbs.Count - 1);
            d.TtfbP99Ms = ttfbs[idx];
        }
        return d;
    }
}
```

- [ ] **Step 8: SectionActivityDigester**

```csharp
namespace GlDrive.AiAgent;

public sealed class SectionActivityDigester
{
    public SectionActivityDigest Build(IEnumerable<SectionActivityEvent> events)
    {
        var d = new SectionActivityDigest();
        foreach (var g in events.GroupBy(e => (e.ServerId, e.Section)))
        {
            var filesIn = g.Sum(e => e.FilesIn);
            var races = g.Sum(e => e.OurRaces);
            var wins = g.Sum(e => e.OurWins);
            d.PerServerSection.Add(new SectionActivityDigest.Row
            {
                ServerId = g.Key.ServerId,
                Section = g.Key.Section,
                FilesIn = filesIn,
                OurRaces = races,
                OurWinRate = races == 0 ? 0 : (double)wins / races
            });
        }
        return d;
    }
}
```

- [ ] **Step 9: ErrorsDigester**

```csharp
namespace GlDrive.AiAgent;

public sealed class ErrorsDigester
{
    public ErrorsDigest Build(IEnumerable<ErrorSignatureEvent> events)
    {
        var list = events.ToList();
        var d = new ErrorsDigest();
        var byHalf = list.Count == 0 ? (new List<ErrorSignatureEvent>(), new List<ErrorSignatureEvent>())
            : (list.Take(list.Count / 2).ToList(), list.Skip(list.Count / 2).ToList());
        var priorCounts = byHalf.Item1
            .GroupBy(e => (e.Component, e.ExceptionType, e.NormalizedMessage))
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Count));

        foreach (var g in list.GroupBy(e => (e.Component, e.ExceptionType, e.NormalizedMessage))
                              .OrderByDescending(g => g.Sum(e => e.Count)).Take(15))
        {
            var total = g.Sum(e => e.Count);
            var prior = priorCounts.GetValueOrDefault(g.Key, 0);
            var trend = total > prior * 1.25 ? "up" : total < prior * 0.75 ? "down" : "flat";
            d.TopSignatures.Add(new ErrorsDigest.Sig
            {
                Component = g.Key.Component,
                ExceptionType = g.Key.ExceptionType,
                NormalizedMessage = g.Key.NormalizedMessage,
                Count = total,
                TrendVsPrior = trend
            });
        }
        return d;
    }
}
```

- [ ] **Step 10: Build + commit**

`dotnet build` green.
```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/Digesters/
git commit -m "feat(agent): per-stream digesters compress 7d telemetry into prompt-ready digests"
```

---

### Task 4.5: Debug hook — "Build digest (debug)" menu item

**Files:**
- Modify: `src/GlDrive/UI/TrayViewModel.cs`

- [ ] **Step 1: Add command + tray menu entry**

Add a command on TrayViewModel:
```csharp
public ICommand BuildDigestDebugCommand => new RelayCommand(() =>
{
    try
    {
        var appDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlDrive");
        var aiData = Path.Combine(appDataRoot, "ai-data");
        var digester = new GlDrive.AiAgent.LogDigester(aiData);
        var bundle = digester.Build(_config.Agent.WindowDays);
        var outPath = Path.Combine(aiData, "last-digest.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(bundle,
            new JsonSerializerOptions { WriteIndented = true }));
        var approxTokens = new FileInfo(outPath).Length / 4;
        MessageBox.Show($"Digest written to {outPath}\nApprox tokens: {approxTokens:N0}");
    }
    catch (Exception ex) { MessageBox.Show("Digest failed: " + ex.Message); }
});
```
Wire into tray menu under "AI Agent" submenu (submenu lands in Phase 10; for now, add a flat "Build digest (debug)" item).

- [ ] **Step 2: Build + runtime check**

`dotnet build` green. Run app, click "Build digest (debug)". Confirm `last-digest.json` appears and contains all 10 digest sections.

- [ ] **Step 3: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/UI/TrayViewModel.cs
git commit -m "feat(agent): debug tray command to build + dump 7d digest"
```

---

**Phase 4 complete.** Telemetry → compact digest pipeline exists and can be inspected end-to-end.

---

## Phase 5 — FreezeStore + field annotations

Ships: JSON-pointer-based frozen-field storage, attached-property UX, and annotations on the ~30 supported fields.

### Task 5.1: `JsonPointer` helper

**Files:**
- Create: `src/GlDrive/AiAgent/JsonPointer.cs`

- [ ] **Step 1: Write RFC 6901 JSON Pointer helpers**

```csharp
using System.Text.Json.Nodes;

namespace GlDrive.AiAgent;

public static class JsonPointer
{
    public static string Escape(string token)
        => token.Replace("~", "~0").Replace("/", "~1");

    public static string Unescape(string token)
        => token.Replace("~1", "/").Replace("~0", "~");

    public static string[] Split(string pointer)
    {
        if (string.IsNullOrEmpty(pointer)) return Array.Empty<string>();
        if (pointer[0] != '/') throw new ArgumentException("JSON Pointer must start with /");
        return pointer[1..].Split('/').Select(Unescape).ToArray();
    }

    public static JsonNode? Resolve(JsonNode root, string pointer)
    {
        if (string.IsNullOrEmpty(pointer)) return root;
        JsonNode? cur = root;
        foreach (var tok in Split(pointer))
        {
            if (cur is JsonObject obj) cur = obj.TryGetPropertyValue(tok, out var v) ? v : null;
            else if (cur is JsonArray arr && int.TryParse(tok, out var i) && i >= 0 && i < arr.Count)
                cur = arr[i];
            else return null;
            if (cur is null) return null;
        }
        return cur;
    }

    public static bool IsAncestorOrSelf(string maybeAncestor, string path)
        => path == maybeAncestor || path.StartsWith(maybeAncestor + "/", StringComparison.Ordinal);
}
```

- [ ] **Step 2: Build + commit**

`dotnet build` green.
```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/JsonPointer.cs
git commit -m "feat(agent): JsonPointer helper (RFC 6901) for path resolution + ancestor check"
```

---

### Task 5.2: `FreezeStore`

**Files:**
- Create: `src/GlDrive/AiAgent/FreezeStore.cs`

- [ ] **Step 1: Write store**

```csharp
using System.IO;
using System.Text.Json;

namespace GlDrive.AiAgent;

public sealed record FreezeEntry(string Path, string FrozenAt, string? Note);

public sealed class FreezeStore
{
    private readonly string _path;
    private List<FreezeEntry> _entries = new();
    private readonly object _lock = new();
    public event Action? Changed;

    public FreezeStore(string aiDataRoot)
    {
        _path = Path.Combine(aiDataRoot, "frozen.json");
        Load();
    }

    public IReadOnlyList<FreezeEntry> All { get { lock (_lock) return _entries.ToList(); } }

    public bool IsFrozen(string pointer)
    {
        lock (_lock)
            return _entries.Any(e => JsonPointer.IsAncestorOrSelf(e.Path, pointer));
    }

    public void Freeze(string pointer, string? note = null)
    {
        lock (_lock)
        {
            if (_entries.Any(e => e.Path == pointer)) return;
            _entries.Add(new FreezeEntry(pointer, DateTime.UtcNow.ToString("O"), note));
            Save();
        }
        Changed?.Invoke();
    }

    public void Unfreeze(string pointer)
    {
        lock (_lock)
        {
            var removed = _entries.RemoveAll(e => e.Path == pointer);
            if (removed > 0) Save();
            else return;
        }
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _entries = JsonSerializer.Deserialize<List<FreezeEntry>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "FreezeStore load failed; treating as empty"); _entries = new(); }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path,
                JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "FreezeStore save failed"); }
    }
}
```

- [ ] **Step 2: Register singleton in App startup**

Add `public static FreezeStore? FreezeStore { get; private set; }` on `App`; construct after `aiData` is known.

- [ ] **Step 3: Build + commit**

`dotnet build` green.
```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/FreezeStore.cs src/GlDrive/App.xaml.cs
git commit -m "feat(agent): FreezeStore with ancestor-match + change events"
```

---

### Task 5.3: `AiAgent.FreezablePath` attached property + ContextMenu style

**Files:**
- Create: `src/GlDrive/AiAgent/AiAgentAttached.cs`
- Modify: `src/GlDrive/UI/Themes/DarkTheme.xaml`
- Modify: `src/GlDrive/UI/Themes/LightTheme.xaml`

- [ ] **Step 1: Attached property**

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GlDrive.AiAgent;

public static class AiAgentAttached
{
    public static readonly DependencyProperty FreezablePathProperty = DependencyProperty.RegisterAttached(
        "FreezablePath", typeof(string), typeof(AiAgentAttached),
        new PropertyMetadata(null, OnFreezablePathChanged));

    public static string? GetFreezablePath(DependencyObject d) => (string?)d.GetValue(FreezablePathProperty);
    public static void SetFreezablePath(DependencyObject d, string? v) => d.SetValue(FreezablePathProperty, v);

    private static void OnFreezablePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if (string.IsNullOrWhiteSpace(e.NewValue as string)) { fe.ContextMenu = null; return; }

        var cm = new ContextMenu();
        var toggle = new MenuItem { Header = "Freeze for AI" };
        toggle.Click += (_, __) =>
        {
            var store = App.FreezeStore; var path = GetFreezablePath(fe);
            if (store is null || string.IsNullOrWhiteSpace(path)) return;
            if (store.IsFrozen(path)) store.Unfreeze(path);
            else store.Freeze(path);
            UpdateMenuLabel(toggle, store, path!);
            UpdateLockAdorner(fe, store, path!);
        };
        cm.Items.Add(toggle);
        fe.ContextMenu = cm;

        fe.Loaded += (_, __) =>
        {
            var store = App.FreezeStore; var path = GetFreezablePath(fe);
            if (store is null || string.IsNullOrWhiteSpace(path)) return;
            UpdateMenuLabel(toggle, store, path!);
            UpdateLockAdorner(fe, store, path!);
            store.Changed += () =>
            {
                fe.Dispatcher.Invoke(() =>
                {
                    UpdateMenuLabel(toggle, store, path!);
                    UpdateLockAdorner(fe, store, path!);
                });
            };
        };
    }

    private static void UpdateMenuLabel(MenuItem mi, FreezeStore store, string path)
        => mi.Header = store.IsFrozen(path) ? "Unfreeze (AI may change)" : "Freeze for AI";

    private static void UpdateLockAdorner(FrameworkElement fe, FreezeStore store, string path)
    {
        fe.ToolTip = store.IsFrozen(path) ? $"Frozen — AI agent will not modify this ({path})" : null;
        // Simple visual: dashed border when frozen
        if (fe is Control c)
            c.BorderBrush = store.IsFrozen(path)
                ? System.Windows.Application.Current.TryFindResource("AccentBrush") as System.Windows.Media.Brush
                : c.BorderBrush;
    }
}
```

(Full lock glyph adorner can be added later; the tooltip + border tint is enough to start.)

- [ ] **Step 2: Build + commit**

`dotnet build` green.
```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/AiAgentAttached.cs
git commit -m "feat(agent): FreezablePath attached property with right-click freeze toggle"
```

---

### Task 5.4: Annotate the ~30 freezable fields in UI XAML

**Files:**
- Modify: `src/GlDrive/UI/ServerEditDialog.xaml`
- Modify: `src/GlDrive/UI/SettingsWindow.xaml`

- [ ] **Step 1: Add xmlns + annotations in `ServerEditDialog.xaml`**

Add namespace on root element: `xmlns:aia="clr-namespace:GlDrive.AiAgent"`.

Then annotate each freezable field. Example for `maxSlots`:
```xml
<TextBox Text="{Binding MaxSlots}" aia:AiAgentAttached.FreezablePath="{Binding MaxSlotsFreezePath}"/>
```

Add these computed paths on the edit-dialog viewmodel (or code-behind):
```csharp
public string MaxSlotsFreezePath        => $"/servers/{_serverId}/spread/maxSlots";
public string SpreadPoolSizeFreezePath  => $"/servers/{_serverId}/spread/spreadPoolSize";
public string SitePriorityFreezePath    => $"/servers/{_serverId}/spread/sitePriority";
public string AffilsFreezePath          => $"/servers/{_serverId}/spread/affils";
public string SkiplistFreezePath        => $"/servers/{_serverId}/spread/skiplistRules";
public string BlacklistFreezePath       => $"/servers/{_serverId}/spread/blacklist";
public string SectionMappingsFreezePath => $"/servers/{_serverId}/spread/sectionMappings";
public string AnnounceRulesFreezePath   => $"/servers/{_serverId}/irc/announceRules";
public string ExcludedCategoriesFreezePath => $"/servers/{_serverId}/notifications/excludedCategories";
```

Annotate each bound field (including DataGrids for rules, affils, skiplist, etc.).

- [ ] **Step 2: Annotate global fields in `SettingsWindow.xaml`**

Add namespace + annotate `MaxConcurrentRaces`:
```xml
<TextBox Text="{Binding MaxConcurrentRaces}" aia:AiAgentAttached.FreezablePath="/spread/maxConcurrentRaces"/>
```

- [ ] **Step 3: Build + runtime check**

`dotnet build` green. Launch app, open ServerEditDialog → right-click a freezable field → "Freeze for AI" appears → click → field now has tooltip "Frozen — …". `ai-data/frozen.json` contains the entry.

- [ ] **Step 4: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/UI/ServerEditDialog.xaml src/GlDrive/UI/SettingsWindow.xaml src/GlDrive/UI/ServerEditDialog.xaml.cs
git commit -m "feat(agent): annotate ~30 freezable config fields with FreezablePath"
```

---

**Phase 5 complete.** Freeze mechanism wired end-to-end.

---

## Phase 6 — AgentPrompt + AgentClient

Ships: the LLM round-trip — schemas, prompt composer, client.

### Task 6.1: `AgentRunResult` schemas

**Files:**
- Create: `src/GlDrive/AiAgent/AgentRunResult.cs`

- [ ] **Step 1: Define result + change shapes**

```csharp
using System.Text.Json.Serialization;

namespace GlDrive.AiAgent;

public sealed class AgentRunResult
{
    [JsonPropertyName("memo_update")]    public string MemoUpdate { get; set; } = "";
    [JsonPropertyName("changes")]        public List<AgentChange> Changes { get; set; } = [];
    [JsonPropertyName("suggestions")]    public List<AgentChange> Suggestions { get; set; } = [];
    [JsonPropertyName("brief_markdown")] public string BriefMarkdown { get; set; } = "";
}

public sealed class AgentChange
{
    [JsonPropertyName("category")]     public string Category { get; set; } = "";
    [JsonPropertyName("target")]       public string Target { get; set; } = "";
    [JsonPropertyName("before")]       public object? Before { get; set; }
    [JsonPropertyName("after")]        public object? After { get; set; }
    [JsonPropertyName("reasoning")]    public string Reasoning { get; set; } = "";
    [JsonPropertyName("evidence_ref")] public string EvidenceRef { get; set; } = "";
    [JsonPropertyName("confidence")]   public double Confidence { get; set; }
}

public static class AgentCategories
{
    public const string Skiplist = "skiplist";
    public const string Priority = "priority";
    public const string SectionMapping = "sectionMapping";
    public const string AnnounceRule = "announceRule";
    public const string ExcludedCategories = "excludedCategories";
    public const string WishlistPrune = "wishlistPrune";
    public const string PoolSizing = "poolSizing";
    public const string Blacklist = "blacklist";
    public const string Affils = "affils";
    public const string ErrorReport = "errorReport";

    public static readonly string[] All =
        { Skiplist, Priority, SectionMapping, AnnounceRule, ExcludedCategories,
          WishlistPrune, PoolSizing, Blacklist, Affils, ErrorReport };
}
```

- [ ] **Step 2: Build + commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/AgentRunResult.cs
git commit -m "feat(agent): AgentRunResult + AgentChange + AgentCategories constants"
```

---

### Task 6.2: `AgentPrompt` composer

**Files:**
- Create: `src/GlDrive/AiAgent/AgentPrompt.cs`

- [ ] **Step 1: Write composer**

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlDrive.AiAgent;

public sealed class AgentPrompt
{
    public const string SystemPrompt = """
        You are an operations agent for GlDrive, a Windows app that races files between glftpd FTP servers.
        Your job: analyze N days of structured telemetry and propose config changes within the TEN allowed
        categories + invariants below. NEVER touch frozen paths. Cite evidence for every change.
        Return STRICT JSON matching the schema. If unsure, prefer LOW confidence or emit NOTHING.

        CATEGORIES (must be one of these strings):
        - skiplist: add/update/remove per-site deny rules.
        - priority: bump site priority ±1 tier (never to VeryHigh autonomously).
        - sectionMapping: add row or patch trigger IF existing trigger is default (.* or empty).
        - announceRule: add rule or patch existing; new pattern must compile AND match ≥3 nomatch samples.
        - excludedCategories: add section key to a server's excluded notifications.
        - wishlistPrune: soft-mark "dead" or hard-remove wishlist item per invariants.
        - poolSizing: tweak SpreadPoolSize, maxSlots, maxConcurrentRaces (±25%, absolute [2,32]).
        - blacklist: add/extend/remove (site, section) persistent blacklist entry.
        - affils: add group to site affils (never remove).
        - errorReport: INFORMATIONAL ONLY — emits a Markdown issue report, never mutates config.

        INVARIANTS (the Applier will re-validate and reject violations, but you should honor them):
        - Max 20 total changes per run. Max 5 per category.
        - Confidence is a float 0.0-1.0. Below the configured threshold → goes to suggestions[] not changes[].
        - `target` must be a JSON Pointer (RFC 6901) to a field in the current config.
        - `before` must match the current value at `target` (the Applier cross-checks).
        - For list appends, use `"/path/-"` as target and include `after` as the new element only.

        FROZEN PATHS list is provided below. Producing any change whose target is frozen (or a descendant
        of a frozen path) is a bug — such changes will be rejected with reason "frozen".

        Respond with ONLY a JSON object (no markdown fences, no text outside):
        {
          "memo_update": "…full replacement for agent-memo.md (your long-running beliefs)…",
          "changes": [ AgentChange, ... ],
          "suggestions": [ AgentChange, ... ],
          "brief_markdown": "…Markdown summary — headline + per-category cards…"
        }

        AgentChange shape:
        {
          "category": "skiplist",
          "target": "/servers/srv-abc/spread/skiplistRules/-",
          "before": null,
          "after": { "pattern": "*DUBBED*", "isRegex": false, "action": "Deny", ... },
          "reasoning": "Site X rejected 14/14 DUBBED in window.",
          "evidence_ref": "races-20260418.jsonl:12-34",
          "confidence": 0.92
        }
        """;

    public string Compose(DigestBundle digest, string memo, IEnumerable<string> frozenPaths,
                          JsonNode redactedConfig, IEnumerable<string> lastAuditSummaries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== WINDOW ===");
        sb.AppendLine($"{digest.WindowStart} → {digest.WindowEnd}");

        sb.AppendLine("\n=== AGENT MEMO (carry-forward beliefs) ===");
        sb.AppendLine(string.IsNullOrWhiteSpace(memo) ? "(empty — first run)" : memo);

        sb.AppendLine("\n=== FROZEN PATHS (do NOT touch these or any descendants) ===");
        foreach (var p in frozenPaths.Take(500)) sb.AppendLine(p);

        sb.AppendLine("\n=== LAST 3 RUNS (audit summary) ===");
        foreach (var s in lastAuditSummaries.Take(3)) sb.AppendLine(s);

        sb.AppendLine("\n=== TELEMETRY DIGEST (7-day compact) ===");
        sb.AppendLine(JsonSerializer.Serialize(digest,
            new JsonSerializerOptions { WriteIndented = false }));

        sb.AppendLine("\n=== CURRENT CONFIG (frozen paths masked as ***FROZEN***) ===");
        sb.AppendLine(redactedConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));

        sb.AppendLine("\nEmit STRICT JSON: { memo_update, changes[], suggestions[], brief_markdown }.");
        return sb.ToString();
    }

    public static JsonNode RedactFrozen(JsonNode original, IEnumerable<string> frozenPaths)
    {
        var root = JsonNode.Parse(original.ToJsonString())!;
        foreach (var fp in frozenPaths.OrderByDescending(p => p.Length))
        {
            var tokens = JsonPointer.Split(fp);
            if (tokens.Length == 0) continue;
            JsonNode? cur = root;
            JsonNode? parent = null; string? lastToken = null;
            for (int i = 0; i < tokens.Length; i++)
            {
                parent = cur;
                lastToken = tokens[i];
                if (cur is JsonObject obj) cur = obj.TryGetPropertyValue(lastToken, out var v) ? v : null;
                else if (cur is JsonArray arr && int.TryParse(lastToken, out var idx) && idx < arr.Count)
                    cur = arr[idx];
                else { cur = null; break; }
                if (cur is null) break;
            }
            if (parent is JsonObject po && lastToken != null && po.ContainsKey(lastToken))
                po[lastToken] = "***FROZEN***";
        }
        return root;
    }
}
```

- [ ] **Step 2: Build + commit**

`dotnet build` green.
```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/AgentPrompt.cs
git commit -m "feat(agent): AgentPrompt composer + RedactFrozen masks frozen config values"
```

---

### Task 6.3: `AgentClient` (OpenRouter call with json-object response format)

**Files:**
- Create: `src/GlDrive/AiAgent/AgentClient.cs`

- [ ] **Step 1: Write client**

```csharp
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Spread;  // reuse RepairTruncatedJson pattern
using Serilog;

namespace GlDrive.AiAgent;

public sealed class AgentRunOutcome
{
    public AgentRunResult? Result { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public double EstimatedCostUsd { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class AgentClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string _model;

    public AgentClient(string apiKey, string model)
    {
        _model = model;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/"),
            Timeout = TimeSpan.FromMinutes(5)
        };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        _http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/misterentity/GlDrive");
        _http.DefaultRequestHeaders.Add("X-Title", "GlDrive Agent");
    }

    public async Task<AgentRunOutcome> RunAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var request = new
        {
            model = _model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = 0.2,
            max_tokens = 32000,
            response_format = new { type = "json_object" }
        };

        var body = JsonSerializer.Serialize(request, JsonOpts);
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        try
        {
            var resp = await _http.PostAsync("chat/completions", content, ct);
            var responseBody = await resp.Content.ReadAsStringAsync(ct);
            Log.Information("AgentClient response status={Status} bytes={Bytes}",
                resp.StatusCode, responseBody.Length);

            if (!resp.IsSuccessStatusCode)
                return new AgentRunOutcome { ErrorMessage = $"HTTP {(int)resp.StatusCode}" };

            using var doc = JsonDocument.Parse(responseBody);
            var msg = doc.RootElement.GetProperty("choices")[0]
                         .GetProperty("message").GetProperty("content").GetString() ?? "";
            var usage = doc.RootElement.TryGetProperty("usage", out var u) ? u : default;
            int inputTok = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
            int outputTok = usage.TryGetProperty("completion_tokens", out var ct2) ? ct2.GetInt32() : 0;

            var jsonStart = msg.IndexOf('{');
            var jsonEnd = msg.LastIndexOf('}');
            AgentRunResult? result = null;
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var rawJson = msg[jsonStart..(jsonEnd + 1)];
                try { result = JsonSerializer.Deserialize<AgentRunResult>(rawJson, JsonOpts); }
                catch (JsonException ex)
                {
                    Log.Warning("Agent JSON parse fail, attempting repair: {Msg}", ex.Message);
                    var repaired = RepairJson(rawJson);
                    if (repaired != null)
                    {
                        try { result = JsonSerializer.Deserialize<AgentRunResult>(repaired, JsonOpts); }
                        catch { /* give up */ }
                    }
                }
            }

            return new AgentRunOutcome
            {
                Result = result,
                InputTokens = inputTok,
                OutputTokens = outputTok,
                EstimatedCostUsd = EstimateCost(_model, inputTok, outputTok),
                ErrorMessage = result is null ? "failed-to-parse-json" : null
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AgentClient call failed");
            return new AgentRunOutcome { ErrorMessage = ex.Message };
        }
    }

    // Rough guesses — refine when pricing changes.
    private static double EstimateCost(string model, int inTok, int outTok)
    {
        double ip = 3.0 / 1e6, op = 15.0 / 1e6; // Sonnet default
        if (model.Contains("opus")) { ip = 15.0 / 1e6; op = 75.0 / 1e6; }
        else if (model.Contains("gemini-2.5-pro")) { ip = 1.25 / 1e6; op = 5.0 / 1e6; }
        else if (model.Contains(":free")) { ip = 0; op = 0; }
        return inTok * ip + outTok * op;
    }

    private static string? RepairJson(string json)
    {
        // Reuse the logic pattern from OpenRouterClient.RepairTruncatedJson — duplicate for decoupling
        try
        {
            var lastComplete = -1; var depth = 0; var inString = false; var escape = false;
            for (var i = 0; i < json.Length; i++)
            {
                var c = json[i];
                if (escape) { escape = false; continue; }
                if (c == '\\' && inString) { escape = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c is '{' or '[') depth++;
                else if (c is '}' or ']') { depth--; lastComplete = i; }
            }
            if (depth <= 0) return null;
            var truncateAt = json.Length;
            for (var i = json.Length - 1; i > lastComplete; i--)
                if (json[i] == ',') { truncateAt = i; break; }
            if (truncateAt == json.Length) truncateAt = lastComplete + 1;
            var repaired = json[..truncateAt];
            int ob = 0, obr = 0; inString = false; escape = false;
            foreach (var c in repaired)
            {
                if (escape) { escape = false; continue; }
                if (c == '\\' && inString) { escape = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') ob++; else if (c == '}') ob--;
                else if (c == '[') obr++; else if (c == ']') obr--;
            }
            for (var i = 0; i < obr; i++) repaired += "]";
            for (var i = 0; i < ob; i++) repaired += "}";
            return repaired;
        }
        catch { return null; }
    }

    public void Dispose() => _http.Dispose();
}
```

- [ ] **Step 2: Build + commit**

`dotnet build` green.
```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/AgentClient.cs
git commit -m "feat(agent): AgentClient calls OpenRouter with json_object format + cost capture"
```

---

**Phase 6 complete.** Full LLM round-trip plumbing in place, ready to call.

---

## Phase 7 — ChangeApplier + 10 validators

Ships: validation + mutation dispatcher and 10 per-category validators.

### Task 7.1: `ChangeApplier` dispatcher

**Files:**
- Create: `src/GlDrive/AiAgent/ChangeApplier.cs`
- Create: `src/GlDrive/AiAgent/Validators/IChangeValidator.cs`

- [ ] **Step 1: Validator interface**

```csharp
namespace GlDrive.AiAgent;

public sealed record ValidationResult(bool Ok, string? RejectionReason, Action<GlDrive.Config.AppConfig>? Mutate);

public interface IChangeValidator
{
    string Category { get; }
    ValidationResult Validate(AgentChange change, GlDrive.Config.AppConfig config);
}
```

- [ ] **Step 2: Dispatcher**

```csharp
using Serilog;

namespace GlDrive.AiAgent;

public sealed class ChangeApplier
{
    private readonly Dictionary<string, IChangeValidator> _validators;
    private readonly FreezeStore _freeze;
    private readonly AuditTrail _audit;

    public ChangeApplier(IEnumerable<IChangeValidator> validators, FreezeStore freeze, AuditTrail audit)
    {
        _validators = validators.ToDictionary(v => v.Category);
        _freeze = freeze; _audit = audit;
    }

    public sealed class RunReport
    {
        public int Applied { get; set; }
        public int Rejected { get; set; }
        public Dictionary<string, int> RejectionByReason { get; } = new();
        public Dictionary<string, int> AppliedByCategory { get; } = new();
    }

    public RunReport Apply(IEnumerable<AgentChange> changes, GlDrive.Config.AppConfig config,
                           AgentConfig agentCfg, string runId, bool dryRun)
    {
        var report = new RunReport();
        var perCategoryCount = new Dictionary<string, int>();

        foreach (var change in changes)
        {
            string? reject = null;

            if (_freeze.IsFrozen(change.Target))
                reject = "frozen";
            else if (!_validators.TryGetValue(change.Category, out var v))
                reject = "unknown-category";
            else if (change.Confidence < agentCfg.ConfidenceThreshold_x100 / 100.0
                     && change.Category != AgentCategories.ErrorReport)
                reject = "low-confidence";
            else if (report.Applied >= agentCfg.MaxChangesPerRun)
                reject = "budget-exceeded-total";
            else if (perCategoryCount.GetValueOrDefault(change.Category) >= agentCfg.MaxChangesPerCategory)
                reject = "budget-exceeded-category";

            if (reject is null)
            {
                var vr = _validators[change.Category].Validate(change, config);
                if (!vr.Ok) reject = vr.RejectionReason ?? "invariant-failed";
                else
                {
                    if (!dryRun)
                    {
                        try { vr.Mutate?.Invoke(config); }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "ChangeApplier mutation threw for {Category} {Target}", change.Category, change.Target);
                            reject = "mutation-threw:" + ex.GetType().Name;
                        }
                    }
                    if (reject is null)
                    {
                        _audit.Append(new AuditRow
                        {
                            RunId = runId, Category = change.Category, Target = change.Target,
                            Before = change.Before, After = change.After,
                            Reasoning = change.Reasoning, EvidenceRef = change.EvidenceRef,
                            Confidence = change.Confidence, Applied = true, DryRun = dryRun
                        });
                        report.Applied++;
                        perCategoryCount[change.Category] = perCategoryCount.GetValueOrDefault(change.Category) + 1;
                        report.AppliedByCategory[change.Category] = perCategoryCount[change.Category];
                        continue;
                    }
                }
            }

            _audit.Append(new AuditRow
            {
                RunId = runId, Category = change.Category, Target = change.Target,
                Before = change.Before, After = change.After,
                Reasoning = change.Reasoning, EvidenceRef = change.EvidenceRef,
                Confidence = change.Confidence, Applied = false, DryRun = dryRun,
                RejectionReason = reject
            });
            report.Rejected++;
            report.RejectionByReason[reject!] = report.RejectionByReason.GetValueOrDefault(reject!) + 1;
        }
        return report;
    }
}
```

`AuditRow` and `AuditTrail` are introduced in Phase 8.1 — declare them as placeholders now and implement there. To keep this task self-contained, create stubs:

```csharp
// src/GlDrive/AiAgent/AuditTrail.cs  (stub — full impl in Task 8.1)
namespace GlDrive.AiAgent;

public class AuditRow
{
    public string Ts { get; set; } = DateTime.UtcNow.ToString("O");
    public string RunId { get; set; } = "";
    public string Category { get; set; } = "";
    public string Target { get; set; } = "";
    public object? Before { get; set; }
    public object? After { get; set; }
    public string Reasoning { get; set; } = "";
    public string EvidenceRef { get; set; } = "";
    public double Confidence { get; set; }
    public bool Applied { get; set; }
    public bool DryRun { get; set; }
    public string? RejectionReason { get; set; }
    public bool Undone { get; set; }
    public string? UndoneAt { get; set; }
    public string? UndoneReason { get; set; }
}

public class AuditTrail
{
    public virtual void Append(AuditRow row) { /* full impl in Task 8.1 */ }
}
```

- [ ] **Step 3: Build + commit**

`dotnet build` green.
```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/Validators/IChangeValidator.cs src/GlDrive/AiAgent/ChangeApplier.cs src/GlDrive/AiAgent/AuditTrail.cs
git commit -m "feat(agent): ChangeApplier dispatcher with freeze/confidence/budget gating"
```

---

### Task 7.2: `SkiplistValidator`

**Files:**
- Create: `src/GlDrive/AiAgent/Validators/SkiplistValidator.cs`

- [ ] **Step 1: Write validator**

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using GlDrive.Config;
using GlDrive.Spread;

namespace GlDrive.AiAgent;

public sealed class SkiplistValidator : IChangeValidator
{
    public string Category => AgentCategories.Skiplist;

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        if (!TryMatchServer(change.Target, "/spread/skiplistRules", out var server, out var trailing))
            return new(false, "target-shape-unsupported", null);

        if (change.After is null && change.Before is null)
            return new(false, "empty-change", null);

        SkiplistRule? newRule = null;
        if (change.After is not null)
        {
            try { newRule = JsonSerializer.Deserialize<SkiplistRule>(JsonSerializer.Serialize(change.After)); }
            catch { return new(false, "after-parse-failed", null); }
            if (newRule is null) return new(false, "after-null", null);
            if (!PatternCompiles(newRule.Pattern, newRule.IsRegex)) return new(false, "pattern-bad", null);
            if (newRule.Action == SkiplistAction.Allow && change.Confidence < 0.8)
                return new(false, "allow-needs-higher-confidence", null);
        }

        if (trailing == "-") // append
        {
            return new(true, null, cfg =>
            {
                var s = server(cfg);
                if (s is null) return;
                s.Spread.SkiplistRules.Add(newRule!);
            });
        }

        if (int.TryParse(trailing, out var idx))
        {
            return new(true, null, cfg =>
            {
                var s = server(cfg);
                if (s is null || idx < 0 || idx >= s.Spread.SkiplistRules.Count) return;
                if (change.After is null) s.Spread.SkiplistRules.RemoveAt(idx);
                else s.Spread.SkiplistRules[idx] = newRule!;
            });
        }
        return new(false, "target-shape-unsupported", null);
    }

    private static bool PatternCompiles(string p, bool isRegex)
    {
        if (string.IsNullOrWhiteSpace(p)) return false;
        if (!isRegex) return true; // glob — accept
        try { new Regex(p); return true; } catch { return false; }
    }

    internal static bool TryMatchServer(string pointer, string expectedSuffix,
        out Func<AppConfig, ServerConfig?> serverResolver, out string trailing)
    {
        trailing = "";
        serverResolver = _ => null;
        // Expected: "/servers/{id}/spread/skiplistRules" + "/-" or "/N" or ""
        if (!pointer.StartsWith("/servers/")) return false;
        var rest = pointer["/servers/".Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return false;
        var serverId = rest[..slash];
        var afterId = rest[slash..]; // e.g., "/spread/skiplistRules/-"
        if (!afterId.StartsWith(expectedSuffix)) return false;
        trailing = afterId[expectedSuffix.Length..].TrimStart('/');
        serverResolver = cfg => cfg.Servers.FirstOrDefault(s => s.Id == serverId);
        return true;
    }
}
```

(If `ServerConfig.Spread.SkiplistRules` / `SkiplistRule` / `SkiplistAction` types are under different namespaces, adjust the `using`s.)

- [ ] **Step 2: Build + commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/Validators/SkiplistValidator.cs
git commit -m "feat(agent): SkiplistValidator — add/update/remove per-site deny rules"
```

---

### Task 7.3: `PriorityValidator`

**Files:**
- Create: `src/GlDrive/AiAgent/Validators/PriorityValidator.cs`

- [ ] **Step 1**

```csharp
using GlDrive.Config;

namespace GlDrive.AiAgent;

public sealed class PriorityValidator : IChangeValidator
{
    public string Category => AgentCategories.Priority;

    private static readonly string[] TierOrder = { "VeryLow", "Low", "Normal", "High", "VeryHigh" };

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        // target: "/servers/{id}/spread/sitePriority"
        if (!SkiplistValidator.TryMatchServer(change.Target, "/spread/sitePriority", out var resolver, out var trailing))
            return new(false, "target-shape-unsupported", null);
        if (!string.IsNullOrEmpty(trailing)) return new(false, "target-shape-unsupported", null);

        var afterStr = change.After?.ToString() ?? "";
        if (!TierOrder.Contains(afterStr)) return new(false, "bad-tier-value", null);
        if (afterStr == "VeryHigh") return new(false, "veryhigh-is-manual-only", null);

        return new(true, null, cfg =>
        {
            var s = resolver(cfg); if (s is null) return;
            var beforeIdx = Array.IndexOf(TierOrder, s.Spread.SitePriority.ToString());
            var afterIdx = Array.IndexOf(TierOrder, afterStr);
            if (Math.Abs(beforeIdx - afterIdx) > 1) return;  // ±1 only
            if (Enum.TryParse<SitePriority>(afterStr, out var parsed))
                s.Spread.SitePriority = parsed;
        });
    }
}
```

- [ ] **Step 2: Build + commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/Validators/PriorityValidator.cs
git commit -m "feat(agent): PriorityValidator — ±1 tier, never auto-promote to VeryHigh"
```

---

### Task 7.4: `SectionMappingValidator`

**Files:**
- Create: `src/GlDrive/AiAgent/Validators/SectionMappingValidator.cs`

- [ ] **Step 1**

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using GlDrive.Config;
using GlDrive.Spread;

namespace GlDrive.AiAgent;

public sealed class SectionMappingValidator : IChangeValidator
{
    public string Category => AgentCategories.SectionMapping;

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        if (!SkiplistValidator.TryMatchServer(change.Target, "/spread/sectionMappings", out var resolver, out var trailing))
            return new(false, "target-shape-unsupported", null);

        var after = JsonSerializer.Deserialize<SectionMapping>(JsonSerializer.Serialize(change.After ?? new()));
        if (after is null) return new(false, "after-parse-failed", null);
        try { _ = new Regex(after.TriggerRegex ?? ""); } catch { return new(false, "trigger-bad-regex", null); }

        if (trailing == "-")
            return new(true, null, cfg => { var s = resolver(cfg); s?.Spread.SectionMappings.Add(after); });

        if (int.TryParse(trailing, out var idx))
            return new(true, null, cfg =>
            {
                var s = resolver(cfg); if (s is null) return;
                if (idx < 0 || idx >= s.Spread.SectionMappings.Count) return;
                var cur = s.Spread.SectionMappings[idx];
                var isDefault = string.IsNullOrEmpty(cur.TriggerRegex) || cur.TriggerRegex == ".*";
                if (!isDefault) return; // preserve user edits (v1.44.76+ rule)
                cur.TriggerRegex = after.TriggerRegex;
                cur.IrcSection = string.IsNullOrEmpty(after.IrcSection) ? cur.IrcSection : after.IrcSection;
                cur.RemoteSection = string.IsNullOrEmpty(after.RemoteSection) ? cur.RemoteSection : after.RemoteSection;
            });

        return new(false, "target-shape-unsupported", null);
    }
}
```

- [ ] **Step 2: Build + commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/Validators/SectionMappingValidator.cs
git commit -m "feat(agent): SectionMappingValidator — preserves user-edited triggers"
```

---

### Task 7.5: `AnnounceRuleValidator`

**Files:**
- Create: `src/GlDrive/AiAgent/Validators/AnnounceRuleValidator.cs`

- [ ] **Step 1**

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using GlDrive.Config;

namespace GlDrive.AiAgent;

public sealed class AnnounceRuleValidator : IChangeValidator
{
    public string Category => AgentCategories.AnnounceRule;

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        if (!SkiplistValidator.TryMatchServer(change.Target, "/irc/announceRules", out var resolver, out var trailing))
            return new(false, "target-shape-unsupported", null);

        var after = JsonSerializer.Deserialize<IrcAnnounceRule>(JsonSerializer.Serialize(change.After ?? new()));
        if (after is null || string.IsNullOrWhiteSpace(after.Pattern)) return new(false, "after-null-or-empty", null);
        try { _ = new Regex(after.Pattern); } catch { return new(false, "pattern-bad-regex", null); }

        if (trailing == "-")
            return new(true, null, cfg => { var s = resolver(cfg); s?.Irc.AnnounceRules.Add(after); });

        if (int.TryParse(trailing, out var idx))
            return new(true, null, cfg =>
            {
                var s = resolver(cfg); if (s is null) return;
                if (idx < 0 || idx >= s.Irc.AnnounceRules.Count) return;
                s.Irc.AnnounceRules[idx].Pattern = after.Pattern;
                if (!string.IsNullOrEmpty(after.Channel)) s.Irc.AnnounceRules[idx].Channel = after.Channel;
            });

        return new(false, "target-shape-unsupported", null);
    }
}
```

- [ ] **Step 2: Build + commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/Validators/AnnounceRuleValidator.cs
git commit -m "feat(agent): AnnounceRuleValidator — add or patch IRC announce regex"
```

---

### Task 7.6: `ExcludedCategoriesValidator`

**Files:**
- Create: `src/GlDrive/AiAgent/Validators/ExcludedCategoriesValidator.cs`

- [ ] **Step 1**

```csharp
using GlDrive.Config;

namespace GlDrive.AiAgent;

public sealed class ExcludedCategoriesValidator : IChangeValidator
{
    public string Category => AgentCategories.ExcludedCategories;

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        if (!SkiplistValidator.TryMatchServer(change.Target, "/notifications/excludedCategories", out var resolver, out var trailing))
            return new(false, "target-shape-unsupported", null);
        if (trailing != "-") return new(false, "must-append", null);
        var key = change.After?.ToString();
        if (string.IsNullOrWhiteSpace(key)) return new(false, "after-empty", null);

        return new(true, null, cfg =>
        {
            var s = resolver(cfg); if (s is null) return;
            s.Notifications.ExcludedCategories ??= new List<string>();
            if (!s.Notifications.ExcludedCategories.Contains(key!, StringComparer.OrdinalIgnoreCase))
                s.Notifications.ExcludedCategories.Add(key!);
        });
    }
}
```

- [ ] **Step 2: Build + commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/Validators/ExcludedCategoriesValidator.cs
git commit -m "feat(agent): ExcludedCategoriesValidator — add section key (dedup-ordinal-ci)"
```

---

### Task 7.7: `WishlistPruneValidator`

**Files:**
- Create: `src/GlDrive/AiAgent/Validators/WishlistPruneValidator.cs`

- [ ] **Step 1**

```csharp
using System.Text.Json;
using GlDrive.Config;

namespace GlDrive.AiAgent;

public sealed class WishlistPruneValidator : IChangeValidator
{
    public string Category => AgentCategories.WishlistPrune;

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        // target: /wishlist/items/{id}
        if (!change.Target.StartsWith("/wishlist/items/")) return new(false, "target-shape-unsupported", null);
        var id = change.Target["/wishlist/items/".Length..];
        if (string.IsNullOrWhiteSpace(id)) return new(false, "missing-id", null);

        var afterDict = change.After is null ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(change.After));

        bool hardRemove = afterDict is null;
        bool softMark = afterDict != null && afterDict.TryGetValue("dead", out var deadEl) && deadEl.GetBoolean();
        if (!hardRemove && !softMark) return new(false, "action-unclear", null);

        return new(true, null, cfg =>
        {
            var store = GlDrive.Downloads.WishlistStore.Load();
            var item = store.Items.FirstOrDefault(x => x.Id == id);
            if (item is null) return;
            if (hardRemove) store.Items.Remove(item);
            else item.Dead = true;  // ensure WishlistItem has a `Dead` bool; add if missing
            store.Save();
        });
    }
}
```

If `WishlistItem` doesn't have a `Dead` bool, add it (`public bool Dead { get; set; }` default false) as part of this task. `WishlistStore.Load()/Save()` must be static helpers; adjust if the real API differs.

- [ ] **Step 2: Build + commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/Validators/WishlistPruneValidator.cs src/GlDrive/Downloads/WishlistStore.cs
git commit -m "feat(agent): WishlistPruneValidator — soft-mark dead or hard-remove"
```

---

### Task 7.8: `PoolSizingValidator`

**Files:**
- Create: `src/GlDrive/AiAgent/Validators/PoolSizingValidator.cs`

- [ ] **Step 1**

```csharp
using System.Text.Json;
using GlDrive.Config;

namespace GlDrive.AiAgent;

public sealed class PoolSizingValidator : IChangeValidator
{
    public string Category => AgentCategories.PoolSizing;

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        if (change.After is null) return new(false, "after-null", null);
        if (!int.TryParse(change.After.ToString(), out var after)) return new(false, "after-not-int", null);
        after = Math.Clamp(after, 2, 32);

        // Global: /spread/maxConcurrentRaces
        if (change.Target == "/spread/maxConcurrentRaces")
        {
            var before = config.Spread?.MaxConcurrentRaces ?? 1;
            if (!WithinPct(before, after, 0.25)) return new(false, "change-too-large", null);
            return new(true, null, cfg => { if (cfg.Spread != null) cfg.Spread.MaxConcurrentRaces = after; });
        }

        // Per-server: /servers/{id}/spread/spreadPoolSize  or /spread/maxSlots
        foreach (var field in new[] { "/spread/spreadPoolSize", "/spread/maxSlots" })
        {
            if (!SkiplistValidator.TryMatchServer(change.Target, field, out var resolver, out var trailing)) continue;
            if (!string.IsNullOrEmpty(trailing)) continue;
            return new(true, null, cfg =>
            {
                var s = resolver(cfg); if (s is null) return;
                if (field == "/spread/spreadPoolSize")
                {
                    var before = s.Spread.SpreadPoolSize; if (!WithinPct(before, after, 0.25)) return;
                    s.Spread.SpreadPoolSize = after;
                }
                else
                {
                    var before = s.Spread.MaxSlots; if (!WithinPct(before, after, 0.25)) return;
                    s.Spread.MaxSlots = after;
                }
            });
        }
        return new(false, "target-shape-unsupported", null);
    }

    private static bool WithinPct(int before, int after, double pct)
    {
        if (before <= 0) return after > 0 && after <= 32;
        var delta = Math.Abs(after - before); return delta <= Math.Ceiling(before * pct);
    }
}
```

- [ ] **Step 2: Build + commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/Validators/PoolSizingValidator.cs
git commit -m "feat(agent): PoolSizingValidator — ±25%, clamp [2,32] for pool/slots/concurrency"
```

---

### Task 7.9: `BlacklistValidator`

**Files:**
- Create: `src/GlDrive/AiAgent/Validators/BlacklistValidator.cs`

- [ ] **Step 1**

```csharp
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Spread;

namespace GlDrive.AiAgent;

public sealed class BlacklistValidator : IChangeValidator
{
    public string Category => AgentCategories.Blacklist;

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        if (!SkiplistValidator.TryMatchServer(change.Target, "/spread/blacklist", out var resolver, out var trailing))
            return new(false, "target-shape-unsupported", null);

        SectionBlacklistEntry? after = change.After is null ? null
            : JsonSerializer.Deserialize<SectionBlacklistEntry>(JsonSerializer.Serialize(change.After));

        if (trailing == "-")
        {
            if (after is null || string.IsNullOrWhiteSpace(after.Section)) return new(false, "after-empty", null);
            return new(true, null, cfg => { var s = resolver(cfg); s?.Spread.Blacklist.Add(after); });
        }

        if (int.TryParse(trailing, out var idx))
            return new(true, null, cfg =>
            {
                var s = resolver(cfg); if (s is null) return;
                if (idx < 0 || idx >= s.Spread.Blacklist.Count) return;
                if (after is null) s.Spread.Blacklist.RemoveAt(idx);
                else s.Spread.Blacklist[idx] = after;
            });

        return new(false, "target-shape-unsupported", null);
    }
}
```

(If `SectionBlacklistEntry` lives under a different namespace, adjust.)

- [ ] **Step 2: Build + commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/Validators/BlacklistValidator.cs
git commit -m "feat(agent): BlacklistValidator — add/remove (site, section) blacklist entries"
```

---

### Task 7.10: `AffilsValidator`

**Files:**
- Create: `src/GlDrive/AiAgent/Validators/AffilsValidator.cs`

- [ ] **Step 1**

```csharp
using GlDrive.Config;

namespace GlDrive.AiAgent;

public sealed class AffilsValidator : IChangeValidator
{
    public string Category => AgentCategories.Affils;

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        if (!SkiplistValidator.TryMatchServer(change.Target, "/spread/affils", out var resolver, out var trailing))
            return new(false, "target-shape-unsupported", null);
        if (trailing != "-") return new(false, "must-append", null);
        var group = change.After?.ToString();
        if (string.IsNullOrWhiteSpace(group)) return new(false, "after-empty", null);

        return new(true, null, cfg =>
        {
            var s = resolver(cfg); if (s is null) return;
            s.Spread.Affils ??= new List<string>();
            if (!s.Spread.Affils.Contains(group!, StringComparer.OrdinalIgnoreCase))
                s.Spread.Affils.Add(group!);
        });
    }
}
```

- [ ] **Step 2: Build + commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/Validators/AffilsValidator.cs
git commit -m "feat(agent): AffilsValidator — append-only, ordinal-ci dedup"
```

---

### Task 7.11: `ErrorReportValidator` (informational)

**Files:**
- Create: `src/GlDrive/AiAgent/Validators/ErrorReportValidator.cs`

- [ ] **Step 1**

```csharp
using System.IO;
using GlDrive.Config;

namespace GlDrive.AiAgent;

public sealed class ErrorReportValidator : IChangeValidator
{
    public string Category => AgentCategories.ErrorReport;

    private readonly string _briefsIssuesDir;

    public ErrorReportValidator(string aiDataRoot)
    {
        _briefsIssuesDir = Path.Combine(aiDataRoot, "ai-briefs", "issues");
        Directory.CreateDirectory(_briefsIssuesDir);
    }

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        // target: "issues/<sig>"  (informational — config untouched)
        if (!change.Target.StartsWith("issues/")) return new(false, "target-shape-unsupported", null);
        var sig = change.Target["issues/".Length..];
        if (string.IsNullOrWhiteSpace(sig)) return new(false, "missing-sig", null);
        if (change.After is null) return new(false, "after-null", null);

        return new(true, null, _ =>
        {
            var path = Path.Combine(_briefsIssuesDir, $"{DateTime.Now:yyyyMMdd}-{Sanitize(sig)}.md");
            File.WriteAllText(path, change.After.ToString() ?? "");
        });
    }

    private static string Sanitize(string s) => string.Concat(s.Select(c =>
        char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
}
```

- [ ] **Step 2: Build + commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/Validators/ErrorReportValidator.cs
git commit -m "feat(agent): ErrorReportValidator — writes Markdown issue files (no config mutation)"
```

---

**Phase 7 complete.** All ten validators in place, ChangeApplier dispatches + gates.

---

## Phase 8 — AuditTrail, SnapshotStore, AgentMemo, AgentRunner

Ships: persistence for audit rows + config snapshots + memo, plus the scheduler wiring it all together.

### Task 8.1: `AuditTrail` full implementation

**Files:**
- Modify: `src/GlDrive/AiAgent/AuditTrail.cs` (replaces the Task 7.1 stub)

- [ ] **Step 1: Replace stub with full implementation**

```csharp
using System.IO;
using System.Text;
using System.Text.Json;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class AuditTrail
{
    private readonly string _path;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public AuditTrail(string aiDataRoot)
    {
        Directory.CreateDirectory(aiDataRoot);
        _path = Path.Combine(aiDataRoot, "ai-audit.jsonl");
    }

    public void Append(AuditRow row)
    {
        try
        {
            lock (_lock)
                File.AppendAllText(_path,
                    JsonSerializer.Serialize(row, JsonOpts) + "\n", Encoding.UTF8);
        }
        catch (Exception ex) { Log.Warning(ex, "AuditTrail append failed"); }
    }

    public IEnumerable<AuditRow> ReadAll()
    {
        if (!File.Exists(_path)) yield break;
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            AuditRow? row = null;
            try { row = JsonSerializer.Deserialize<AuditRow>(line, JsonOpts); }
            catch { continue; }
            if (row != null) yield return row;
        }
    }

    public void MarkUndone(string runId, string target, string reason)
    {
        lock (_lock)
        {
            var rows = ReadAll().ToList();
            var updated = false;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].RunId == runId && rows[i].Target == target && rows[i].Applied && !rows[i].Undone)
                {
                    rows[i].Undone = true;
                    rows[i].UndoneAt = DateTime.UtcNow.ToString("O");
                    rows[i].UndoneReason = reason;
                    updated = true;
                }
            }
            if (!updated) return;
            var sb = new StringBuilder();
            foreach (var r in rows) sb.AppendLine(JsonSerializer.Serialize(r, JsonOpts));
            File.WriteAllText(_path, sb.ToString(), Encoding.UTF8);
        }
    }
}
```

Remove the stub `AuditTrail` class but keep the `AuditRow` class (shared).

- [ ] **Step 2: Build + commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/AuditTrail.cs
git commit -m "feat(agent): AuditTrail append + read + MarkUndone rewrite-in-place"
```

---

### Task 8.2: `SnapshotStore`

**Files:**
- Create: `src/GlDrive/AiAgent/SnapshotStore.cs`

- [ ] **Step 1**

```csharp
using System.IO;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class SnapshotStore
{
    private readonly string _dir;
    private readonly int _retain;

    public SnapshotStore(string aiDataRoot, int retentionCount)
    {
        _dir = Path.Combine(aiDataRoot, "ai-snapshots");
        _retain = Math.Max(1, retentionCount);
        Directory.CreateDirectory(_dir);
    }

    public string Save(string appsettingsPath, string runId)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var shortId = runId.Length > 8 ? runId[..8] : runId;
        var dest = Path.Combine(_dir, $"{stamp}-{shortId}.json");
        File.Copy(appsettingsPath, dest, overwrite: true);
        Prune();
        return dest;
    }

    public IEnumerable<string> List() => Directory.Exists(_dir)
        ? Directory.GetFiles(_dir, "*.json").OrderByDescending(f => f)
        : Enumerable.Empty<string>();

    public void Restore(string snapshotPath, string appsettingsPath)
    {
        // Safety: snapshot current first
        var preRestore = Path.Combine(_dir, $"pre-restore-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.Copy(appsettingsPath, preRestore, overwrite: true);
        File.Copy(snapshotPath, appsettingsPath, overwrite: true);
    }

    private void Prune()
    {
        try
        {
            var files = Directory.GetFiles(_dir, "*.json").OrderByDescending(f => f).ToList();
            foreach (var old in files.Skip(_retain))
            {
                try { File.Delete(old); } catch (Exception ex) { Log.Debug(ex, "snapshot prune"); }
            }
        }
        catch (Exception ex) { Log.Warning(ex, "SnapshotStore prune failed"); }
    }
}
```

- [ ] **Step 2: Build + commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/SnapshotStore.cs
git commit -m "feat(agent): SnapshotStore saves pre-run config, prunes, supports restore"
```

---

### Task 8.3: `AgentMemo`

**Files:**
- Create: `src/GlDrive/AiAgent/AgentMemo.cs`

- [ ] **Step 1**

```csharp
using System.IO;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class AgentMemo
{
    private readonly string _path;
    public AgentMemo(string aiDataRoot) { _path = Path.Combine(aiDataRoot, "agent-memo.md"); }

    public string Load()
    {
        try { return File.Exists(_path) ? File.ReadAllText(_path) : ""; }
        catch (Exception ex) { Log.Warning(ex, "AgentMemo load failed"); return ""; }
    }

    public void Save(string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(_path, content ?? "");
        }
        catch (Exception ex) { Log.Warning(ex, "AgentMemo save failed"); }
    }
}
```

- [ ] **Step 2: Build + commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/AgentMemo.cs
git commit -m "feat(agent): AgentMemo load/save for persistent carry-forward beliefs"
```

---

### Task 8.4: `AgentRunner` — scheduler + manual trigger

**Files:**
- Create: `src/GlDrive/AiAgent/AgentRunner.cs`
- Modify: `src/GlDrive/App.xaml.cs`

- [ ] **Step 1: Write runner**

```csharp
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlDrive.Config;
using Microsoft.Win32;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class AgentRunner : IDisposable
{
    private readonly TelemetryRecorder _telemetry;
    private readonly LogDigester _digester;
    private readonly AgentMemo _memo;
    private readonly FreezeStore _freeze;
    private readonly ChangeApplier _applier;
    private readonly AuditTrail _audit;
    private readonly SnapshotStore _snapshots;
    private readonly ConfigManager _configManager;
    private readonly Func<AppConfig> _getConfig;
    private readonly string _aiDataRoot;
    private readonly string _briefsDir;

    private readonly SemaphoreSlim _runGate = new(1, 1);
    private Timer? _timer;
    private DateTime _lastRunUtc = DateTime.MinValue;
    private CancellationTokenSource? _activeRunCts;

    public AgentRunner(TelemetryRecorder telemetry, LogDigester digester, AgentMemo memo,
                       FreezeStore freeze, ChangeApplier applier, AuditTrail audit,
                       SnapshotStore snapshots, ConfigManager configManager,
                       Func<AppConfig> getConfig, string aiDataRoot)
    {
        _telemetry = telemetry; _digester = digester; _memo = memo; _freeze = freeze;
        _applier = applier; _audit = audit; _snapshots = snapshots; _configManager = configManager;
        _getConfig = getConfig; _aiDataRoot = aiDataRoot;
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
        _timer?.Dispose(); _timer = null;
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
        if ((DateTime.UtcNow - _lastRunUtc).TotalHours >= 23 && _lastRunUtc != DateTime.MinValue)
        {
            _timer = new Timer(_ => _ = RunOnceAsync(), null, TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
            return;
        }

        var nextRun = new DateTime(now.Year, now.Month, now.Day, cfg.RunHourLocal, 0, 0, DateTimeKind.Local);
        if (nextRun <= now) nextRun = nextRun.AddDays(1);
        var delay = nextRun - now;
        _timer = new Timer(_ => _ = RunOnceAsync(), null, delay, Timeout.InfiniteTimeSpan);
        Log.Information("AgentRunner scheduled next run in {Delay}", delay);
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

        try
        {
            var cfg = _getConfig();
            if (!cfg.Agent.Enabled && !manualTrigger) { status = "disabled"; return; }

            var appsettingsPath = _configManager.Path;
            var snapshotPath = _snapshots.Save(appsettingsPath, runId);

            var digest = _digester.Build(cfg.Agent.WindowDays);
            var memoText = _memo.Load();
            var frozenPaths = _freeze.All.Select(e => e.Path).ToList();

            var configNode = JsonNode.Parse(File.ReadAllText(appsettingsPath))!;
            var redacted = AgentPrompt.RedactFrozen(configNode, frozenPaths);

            var lastSummaries = _audit.ReadAll().Reverse()
                .GroupBy(r => r.RunId)
                .Take(3)
                .Select(g => $"run {g.Key[..8]}: applied={g.Count(r => r.Applied)} rejected={g.Count(r => !r.Applied)}")
                .ToList();

            var composer = new AgentPrompt();
            var userPrompt = composer.Compose(digest, memoText, frozenPaths, redacted, lastSummaries);
            var apiKey = cfg.ResolveOpenRouterKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                status = "no-api-key";
                File.WriteAllText(briefPath, "# Agent run skipped\n\nNo OpenRouter API key configured.\n");
                return;
            }

            using var client = new AgentClient(apiKey, cfg.ResolveAgentModel());
            var outcome = await client.RunAsync(AgentPrompt.SystemPrompt, userPrompt, ct);
            if (outcome.Result is null)
            {
                status = outcome.ErrorMessage ?? "model-failure";
                File.WriteAllText(briefPath, $"# Agent run failed\n\nReason: {status}\n");
                return;
            }

            bool dryRun = cfg.Agent.DryRunsRemaining > 0;
            var applyReport = _applier.Apply(outcome.Result.Changes, cfg, cfg.Agent, runId, dryRun);
            var suggestionReport = _applier.Apply(outcome.Result.Suggestions, cfg, cfg.Agent, runId, dryRun: true);

            if (!dryRun) _configManager.Save(cfg);

            _memo.Save(outcome.Result.MemoUpdate);
            if (cfg.Agent.DryRunsRemaining > 0)
            {
                cfg.Agent.DryRunsRemaining -= 1;
                _configManager.Save(cfg);
            }

            var footer = $"\n\n---\n_Tokens: {outcome.InputTokens} in / {outcome.OutputTokens} out — est. ${outcome.EstimatedCostUsd:F3}_\n" +
                         $"_Applied: {applyReport.Applied} / Rejected: {applyReport.Rejected} ({(dryRun ? "DRY RUN" : "live")})_\n";
            File.WriteAllText(briefPath, (outcome.Result.BriefMarkdown ?? "# (no brief)") + footer);
            _lastRunUtc = DateTime.UtcNow;
            SaveLastRun();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AgentRunner partial-failure");
            status = "partial-failure";
            File.WriteAllText(briefPath, $"# Agent partial failure\n\n```\n{ex}\n```\n");
        }
        finally
        {
            _runGate.Release();
            _activeRunCts?.Dispose(); _activeRunCts = null;
            ScheduleNext();
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
                if (DateTime.TryParse(node?["utc"]?.ToString(), out var t)) _lastRunUtc = t;
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
```

`ConfigManager.Path` must be accessible — add a property if missing. Same for `ConfigManager.Save(AppConfig)` — adapt to actual method shape.

- [ ] **Step 2: Register in App startup**

In `App.xaml.cs` after all other singletons:
```csharp
var validators = new List<GlDrive.AiAgent.IChangeValidator>
{
    new GlDrive.AiAgent.SkiplistValidator(),
    new GlDrive.AiAgent.PriorityValidator(),
    new GlDrive.AiAgent.SectionMappingValidator(),
    new GlDrive.AiAgent.AnnounceRuleValidator(),
    new GlDrive.AiAgent.ExcludedCategoriesValidator(),
    new GlDrive.AiAgent.WishlistPruneValidator(),
    new GlDrive.AiAgent.PoolSizingValidator(),
    new GlDrive.AiAgent.BlacklistValidator(),
    new GlDrive.AiAgent.AffilsValidator(),
    new GlDrive.AiAgent.ErrorReportValidator(aiData)
};
AuditTrail = new GlDrive.AiAgent.AuditTrail(aiData);
SnapshotStore = new GlDrive.AiAgent.SnapshotStore(aiData, _config.Agent.SnapshotRetentionCount);
AgentMemo = new GlDrive.AiAgent.AgentMemo(aiData);
ChangeApplier = new GlDrive.AiAgent.ChangeApplier(validators, FreezeStore!, AuditTrail);
LogDigester = new GlDrive.AiAgent.LogDigester(aiData);
AgentRunner = new GlDrive.AiAgent.AgentRunner(
    TelemetryRecorder, LogDigester, AgentMemo, FreezeStore!, ChangeApplier,
    AuditTrail, SnapshotStore, _configManager, () => _config, aiData);
AgentRunner.Start();
```
Add matching `public static ... { get; private set; }` for each. Dispose AgentRunner on exit.

- [ ] **Step 3: Build + runtime check**

`dotnet build` green. Enable the agent in Settings (with a valid OpenRouter API key stored in Credential Manager), set `RunHourLocal` to the next possible hour, verify a run fires and a brief file lands in `ai-briefs/`. Alternatively, trigger "Run now" via a debug command.

- [ ] **Step 4: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/AiAgent/AgentRunner.cs src/GlDrive/App.xaml.cs
git commit -m "feat(agent): AgentRunner schedule + catch-up + power/time change handling"
```

---

**Phase 8 complete.** End-to-end run works. UI not yet wired up.

---

## Phase 9 — Dashboard "AI Agent" tab

Ships: the user-facing surface with five sub-tabs.

### Task 9.1: `AgentViewModel` + tab scaffold

**Files:**
- Create: `src/GlDrive/UI/AgentViewModel.cs`
- Create: `src/GlDrive/UI/AgentView.xaml`
- Create: `src/GlDrive/UI/AgentView.xaml.cs`
- Modify: `src/GlDrive/UI/DashboardWindow.xaml`

- [ ] **Step 1: Create `AgentViewModel`**

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GlDrive.AiAgent;

namespace GlDrive.UI;

public sealed class AgentViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));

    private string _briefMarkdown = "";
    public string BriefMarkdown { get => _briefMarkdown; set { _briefMarkdown = value; Raise(); } }

    public ObservableCollection<AuditRow> AuditRows { get; } = new();
    public ObservableCollection<AuditRow> Suggestions { get; } = new();
    public ObservableCollection<FreezeEntry> Frozen { get; } = new();

    private string _memo = "";
    public string Memo { get => _memo; set { _memo = value; Raise(); } }

    public ICommand RunNowCommand { get; }
    public ICommand UndoAuditRowCommand { get; }
    public ICommand ApplyAnywayCommand { get; }
    public ICommand DismissSuggestionCommand { get; }
    public ICommand UnfreezeCommand { get; }
    public ICommand SaveMemoCommand { get; }

    public AgentViewModel()
    {
        RunNowCommand = new RelayCommand(async () =>
        {
            if (App.AgentRunner != null) await App.AgentRunner.RunNowAsync();
            Refresh();
        });
        UndoAuditRowCommand = new RelayCommand<AuditRow>(UndoRow);
        ApplyAnywayCommand = new RelayCommand<AuditRow>(ApplyAnyway);
        DismissSuggestionCommand = new RelayCommand<AuditRow>(Dismiss);
        UnfreezeCommand = new RelayCommand<FreezeEntry>(e =>
        {
            if (e != null) App.FreezeStore?.Unfreeze(e.Path);
            RefreshFrozen();
        });
        SaveMemoCommand = new RelayCommand(() => App.AgentMemo?.Save(Memo));
        Refresh();
    }

    public void Refresh()
    {
        RefreshBrief(); RefreshAudit(); RefreshSuggestions(); RefreshFrozen(); RefreshMemo();
    }

    private void RefreshBrief()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "GlDrive", "ai-data", "ai-briefs");
            var latest = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.md").OrderByDescending(f => f).FirstOrDefault()
                : null;
            BriefMarkdown = latest != null ? File.ReadAllText(latest) : "_No brief yet._";
        }
        catch (Exception ex) { BriefMarkdown = $"Error loading brief: {ex.Message}"; }
    }

    private void RefreshAudit()
    {
        AuditRows.Clear();
        if (App.AuditTrail is null) return;
        foreach (var r in App.AuditTrail.ReadAll().Where(r => r.Applied).Reverse().Take(500))
            AuditRows.Add(r);
    }

    private void RefreshSuggestions()
    {
        Suggestions.Clear();
        if (App.AuditTrail is null) return;
        foreach (var r in App.AuditTrail.ReadAll()
                .Where(r => !r.Applied && r.RejectionReason != "frozen" && !r.Undone)
                .Reverse().Take(500))
            Suggestions.Add(r);
    }

    private void RefreshFrozen()
    {
        Frozen.Clear();
        if (App.FreezeStore is null) return;
        foreach (var e in App.FreezeStore.All) Frozen.Add(e);
    }

    private void RefreshMemo()
    {
        if (App.AgentMemo is null) return;
        Memo = App.AgentMemo.Load();
    }

    private void UndoRow(AuditRow? row) { /* filled in Task 9.7 */ }
    private void ApplyAnyway(AuditRow? row) { /* filled in Task 9.5 */ }
    private void Dismiss(AuditRow? row) { /* filled in Task 9.5 */ }
}
```

- [ ] **Step 2: Create `AgentView.xaml` scaffold with five sub-tabs**

```xml
<UserControl x:Class="GlDrive.UI.AgentView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <TabControl>
    <TabItem Header="Today's Brief">
      <Grid>
        <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
        <StackPanel Orientation="Horizontal" Margin="6">
          <Button Content="Run now" Command="{Binding RunNowCommand}" Padding="10,4"/>
        </StackPanel>
        <ScrollViewer Grid.Row="1" Margin="6">
          <TextBox Text="{Binding BriefMarkdown, Mode=OneWay}" IsReadOnly="True"
                   TextWrapping="Wrap" FontFamily="Consolas" Background="Transparent"
                   BorderThickness="0" AcceptsReturn="True"/>
        </ScrollViewer>
      </Grid>
    </TabItem>

    <TabItem Header="Audit Trail">
      <DataGrid ItemsSource="{Binding AuditRows}" AutoGenerateColumns="False" IsReadOnly="True">
        <DataGrid.Columns>
          <DataGridTextColumn Header="Time" Binding="{Binding Ts}" Width="140"/>
          <DataGridTextColumn Header="Run" Binding="{Binding RunId}" Width="80"/>
          <DataGridTextColumn Header="Category" Binding="{Binding Category}" Width="120"/>
          <DataGridTextColumn Header="Target" Binding="{Binding Target}" Width="300"/>
          <DataGridTextColumn Header="Confidence" Binding="{Binding Confidence}" Width="80"/>
          <DataGridTemplateColumn Header="Undo" Width="70">
            <DataGridTemplateColumn.CellTemplate>
              <DataTemplate>
                <Button Content="Undo"
                        Command="{Binding DataContext.UndoAuditRowCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                        CommandParameter="{Binding}"/>
              </DataTemplate>
            </DataGridTemplateColumn.CellTemplate>
          </DataGridTemplateColumn>
        </DataGrid.Columns>
      </DataGrid>
    </TabItem>

    <TabItem Header="Suggestions">
      <DataGrid ItemsSource="{Binding Suggestions}" AutoGenerateColumns="False" IsReadOnly="True">
        <DataGrid.Columns>
          <DataGridTextColumn Header="Time" Binding="{Binding Ts}" Width="140"/>
          <DataGridTextColumn Header="Category" Binding="{Binding Category}" Width="120"/>
          <DataGridTextColumn Header="Target" Binding="{Binding Target}" Width="280"/>
          <DataGridTextColumn Header="Reason" Binding="{Binding RejectionReason}" Width="180"/>
          <DataGridTextColumn Header="Confidence" Binding="{Binding Confidence}" Width="80"/>
          <DataGridTemplateColumn Header="Action" Width="180">
            <DataGridTemplateColumn.CellTemplate>
              <DataTemplate>
                <StackPanel Orientation="Horizontal">
                  <Button Content="Apply anyway" Margin="0,0,4,0"
                          Command="{Binding DataContext.ApplyAnywayCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                          CommandParameter="{Binding}"/>
                  <Button Content="Dismiss"
                          Command="{Binding DataContext.DismissSuggestionCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                          CommandParameter="{Binding}"/>
                </StackPanel>
              </DataTemplate>
            </DataGridTemplateColumn.CellTemplate>
          </DataGridTemplateColumn>
        </DataGrid.Columns>
      </DataGrid>
    </TabItem>

    <TabItem Header="Frozen Fields">
      <DataGrid ItemsSource="{Binding Frozen}" AutoGenerateColumns="False" IsReadOnly="True">
        <DataGrid.Columns>
          <DataGridTextColumn Header="Path" Binding="{Binding Path}" Width="*"/>
          <DataGridTextColumn Header="Frozen At" Binding="{Binding FrozenAt}" Width="180"/>
          <DataGridTextColumn Header="Note" Binding="{Binding Note}" Width="200"/>
          <DataGridTemplateColumn Header="Unfreeze" Width="90">
            <DataGridTemplateColumn.CellTemplate>
              <DataTemplate>
                <Button Content="Unfreeze"
                        Command="{Binding DataContext.UnfreezeCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                        CommandParameter="{Binding}"/>
              </DataTemplate>
            </DataGridTemplateColumn.CellTemplate>
          </DataGridTemplateColumn>
        </DataGrid.Columns>
      </DataGrid>
    </TabItem>

    <TabItem Header="Memo">
      <Grid>
        <Grid.RowDefinitions><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
        <TextBox Text="{Binding Memo, UpdateSourceTrigger=PropertyChanged}" AcceptsReturn="True"
                 VerticalScrollBarVisibility="Auto" FontFamily="Consolas" Margin="6"/>
        <Button Grid.Row="1" Content="Save memo" Margin="6" HorizontalAlignment="Right"
                Command="{Binding SaveMemoCommand}"/>
      </Grid>
    </TabItem>
  </TabControl>
</UserControl>
```

Code-behind:
```csharp
using System.Windows.Controls;

namespace GlDrive.UI;
public partial class AgentView : UserControl
{
    public AgentView() { InitializeComponent(); DataContext = new AgentViewModel(); }
}
```

- [ ] **Step 3: Host in `DashboardWindow.xaml`**

In the existing TabControl, add:
```xml
<TabItem Header="AI Agent">
  <ui:AgentView xmlns:ui="clr-namespace:GlDrive.UI"/>
</TabItem>
```

- [ ] **Step 4: Build + runtime check**

`dotnet build` green. Launch → Dashboard → AI Agent tab appears with five sub-tabs. Today's Brief shows latest `.md` (or "No brief yet"). Audit Trail empty until a run. Frozen Fields lists entries from Task 5.4. Memo loads/saves.

- [ ] **Step 5: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/UI/AgentViewModel.cs src/GlDrive/UI/AgentView.xaml src/GlDrive/UI/AgentView.xaml.cs src/GlDrive/UI/DashboardWindow.xaml
git commit -m "feat(agent): AI Agent dashboard tab with five sub-tabs"
```

---

### Task 9.2: Undo / Apply-anyway / Dismiss commands

**Files:**
- Modify: `src/GlDrive/UI/AgentViewModel.cs`

- [ ] **Step 1: Implement Undo**

Replace `UndoRow` stub in AgentViewModel:
```csharp
private void UndoRow(AuditRow? row)
{
    if (row is null || !row.Applied || row.Undone) return;
    try
    {
        // Reverse mutation: build an AgentChange with before/after swapped, re-run through applier as non-dryrun
        var inverse = new AgentChange
        {
            Category = row.Category, Target = row.Target,
            Before = row.After, After = row.Before,
            Reasoning = "User-initiated undo", EvidenceRef = "user-undo",
            Confidence = 1.0
        };
        var cfg = App.CurrentConfig;
        var undoRunId = "undo-" + Guid.NewGuid().ToString()[..8];
        App.ChangeApplier?.Apply(new[] { inverse }, cfg, cfg.Agent, undoRunId, dryRun: false);
        App.ConfigManager?.Save(cfg);
        App.AuditTrail?.MarkUndone(row.RunId, row.Target, "user-click");
        RefreshAudit();

        App.TelemetryRecorder?.Record(GlDrive.AiAgent.TelemetryStream.Overrides,
            new GlDrive.AiAgent.ConfigOverrideEvent
            {
                JsonPointer = row.Target,
                BeforeValue = row.After?.ToString(),
                AfterValue = row.Before?.ToString(),
                AiAuditRef = row.RunId
            });
    }
    catch (Exception ex) { MessageBox.Show("Undo failed: " + ex.Message); }
}
```

- [ ] **Step 2: Implement Apply-anyway**

```csharp
private void ApplyAnyway(AuditRow? row)
{
    if (row is null || row.Applied) return;
    try
    {
        var change = new AgentChange
        {
            Category = row.Category, Target = row.Target,
            Before = row.Before, After = row.After,
            Reasoning = row.Reasoning + " [user apply-anyway]",
            EvidenceRef = row.EvidenceRef,
            Confidence = 1.0  // force through confidence gate
        };
        var cfg = App.CurrentConfig;
        App.ChangeApplier?.Apply(new[] { change }, cfg, cfg.Agent, "manual-" + Guid.NewGuid().ToString()[..8], dryRun: false);
        App.ConfigManager?.Save(cfg);
        RefreshAudit(); RefreshSuggestions();
    }
    catch (Exception ex) { MessageBox.Show("Apply-anyway failed: " + ex.Message); }
}
```

- [ ] **Step 3: Implement Dismiss**

```csharp
private void Dismiss(AuditRow? row)
{
    if (row is null) return;
    App.AuditTrail?.MarkUndone(row.RunId, row.Target, "user-dismiss");
    RefreshSuggestions();
}
```

Add required properties on `App`: `CurrentConfig` (returns `_config`), `ConfigManager`, `ChangeApplier`, `AuditTrail`, `AgentMemo`, `FreezeStore`, `AgentRunner`, `TelemetryRecorder` (already added).

- [ ] **Step 4: Build + runtime check**

`dotnet build` green. After a run has produced rows, verify Undo / Apply-anyway / Dismiss each behave correctly and both grids refresh.

- [ ] **Step 5: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/UI/AgentViewModel.cs src/GlDrive/App.xaml.cs
git commit -m "feat(agent): Undo/Apply-anyway/Dismiss commands wired into agent dashboard"
```

---

**Phase 9 complete.** User-facing dashboard is fully interactive.

---

## Phase 10 — Kill switch, panic revert, restore-from-snapshot

Ships: tray submenu, enable toggle wired to cancellation, revert-last-N modal, panic revert, restore UI.

### Task 10.1: Tray AI Agent submenu

**Files:**
- Modify: `src/GlDrive/UI/TrayIconSetup.cs`
- Modify: `src/GlDrive/UI/TrayViewModel.cs`

- [ ] **Step 1: Add tray submenu**

Locate where the tray context menu is built (likely `TrayIconSetup.BuildMenu` or similar). Add:
```csharp
var agentRoot = new MenuItem { Header = "AI Agent" };
var enabledItem = new MenuItem
{
    Header = "Enabled",
    IsCheckable = true,
    IsChecked = vm.AgentEnabled,
    Command = vm.ToggleAgentEnabledCommand
};
agentRoot.Items.Add(enabledItem);
agentRoot.Items.Add(new MenuItem { Header = "Run now", Command = vm.AgentRunNowCommand });
agentRoot.Items.Add(new MenuItem { Header = "Open dashboard…", Command = vm.OpenDashboardToAgentCommand });
agentRoot.Items.Add(new MenuItem { Header = "Pause 24h", Command = vm.PauseAgent24hCommand });
agentRoot.Items.Add(new Separator());
agentRoot.Items.Add(new MenuItem { Header = "Panic: revert last run", Command = vm.PanicRevertLastRunCommand });
contextMenu.Items.Add(agentRoot);
```

- [ ] **Step 2: Add TrayViewModel commands**

```csharp
public bool AgentEnabled
{
    get => _config.Agent.Enabled;
    set
    {
        _config.Agent.Enabled = value;
        _configManager.Save(_config);
        if (value) App.AgentRunner?.Start();
        else       App.AgentRunner?.Stop();
        OnPropertyChanged();
    }
}
public ICommand ToggleAgentEnabledCommand => new RelayCommand(() => AgentEnabled = !AgentEnabled);
public ICommand AgentRunNowCommand => new RelayCommand(async () =>
{
    if (App.AgentRunner != null) await App.AgentRunner.RunNowAsync();
});
public ICommand OpenDashboardToAgentCommand => new RelayCommand(() =>
{
    OpenDashboardCommand.Execute(null);
    // v1: just opens the dashboard; user clicks the AI Agent tab.
    // v2 (later): expose a `DashboardWindow.SelectTab("AI Agent")` method and call it here.
});
public ICommand PauseAgent24hCommand => new RelayCommand(() =>
{
    _config.Agent.Enabled = false;
    _configManager.Save(_config);
    App.AgentRunner?.Stop();
    // Schedule re-enable after 24h
    var t = new Timer(_ =>
    {
        _config.Agent.Enabled = true;
        _configManager.Save(_config);
        App.AgentRunner?.Start();
    }, null, TimeSpan.FromHours(24), Timeout.InfiniteTimeSpan);
});
public ICommand PanicRevertLastRunCommand => new RelayCommand(() =>
{
    var last = App.AuditTrail?.ReadAll().Where(r => r.Applied && !r.Undone).LastOrDefault()?.RunId;
    if (last is null) { MessageBox.Show("No recent applied changes to revert."); return; }
    if (MessageBox.Show($"Revert every change from run {last[..8]}?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
    PanicRevertRun(last);
});

private void PanicRevertRun(string runId)
{
    if (App.AuditTrail is null || App.ChangeApplier is null) return;
    var rows = App.AuditTrail.ReadAll().Where(r => r.RunId == runId && r.Applied && !r.Undone).Reverse().ToList();
    foreach (var r in rows)
    {
        var inverse = new GlDrive.AiAgent.AgentChange
        {
            Category = r.Category, Target = r.Target,
            Before = r.After, After = r.Before,
            Reasoning = "Panic revert", EvidenceRef = "panic-revert", Confidence = 1.0
        };
        App.ChangeApplier.Apply(new[] { inverse }, _config, _config.Agent,
            "panic-" + Guid.NewGuid().ToString()[..8], dryRun: false);
        App.AuditTrail.MarkUndone(r.RunId, r.Target, "panic-revert");
    }
    _configManager.Save(_config);
}
```

- [ ] **Step 3: Build + runtime check**

`dotnet build` green. Right-click tray → AI Agent submenu appears. Toggle Enabled → log line confirms start/stop. Pause 24h → agent disabled; after 24h (or with a shortened test timer) it re-enables. Panic revert (after at least one applied run exists) → changes reversed, audit rows flipped `Undone`.

- [ ] **Step 4: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/UI/TrayIconSetup.cs src/GlDrive/UI/TrayViewModel.cs
git commit -m "feat(agent): tray AI Agent submenu with enable/run-now/pause/panic-revert"
```

---

### Task 10.2: "Revert last N runs" modal picker

**Files:**
- Create: `src/GlDrive/UI/RevertRunsDialog.xaml`
- Create: `src/GlDrive/UI/RevertRunsDialog.xaml.cs`
- Modify: `src/GlDrive/UI/TrayViewModel.cs` (add command + tray menu item)

- [ ] **Step 1: Create picker dialog**

`RevertRunsDialog.xaml`:
```xml
<Window x:Class="GlDrive.UI.RevertRunsDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Revert AI Runs" Width="560" Height="400">
  <Grid Margin="12">
    <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
    <TextBlock Text="Select runs to revert. Each reverses every applied change from that run."/>
    <ListBox Grid.Row="1" x:Name="RunList" SelectionMode="Multiple" Margin="0,8" DisplayMemberPath="Label"/>
    <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right">
      <Button Content="Cancel" Width="80" Margin="0,0,8,0" Click="Cancel_Click"/>
      <Button Content="Revert selected" Width="140" Click="Revert_Click"/>
    </StackPanel>
  </Grid>
</Window>
```

`RevertRunsDialog.xaml.cs`:
```csharp
using System.Windows;

namespace GlDrive.UI;

public partial class RevertRunsDialog : Window
{
    public sealed class RunOption
    {
        public string RunId { get; set; } = "";
        public string Label { get; set; } = "";
    }

    public List<string> SelectedRunIds { get; } = new();

    public RevertRunsDialog()
    {
        InitializeComponent();
        Load();
    }

    private void Load()
    {
        var audit = App.AuditTrail;
        if (audit is null) return;
        var runs = audit.ReadAll()
            .Where(r => r.Applied && !r.Undone)
            .GroupBy(r => r.RunId)
            .OrderByDescending(g => g.First().Ts)
            .Take(30)
            .Select(g => new RunOption
            {
                RunId = g.Key,
                Label = $"{g.First().Ts}  run {g.Key[..8]}  ({g.Count()} change(s))"
            })
            .ToList();
        RunList.ItemsSource = runs;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in RunList.SelectedItems.Cast<RunOption>()) SelectedRunIds.Add(item.RunId);
        DialogResult = true; Close();
    }
}
```

- [ ] **Step 2: Add tray command**

```csharp
public ICommand RevertLastNCommand => new RelayCommand(() =>
{
    var dlg = new RevertRunsDialog { Owner = null };
    if (dlg.ShowDialog() != true || dlg.SelectedRunIds.Count == 0) return;
    foreach (var id in dlg.SelectedRunIds) PanicRevertRun(id);
});
```

Add tray menu item:
```csharp
agentRoot.Items.Add(new MenuItem { Header = "Revert last N runs…", Command = vm.RevertLastNCommand });
```

- [ ] **Step 3: Build + runtime check**

`dotnet build` green. With applied rows, open dialog, select 2 runs, revert → all their changes undone in audit trail + config reverted.

- [ ] **Step 4: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/UI/RevertRunsDialog.xaml src/GlDrive/UI/RevertRunsDialog.xaml.cs src/GlDrive/UI/TrayViewModel.cs src/GlDrive/UI/TrayIconSetup.cs
git commit -m "feat(agent): Revert last N runs modal picker with bulk reverse"
```

---

### Task 10.3: "Panic: revert everything" + "Restore from snapshot" buttons in Settings

**Files:**
- Modify: `src/GlDrive/UI/SettingsWindow.xaml`
- Modify: `src/GlDrive/UI/SettingsViewModel.cs`

- [ ] **Step 1: Add commands in SettingsViewModel**

```csharp
public ICommand PanicRevertAllCommand => new RelayCommand(() =>
{
    if (MessageBox.Show("Revert EVERY change the AI agent has ever made? This cannot be undone easily.",
        "DANGER", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
    if (App.AuditTrail is null || App.ChangeApplier is null) return;
    var runs = App.AuditTrail.ReadAll().Where(r => r.Applied && !r.Undone)
        .GroupBy(r => r.RunId).Select(g => g.Key);
    foreach (var runId in runs)
    {
        var rows = App.AuditTrail.ReadAll().Where(r => r.RunId == runId && r.Applied && !r.Undone).Reverse().ToList();
        foreach (var r in rows)
        {
            var inverse = new GlDrive.AiAgent.AgentChange
            {
                Category = r.Category, Target = r.Target,
                Before = r.After, After = r.Before,
                Reasoning = "Panic revert all", EvidenceRef = "panic-all", Confidence = 1.0
            };
            App.ChangeApplier.Apply(new[] { inverse }, _config, _config.Agent,
                "panic-" + Guid.NewGuid().ToString()[..8], dryRun: false);
            App.AuditTrail.MarkUndone(r.RunId, r.Target, "panic-all");
        }
    }
    App.ConfigManager?.Save(_config);
});

public ICommand RestoreFromSnapshotCommand => new RelayCommand(() =>
{
    var dlg = new Microsoft.Win32.OpenFileDialog
    {
        InitialDirectory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GlDrive", "ai-data", "ai-snapshots"),
        Filter = "Snapshot (*.json)|*.json"
    };
    if (dlg.ShowDialog() != true) return;
    if (MessageBox.Show($"Restore config from {System.IO.Path.GetFileName(dlg.FileName)}? A pre-restore snapshot will be saved.",
        "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
    App.SnapshotStore?.Restore(dlg.FileName, App.ConfigManager!.Path);
    MessageBox.Show("Restored. Restart the app to pick up changes.");
});
```

- [ ] **Step 2: Add buttons to Settings AI Agent tab XAML**

```xml
<Button Content="Panic: revert every AI change…" Command="{Binding PanicRevertAllCommand}"
        Background="#662222" Foreground="White" Margin="0,20,0,0" HorizontalAlignment="Left"/>
<Button Content="Restore from snapshot…" Command="{Binding RestoreFromSnapshotCommand}"
        Margin="0,8,0,0" HorizontalAlignment="Left"/>
```

- [ ] **Step 3: Build + runtime check**

`dotnet build` green. Click "Panic: revert every AI change" → confirmation → every applied audit row flips undone + config restored. "Restore from snapshot" opens file picker pre-focused on `ai-snapshots`, confirms, restores config.

- [ ] **Step 4: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/UI/SettingsWindow.xaml src/GlDrive/UI/SettingsViewModel.cs
git commit -m "feat(agent): Panic-revert-all + Restore-from-snapshot buttons in Settings"
```

---

**Phase 10 complete.** Kill switches everywhere.

---

## Phase 11 — First-run consent dialog + help blurb

Ships: PII consent gate on first Enable and brief help text.

### Task 11.1: First-run consent dialog

**Files:**
- Create: `src/GlDrive/UI/FirstRunAgentConsentDialog.xaml`
- Create: `src/GlDrive/UI/FirstRunAgentConsentDialog.xaml.cs`
- Modify: `src/GlDrive/UI/SettingsViewModel.cs`

- [ ] **Step 1: Create dialog**

`FirstRunAgentConsentDialog.xaml`:
```xml
<Window x:Class="GlDrive.UI.FirstRunAgentConsentDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        Title="AI Agent — First-run consent" Width="520" Height="360" ResizeMode="NoResize">
  <Grid Margin="16">
    <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
    <TextBlock Text="Enable nightly AI Agent?" FontSize="16" FontWeight="Bold" Margin="0,0,0,10"/>
    <TextBlock Grid.Row="1" TextWrapping="Wrap">
The agent reviews 7 days of local telemetry each night and sends a compact digest to your configured model (<Run FontWeight="Bold" Text="{Binding ModelId, Mode=OneWay}"/>) via OpenRouter.<LineBreak/><LineBreak/>
Content sent includes: server names, release names, IRC channel names, site section keys, and error signatures. NO passwords, NO certificate material, NO log file contents beyond the pre-digested summaries.<LineBreak/><LineBreak/>
Data stays on disk under %AppData%\GlDrive\ai-data\. Only the nightly digest is transmitted.<LineBreak/><LineBreak/>
The first 3 runs are DRY-RUN — changes are proposed and logged, but not applied. You can review them in the Dashboard → AI Agent tab before the agent goes live.
    </TextBlock>
    <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,10,0,0">
      <Button Content="Cancel" Width="80" Margin="0,0,8,0" Click="Cancel_Click"/>
      <Button Content="I accept, enable" Width="160" Click="Accept_Click"/>
    </StackPanel>
  </Grid>
</Window>
```

Code-behind:
```csharp
using System.Windows;

namespace GlDrive.UI;

public partial class FirstRunAgentConsentDialog : Window
{
    public string ModelId { get; set; } = "";
    public FirstRunAgentConsentDialog(string modelId)
    {
        ModelId = modelId;
        InitializeComponent();
        DataContext = this;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void Accept_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
}
```

- [ ] **Step 2: Gate enable-toggle on consent**

In `SettingsViewModel.AgentEnabled` setter:
```csharp
public bool AgentEnabled
{
    get => _config.Agent.Enabled;
    set
    {
        if (value && !_config.Agent.HasAcceptedConsent)
        {
            var dlg = new FirstRunAgentConsentDialog(_config.Agent.ModelId) { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) { OnPropertyChanged(); return; }
            _config.Agent.HasAcceptedConsent = true;
        }
        _config.Agent.Enabled = value;
        OnPropertyChanged();
    }
}
```

- [ ] **Step 3: Build + runtime check**

`dotnet build` green. Wipe `HasAcceptedConsent` in `appsettings.json`. Toggle Enabled → consent dialog shows → Cancel leaves disabled; Accept enables and sets flag. Subsequent toggles skip the dialog.

- [ ] **Step 4: Commit**

```bash
git status --short | grep -v "^??"
git add src/GlDrive/UI/FirstRunAgentConsentDialog.xaml src/GlDrive/UI/FirstRunAgentConsentDialog.xaml.cs src/GlDrive/UI/SettingsViewModel.cs
git commit -m "feat(agent): first-run consent dialog gates Enable until PII acknowledged"
```

---

### Task 11.2: Help blurb in Dashboard AI Agent tab

**Files:**
- Modify: `src/GlDrive/UI/AgentView.xaml`

- [ ] **Step 1: Add "What is this?" expander**

At the top of AgentView (above the five-tab TabControl), add:
```xml
<DockPanel>
  <Expander DockPanel.Dock="Top" Header="What is the AI Agent?" Margin="6">
    <TextBlock TextWrapping="Wrap" Margin="6">
The AI Agent reviews 7 days of telemetry each night and makes bounded config changes (skiplist patterns, site priority, announce rules, wishlist pruning, pool sizing, blacklist, affils, excluded categories, section mappings, error reports).<LineBreak/><LineBreak/>
Safety rails: change budget (20/run, 5/category), 3 dry-runs before live, audit trail with per-row Undo, snapshot-before-run, freezable fields (right-click any supported field to Freeze), confidence threshold, kill switch in tray.
    </TextBlock>
  </Expander>
  <TabControl> ... existing ... </TabControl>
</DockPanel>
```

- [ ] **Step 2: Build + commit**

`dotnet build` green.
```bash
git status --short | grep -v "^??"
git add src/GlDrive/UI/AgentView.xaml
git commit -m "feat(agent): help expander on AI Agent dashboard tab"
```

---

**Phase 11 complete.** Feature is shippable end-to-end.

---

## Self-review checklist

Run through before shipping:

1. **Spec coverage.** Every §1–§13 item in the spec should map to at least one task:
   - §2 ten categories → Tasks 7.2–7.11 (one validator each).
   - §3 safety rails A/B/C/E/F/G/H/I → budget+confidence in Task 7.1, dry-run in 8.4, audit in 8.1, kill switch in 10.1, snapshot in 8.2, freeze in 5.2/5.3/5.4, cadence in 8.4.
   - §6 ten telemetry streams → Tasks 2.4–2.12.
   - §8 UI (five sub-tabs, tray submenu, Settings tab, freeze UX) → Tasks 9.x, 10.x, 1.2, 5.3–5.4.
   - §9 model integration → Tasks 6.2, 6.3.
   - §10 error handling → scattered across all tasks (try/catch + log warning patterns).
   - §11 PII consent → Task 11.1.
   - §12 verification → every task's runtime-check step.
   - §13 implementation order → matches phases 1–11 here.

2. **Placeholder scan.** No "TBD" / "TODO" outside explicit handoffs between tasks (Task 7.1 stub filled in 8.1 — explicit). `// TODO: plumb once available` in Task 2.4 is acceptable because the note explains what to verify before accepting the placeholder.

3. **Type consistency.** `AuditRow` / `AgentChange` / `ValidationResult` used consistently. `AgentCategories` constants used (not raw strings) in validators. Freeze-path shape `"/servers/{id}/..."` consistent across Task 5.4, 7.x validators, 9.x viewmodel.

4. **File paths.** All absolute from repo root. All new files under `src/GlDrive/AiAgent/...` or `src/GlDrive/UI/...`.

---

## Execution handoff

Plan saved to `docs/superpowers/plans/2026-04-23-ai-log-review.md`.

Two execution options:

**1. Subagent-Driven (recommended)** — fresh subagent per task, review between tasks, fast iteration.
**2. Inline Execution** — tasks run in this session via executing-plans, batch execution with checkpoints.

Which approach?



