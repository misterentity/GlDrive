# AI Nightly Agent — Design Spec

**Date:** 2026-04-23
**Status:** Draft — awaiting user review
**Scope:** A single, internally-consistent feature that adds a BYOK OpenRouter-powered agent to GlDrive. The agent reviews telemetry nightly, proposes and autonomously applies config changes within a bounded authority, and surfaces a Daily Brief + Audit Trail in the Dashboard.

## 1. Goals & Motivation

- GlDrive emits rich runtime behavior (races, IRC announces, transfers, errors) but today nothing learns from it. Rules, priorities, skiplists, pool sizes, and wishlist items drift out of alignment with reality.
- Reuse the already-integrated BYOK OpenRouter AI path (`OpenRouterClient`, `CredentialStore.GetApiKey("openrouter")`) to close that loop.
- Also: expand what we log so the agent has richer signal than today's free-form Serilog output.

## 2. Authority (in scope for autonomous action)

The agent may watch, reason about, and modify **all ten** of the following. Each has per-category invariants; see §5.

1. Skiplist / deny patterns (per site)
2. Site priority tier
3. Section mapping triggers
4. IRC announce regex
5. Excluded notification categories
6. Wishlist pruning
7. Pool / slot sizing (`SpreadPoolSize`, `maxSlots`, `MaxConcurrentRaces`)
8. Per-(site, section) backoff / persistent blacklist (extends v1.44.85–87 logic)
9. Affils
10. Error-recovery (informational only — produces a Markdown issue report, never mutates config)

## 3. Safety rails (all enabled by default)

- **A. Change budget per run** — default 20 total, max 5 per category.
- **B. Dry-run first N runs** — default 3 dry runs before live; user can reset counter any time.
- **C. Full audit trail** — `ai-audit.jsonl` with before/after/reasoning/evidence_ref/confidence per row, Undo per row.
- **E. Kill switch** — tray toggle + "Revert last N runs" + Settings "Panic: revert everything the agent has ever changed".
- **F. Config snapshot before run** — `ai-snapshots/{ts}-{runId}.json`, retained (default 30 copies).
- **G. Freeze markers** — `ai-data/frozen.json` JSON-pointer list; UI affordance: right-click any supported field → Freeze. Ancestor-freeze supported.
- **H. Confidence threshold** — default 0.7; below goes to "Suggestions" with "Apply anyway" button.
- **I. Cadence** — daily at `AgentConfig.RunHourLocal` (default 04:00). Catch-up on wake. `SystemEvents.TimeChanged` recomputes next run.

Explicitly **not** in scope for v1: auto-revert based on metric regression (D), per-category cooldown rate-limits (J). Add later if needed.

## 4. Architecture

### 4.1 New namespace

`GlDrive.AiAgent` containing:

| Component | Purpose |
|---|---|
| `TelemetryRecorder` | Thin facade; one method per stream; background `Channel<string>` writers; fire-and-forget, never blocks caller. |
| `LogDigester` | Reads last N days of telemetry (+ gz) + recent Serilog logs; produces compact per-stream JSON digests. Purely local; no LLM. |
| `AgentMemo` | `ai-data/agent-memo.md` — persistent long-running belief memo; model updates it each run, user can edit. |
| `AgentPrompt` | Composes system + digests + memo + current config (frozen paths masked as `"***FROZEN***"`) + last-3-runs audit summary. |
| `AgentClient` | BYOK OpenRouter; uses `AgentConfig.ModelId`; returns structured `AgentRunResult`. Handles truncation via the existing `RepairTruncatedJson`. |
| `ChangeApplier` | Dispatches on `category`; enforces freeze + confidence + budget + per-category invariants. |
| `AuditTrail` | Append-only `ai-audit.jsonl`; supports flip-on-undo. |
| `SnapshotStore` | Pre-run config backup; retention-bounded. |
| `AgentRunner` | `BackgroundService`-style scheduled loop; catch-up after sleep; manual "Run now". |
| `FreezeStore` | Load/save `frozen.json`; HashSet lookup used by applier; feeds UI. |
| `NukePoller` | Every `NukePollIntervalHours` (default 6): `SITE NUKES` per site, diff vs cursor, correlate against our uploads → `nukes-*.jsonl`. |
| `HealthRollup` | Hourly rollup of `FtpConnectionPool` + `ConnectionMonitor` counters into `site-health-*.jsonl`. |
| `ErrorSignatureSink` | `Serilog.Core.ILogEventSink` — clusters errors by normalized signature; rolls counts hourly into `errors-*.jsonl`. |

### 4.2 Data flow

```
During day  →  TelemetryRecorder appends 10 streams → ai-data/*.jsonl
               NukePoller every 6h → nukes-{date}.jsonl
               ConfigManager.Save diffs user edits → overrides-{date}.jsonl

04:00 local →  AgentRunner.RunOnce()
                 1. SnapshotStore.Save
                 2. LogDigester.Build(windowDays=7, memo=load)
                 3. AgentPrompt.Compose
                 4. AgentClient.Call → AgentRunResult
                 5. ChangeApplier.Apply
                       for each change:
                         if frozen || confidence<threshold || budget-exceeded
                             → suggestions[] + audit row applied:false
                         else
                             → mutate config + audit row applied:true
                 6. AgentMemo.Save(memo_update)
                 7. Write ai-briefs/{date}.md
                 8. Fire tray balloon on next app-foreground
                 9. Dashboard "AI Agent" tab reads on open
```

### 4.3 Storage layout under `%AppData%\GlDrive\`

```
ai-data/
  races-YYYYMMDD.jsonl
  nukes-YYYYMMDD.jsonl
  site-health-YYYYMMDD.jsonl
  announces-nomatch-YYYYMMDD.jsonl
  wishlist-attempts-YYYYMMDD.jsonl
  overrides-YYYYMMDD.jsonl
  downloads-YYYYMMDD.jsonl
  transfers-YYYYMMDD.jsonl
  section-activity-YYYYMMDD.jsonl
  errors-YYYYMMDD.jsonl
  agent-memo.md
  frozen.json
  ai-audit.jsonl
  nuke-cursors.json
  last-run.json
  ai-briefs/YYYYMMDD.md
  ai-snapshots/YYYYMMDD-HHMMSS-{runId8}.json
```

## 5. Change categories

Every change has the canonical shape:

```jsonc
{
  "category": "skiplist",              // one of ten enum values
  "target":   "/servers/srv-abc/…",    // JSON pointer into current config
  "before":   { … },
  "after":    { … },
  "reasoning": "Site rejected 14/14 DUBBED releases in 7-day window.",
  "evidence_ref": "races-20260418.jsonl:12-34,race-history.json:ids=abc,def",
  "confidence": 0.92
}
```

| # | Category | Can propose | Target shape | Per-category invariants |
|---|---|---|---|---|
| 1 | `skiplist` | Add / update / remove `SkiplistRule` | `/servers/{id}/spread/skiplistRules/-` or `…/N` | Pattern must compile (regex or glob). Removing a user-added rule requires confidence > 0.9 AND 30d no-match. Adding deny always ok; adding allow requires confidence > 0.8. |
| 2 | `priority` | Bump `SitePriority` enum ±1 tier | `/servers/{id}/spread/sitePriority` | ±1 tier/run; needs ≥20 completed races in window. Never sets `VeryHigh` autonomously. |
| 3 | `sectionMapping` | Add row, patch `trigger` on existing row | `/servers/{id}/spread/sectionMappings/…` | Existing rows: patch `trigger` only if current is default (`.*` or empty). `trigger` must compile. Preserves v1.44.76+ semantics. |
| 4 | `announceRule` | Add rule, patch `pattern` on existing rule | `/servers/{id}/irc/announceRules/…` | New pattern must compile AND match ≥3 samples from `announces-nomatch`. Patching keeps original as suggestion row. |
| 5 | `excludedCategories` | Add section key to excluded notifications | `/servers/{id}/notifications/excludedCategories/-` | Section must have >90% racing rejection OR zero user-interactions in 30d. |
| 6 | `wishlistPrune` | Soft-mark "dead" or hard-remove | `/wishlist/items/{id}` | Soft: 60d no match + series ended (TVMaze) or movie flop (OMDB). Hard: soft-marked 30d + user didn't restore. |
| 7 | `poolSizing` | Tweak `SpreadPoolSize`, `maxSlots`, `MaxConcurrentRaces` | `…/spreadPoolSize`, `…/maxSlots`, `/spread/maxConcurrentRaces` | ±25% per run, clamp [2, 32]. Needs pool-exhaustion or borrow-timeout evidence. |
| 8 | `blacklist` | Add / extend / remove `(site, section)` entry | `/servers/{id}/spread/blacklist/-` | Add: ≥3 consecutive 550 MKD in window (aligns with v1.44.87). Remove: 14d clean. |
| 9 | `affils` | Add group to site's affils | `/servers/{id}/spread/affils/-` | Group must appear ≥5 times as "pre'd by" or "aff". Never removes. |
| 10 | `errorReport` | **Informational** — `ai-briefs/issues/{date}-{sig}.md` | n/a | No config mutation; confidence + budget don't apply. |

**Rejected changes** (frozen, low-confidence, budget-exceeded, invariant-failed) write `applied:false` audit rows and appear in the Suggestions sub-tab.

**Undo:** non-informational audit rows capture full `before` object. Undo writes an override telemetry row so next prompt sees "user undid this — don't do again".

## 6. Telemetry expansion (10 streams)

All envelopes prepend `{ "ts": "...", "v": 1, … }`. Retention: daily rotation at 00:00 local; gzip after `GzipAfterDays` (30); delete after `DeleteAfterDays` (90). Bounded per-file: at `TelemetryMaxFileMB` (100MB) per day, switch to 1-in-N sampling and log warning.

| # | Stream | Schema shape | Emit point |
|---|---|---|---|
| 1 | `races` | `{raceId, section, release, startedAt, endedAt, participants:[{serverId, role, bytes, files, avgKbps, abortReason?}], winner, fxpMode, scoreBreakdown, result, filesExpected, filesTotal}` | `SpreadJob.RunAsync` completion (success + failure paths) |
| 2 | `nukes` | `{serverId, section, release, nukedAt, nuker, reason, multiplier, ourRaceRef?}` | `NukePoller` — `SITE NUKES` every 6h, diff vs `nuke-cursors.json`, correlate against `races-*.jsonl` |
| 3 | `site-health` | `{serverId, windowStart, windowEnd, avgConnectMs, p99ConnectMs, disconnects, tlsHandshakeMs, poolExhaustCount, ghostKills, errors5xx, reinitCount}` | `HealthRollup` — hourly from `FtpConnectionPool` + `ConnectionMonitor` counters |
| 4 | `announces-nomatch` | `{serverId, channel, botNick, message, timestamp, nearestRulePattern?, nearestRuleDistance?}` | `IrcAnnounceListener` — when no rule matches AND heuristic says "announce-y" |
| 5 | `wishlist-attempts` | `{wishlistItemId, release, score, matched, missReason?, section, serverId}` | `WishlistMatcher` — every comparison |
| 6 | `overrides` | `{ts, jsonPointer, beforeValue, afterValue, aiAuditRef?}` | `ConfigManager.Save` — diff vs most recent snapshot; `aiAuditRef` linked if reverting a recent AI change |
| 7 | `downloads` | `{downloadId, serverId, remotePath, result, bytes, elapsedMs, retryCount, failureClass?}` | `DownloadManager` state transitions |
| 8 | `transfers` | `{raceId, srcServer, dstServer, file, bytes, elapsedMs, ttfbMs, pasvLatencyMs, abortReason?}` | `FxpTransfer.ExecuteAsync` per file |
| 9 | `section-activity` | `{serverId, section, filesIn, bytesIn, ourRaces, ourWins, dayOfWeek}` | End-of-day rollup from (1) + `NewReleaseMonitor` counters |
| 10 | `errors` | `{component, exceptionType, normalizedMessage, stackTopFrame, count, firstAt, lastAt}` | `ErrorSignatureSink : ILogEventSink` — clusters errors; rolls counts hourly |

**Digester:** `LogDigester.Build(windowDays)` produces compact per-stream digests (≤1KB typical): win rates, route kbps matrix, abort-reason histograms, nuke top-20, health regressions, nomatch clusters, dead wishlist items, user-override paths since last run, failure-class histograms, top error signatures. Digests + raw-last-24h evidence pointers go into the prompt.

## 7. Safety-rail implementation details

### 7.1 Change budget

`MaxChangesPerRun` (20) + `MaxChangesPerCategory` (5). Per-category counters decrement as changes pass. Once capped, remaining go to `suggestions[]` with reason `"budget-exceeded"`. Total cap checked first so a single run cannot smuggle 5×10=50 changes.

### 7.2 Dry-run

`DryRunsRemaining` (int, default 3). Each run decrements to 0 min. While >0: validators run, audit rows written with `dryRun:true`, brief written, no mutation. Header on brief: "DRY RUN — N more dry runs before going live". User can reset to any int any time.

### 7.3 Audit trail row

```jsonc
{
  "ts": "...", "runId": "uuid", "category": "skiplist",
  "target": "/servers/…", "before": {…}, "after": {…},
  "reasoning": "...", "evidenceRef": "...", "confidence": 0.92,
  "applied": true, "dryRun": false,
  "rejectionReason": null,
  "undone": false, "undoneAt": null, "undoneReason": null
}
```
Undo flips `undone`, reverses mutation, emits an `override` telemetry row referencing this audit row.

### 7.4 Kill switch (3 layers)

1. Tray → AI Agent → `Enabled [✓]` — `CancellationToken` fires; in-flight run aborts; partial changes stay (with audit rows).
2. Tray → AI Agent → "Revert last N runs…" — modal picker; reverses every audit row in selected runs.
3. Settings → AI Agent → "Panic: revert everything the agent has ever changed" — with confirmation.

### 7.5 Freeze markers

`ai-data/frozen.json`:
```jsonc
[{ "path": "/servers/srv-abc/spread/maxSlots", "frozenAt": "...", "note": "..." }]
```
UI: attached property `AiAgent.FreezablePath` on supported controls; context menu "Freeze for AI" / "Unfreeze"; lock-glyph indicator. Ancestor-freeze: freezing `/servers/srv-abc` freezes all descendants. `ChangeApplier` rejects before calling the validator.

### 7.6 Confidence threshold

`ConfidenceThreshold_x100` (int 0–100, default 70). Rejected-below-threshold changes still get audit rows + appear in Suggestions with "Apply anyway" button. No information is silently lost.

### 7.7 Cadence

`AgentRunner` runs in-proc:
- Startup: if `now > last-run + 23h`, schedule immediate catch-up.
- Else sleep until next `RunHourLocal`.
- `PeriodicTimer` + `SystemEvents.PowerModeChanged` for wake detection.
- `SystemEvents.TimeChanged` recomputes.
- Manual "Run now" bypasses schedule.
- Concurrency: `SemaphoreSlim(1)` — one run at a time.

### 7.8 Mid-run failure

If model call / validator / mutation throws:
- Already-applied changes stay (their audit rows enable undo).
- `last-run.json` updates.
- Brief written with `status: "partial-failure"` and exception message.
- Next run proceeds on schedule.

## 8. UI surface

### 8.1 Dashboard "AI Agent" tab (after existing tabs)

Five sub-tabs:

| Sub-tab | Content |
|---|---|
| **Today's Brief** | Markdown render of `ai-briefs/{latest}.md`. Header card: run ts, total changes, dry-run status, "Run now". Collapsible category cards with per-row Undo. Footer: "Next run in X" + cadence link. |
| **Audit Trail** | DataGrid over `ai-audit.jsonl`. Columns: time, run, category, target, reasoning, confidence, status, Undo. Filters: date, category, status, serverId. |
| **Suggestions** | Audit rows with `applied:false AND rejectionReason != "frozen"`. Per-row "Apply anyway" (re-runs applier with waived rule) and "Dismiss". |
| **Frozen Fields** | List from `FreezeStore`. Per-row Unfreeze. Add button opens tree picker of known settings paths. |
| **Memo** | Plain-text editor over `agent-memo.md`. Save confirmation: "Agent will see this as ground truth next run." |

### 8.2 Tray

New submenu **AI Agent**: `Enabled [✓]`, `Run now`, `Open dashboard…`, `Pause 24h`, `Panic: revert last run`. Morning balloon on first foreground after a new run: "AI made N changes overnight — click to review" → opens Dashboard at this tab.

### 8.3 Freeze field UX

Attached property `AiAgent.FreezablePath="..."` on each field the agent can modify. Generic app-wide style: context menu + lock glyph + tooltip. Only ~30 paths need annotation (one per supported field from §5).

### 8.4 Settings — "AI Agent" tab in `SettingsWindow`

Binds to `AppConfig.Agent`:

```csharp
public class AgentConfig
{
    public bool Enabled { get; set; } = false;              // opt-in
    public int RunHourLocal { get; set; } = 4;
    public int ConfidenceThreshold_x100 { get; set; } = 70; // int for JSON ergonomics
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
}
```

Model combo: recommendation card "Suggested: anthropic/claude-sonnet-4-6 — best long-context reasoning"; quick-picks: Sonnet 4.6, Opus 4.7, Gemini 2.5 Pro; free-text for any OpenRouter id.

Info pane: "Data at %AppData%\GlDrive\ai-data\ — click to open".

"Revert all AI changes…" (danger red) with confirmation.

## 9. Model integration

`AgentClient` extends the `OpenRouterClient` pattern. Differences from `AnalyzeSiteRules`:
- Model id from `AgentConfig.ModelId` (distinct from legacy `AppConfig.OpenRouterModel` which stays for site-setup).
- `max_tokens: 32000`; `RepairTruncatedJson` handles truncation.
- `response_format: { "type": "json_object" }` when supported; soft-fall-back to regex extract.
- Timeout: 5 minutes.
- Logs request/response sizes only; never the body (PII).

Prompt shape:
```
SYSTEM: You are an operations agent for GlDrive. Your job: analyze N days of structured telemetry and propose changes within the 10 allowed categories and their invariants. Never touch frozen paths. Cite evidence for every change. Return STRICT JSON matching the schema below. Prefer low confidence or emit nothing if unsure.
        [category schema + invariants inline, compact form]
        [frozen paths list]
        [previous memo]

USER:   [pre-digested N-day telemetry bundle, ~10-30KB]
        [last 3 run summaries + their audit delta]
        [current config redacted — frozen paths replaced with "***FROZEN***"]
        Emit: { memo_update, changes[], suggestions[], brief_markdown }
```

Cost ceiling: `AgentClient` captures token counts from response; brief footer shows "Tokens: Xk in / Yk out — est. $Z".

## 10. Error handling

| Failure | Behavior |
|---|---|
| Telemetry writer channel full | Drop event; increment `telemetry_drops`; warn once per 5 min. |
| Telemetry file >`TelemetryMaxFileMB` | Switch to 1-in-10 sampling; warn. |
| Telemetry file unreadable (corrupt line) | Digester skips line, counts errors, continues; surfaced in brief footer. |
| Snapshot save fails | Run aborts with `status: "failed-pre-run-snapshot"`; no model call; no mutations. |
| Model HTTP error | Retry once with 30s backoff; failure brief; next run tomorrow. |
| Model returns invalid JSON (after repair) | Log response hash; failure brief; no changes. |
| Unknown category in change | Skip with `rejectionReason: "unknown-category"`; others proceed. |
| Validator throws | Skip with `rejectionReason: "invariant-failed:<exc>"`; others proceed. |
| Mutation throws mid-apply | Roll that change back (restore `before`); skip; others proceed. |
| Config save fails after mutations | In-memory stays mutated; warn on next launch; audit rows are authoritative. |
| `NukePoller` site unreachable | Per-site circuit breaker (3 fails → skip for the day). |
| `SITE NUKES` format drift | Lenient parser; unparsed lines go to `nuke-nomatch` style stream for the agent to propose a parser fix via `errorReport`. |
| Clock change / DST | `SystemEvents.TimeChanged` handler recomputes next run; 23h dedup. |
| Hibernate through schedule | Catch-up on wake; missed runs don't stack. |
| Kill switch mid-run | `CancellationToken`; partial changes stay (audit rows); partial-failure brief. |
| User edits `agent-memo.md` mid-run | Next run reads edited version — user always wins. |
| Malformed `frozen.json` | Treat as empty; warn. |
| Disk full | Telemetry drops; snapshot failure aborts run cleanly. |

## 11. Security / PII

- Release names, nicks, site names, IRC channels are sent to the model — same posture as existing `AnalyzeSiteRules`. No new surface.
- First-run consent dialog on `Enabled = true`: "The AI agent sends telemetry summaries to `{ModelId}` via OpenRouter. Content includes server names, release names, and IRC channel names. Continue?"
- API key stays in Credential Manager; never logged; never in briefs.
- Audit trail + snapshots are local disk only. Passwords stay in Credential Manager, never in `appsettings.json` snapshots.

## 12. Verification plan (manual — no test project per CLAUDE.md)

1. `dotnet build src/GlDrive/GlDrive.csproj` green after each layer.
2. Telemetry smoke: 1h of normal use; verify each of 10 streams has ≥1 record.
3. Digester dry: hidden "Build digest (debug)" menu item writes `last-digest.json` + token estimate; no model call.
4. Dry-run cycle with `DryRunsRemaining=3`, "Run now": snapshot created, brief written, all audit rows `dryRun:true`, no mutation, memo updated.
5. Live run restricted to `errorReport` only: full round trip; brief renders; audit populates; memo persists.
6. Full live run: budget clamp, frozen respect, confidence threshold, undo, kill switch.
7. Sleep/wake: schedule 04:00, sleep 03:00, wake 09:00 → catch-up fires.
8. Kill switch mid-run: cancellation + partial-failure brief.
9. Panic revert: audit rows flip `undone:true`; config matches pre-run.
10. Ugly input: corrupt a jsonl, kill mid-snapshot, unplug network mid-model-call — each terminates cleanly.

## 13. Implementation order

Each step independently shippable; app remains usable after each:

1. `AgentConfig` + Settings UI scaffolding (plumbing only, no behavior).
2. `TelemetryRecorder` + the 10 emit points.
3. `NukePoller`.
4. `LogDigester`.
5. `FreezeStore` + field annotations in UI.
6. `AgentPrompt` + `AgentClient`.
7. `ChangeApplier` + 10 validators (one per release).
8. `AuditTrail`, `SnapshotStore`, `AgentMemo`, `AgentRunner`.
9. Dashboard "AI Agent" tab (all 5 sub-tabs).
10. Kill switch, panic revert, restore-from-snapshot.
11. First-run consent dialog + help blurb.

## 14. Out of scope for v1

- Auto-revert based on metric regression (safety rail D).
- Per-(site, category) cooldown rate-limiting (safety rail J).
- Slack/Discord webhook delivery of the brief.
- Cross-user / cross-machine federated learning.
- Model fine-tuning or any training data retention for third parties.
- Modifying anything outside the ten categories (credentials, TLS certs, mount points, download paths, theme, etc.).

## 15. Open questions (flag for implementation time, not blocking design)

- `SITE NUKES` output format: confirm across glftpd versions in user's fleet; adjust parser accordingly.
- Exact JSON-pointer paths for every field in §5 — to be pinned when `AgentConfig` + annotations land.
- Whether `response_format: json_object` is supported on the default Sonnet 4.6 via OpenRouter at implementation time — if not, rely solely on the existing `RepairTruncatedJson` path.
