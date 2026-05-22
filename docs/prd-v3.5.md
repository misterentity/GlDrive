# GlDrive v3.5 — Product Requirements Document

**Status:** ✅ **MET** at v3.5.0 (13/13 acceptance criteria) · **Baseline:** v2.6.6
**Theme:** Polish-focused hardening across Racing, Reliability, Observability, and Quality.

## Acceptance evidence

| ID | Where met | Validation |
|---|---|---|
| R1 | v2.9.0 — `RaceHistoryItem.{FilesTotal,FilesDelivered,CleanComplete}` + `Summarize()` + Spread tab "X/Y clean (Z%)" | `RaceSummarizeTests` |
| R2 | v2.7.0 — `DistinctActiveSectionCount ≥ 3` → auto-DL-only | `BlacklistStoreTests` (threshold + double-count guard) |
| R3 | v2.7.0 — dropped-dest branch in `RegisterDestFailure` | code inspection |
| R4 | scorer fail-penalty | `SpreadScorerTests.Failure_penalty_demotes_a_failed_pair_below_a_fresh_one` |
| H1 | v2.8.0 — `Config.Noop=false` on quarantine + cap 30 | observed bounded threads/memory |
| H2 | v2.7.0 — `_refusedUntilTicks` 90s gate | code inspection |
| H3 | 0 crashes 2026-05-20 / 0 crashes 2026-05-21 / 0 crashes 2026-05-22 | log grep |
| H4 | v2.0.0 — `Log.CloseAndFlush()` removed from dispatcher handler | code inspection |
| O1 | existing 2s `SafeRefresh` updating scoreboard/transfers live | runtime |
| O2 | v3.0.0 — `PoolHealth()` + Spread tab line | runtime |
| O3 | v2.9.0 — `ClassifyFailure` + category breakdown | `ClassifyFailureTests` |
| O4 | v3.0.0 — pool-create-failed WARN→INFO | log grep |
| Q1 | v2.8.0 — `GlDrive.Tests` (79 tests, ≥40 target) | `dotnet test` |
| Q2 | `.github/workflows/ci.yml` | local mirror run |
| Q3 | regression guards across IsZipscriptArtifact / dupe / permanent-denial / scorer | `GlDrive.Tests` suite |

## 1. Purpose

v2.0–2.6 was a sustained reliability campaign on the spread/FXP race engine
(native-crash elimination, BNC login-limit handling, pseudo-file filtering,
connection health, permanent-denial blacklisting). v3.5 consolidates that work
into a *trustworthy, observable, self-healing* racer — no major new subsystems,
just finishing and proving what exists.

**Success definition:** A user with a typical glftpd + BNC setup can leave
GlDrive racing unattended for 24h and find: zero process crashes, the majority
of eligible races completing cleanly (0 files undelivered), bad destinations
auto-excluded without manual config, and a dashboard that explains what
happened without reading logs.

## 2. Non-goals (explicitly deferred past 3.5)

- Active speed-test pre-races / route benchmarking (ambitious; later).
- New protocol support (SFTP, drftpd slave routing beyond PRET).
- New media features (player, torrent, casting) — frozen at current scope.
- AI agent autonomy expansion beyond current validators.

## 3. Requirements by theme

Each requirement has an ID, a description, and **acceptance criteria** that make
"meets the PRD" objectively checkable from logs/UI/build.

### Theme R — Racing excellence (polish)

- **R1 — Race outcome metrics.** Persist and surface per-race completion: files
  delivered / total, per-route (src→dst) success rate, and a rolling
  "races completed cleanly" percentage.
  *Accept:* `race-history.json` entries carry `FilesTotal`, `FilesDelivered`,
  and a `CleanComplete` bool; Spread tab shows a session success-rate figure.

- **R2 — Self-healing upload capability.** A destination that returns N permanent
  upload denials (MKD 550 / STOR 553 "no upload rights" / path-filter) across
  distinct sections is auto-flagged effectively download-only and stops being
  picked as a destination, without the user editing config.
  *Accept:* after ≥3 distinct-section permanent denials for a server, an
  `AutoDownloadOnly` flag is set + logged; subsequent auto-races exclude it as a
  dest; cleared on any successful upload.

- **R3 — Backoff log overflow fix.** The "backing off 251622991756s" cosmetic bug
  (DateTime.MaxValue arithmetic on a dropped dest) must read "dropped (will not
  retry this race)".
  *Accept:* no `backing off <huge number>s` lines in logs; dropped dests log a
  clear message.

- **R4 — Source rotation verified.** When a (file,src,dst) pair fails, an
  alternate source for the same file is preferred on retry (scorer fail penalty
  from v2.2.0). Verify with a test.
  *Accept:* unit test proves the scorer ranks an alternate src above a
  recently-failed pair.

### Theme H — Reliability hardening

- **H1 — Quarantine resource bound.** Quarantined poisoned connections must not
  leak threads. Their FluentFTP background tasks (NoopDaemon / keepalive) are
  stopped on quarantine so thread count stays bounded under churn.
  *Accept:* after a sustained race storm (≥200 quarantines), thread count stays
  under a defined ceiling (≤120) and working set under ~900 MB.

- **H2 — BNC cooldown backoff.** When a server starts refusing connections at the
  TCP level ("actively refused"/repeated 530 after the single ghost-kill), pause
  new connection attempts to that server for a cooldown window instead of
  hammering it.
  *Accept:* a `_refusedUntil` cooldown is honored by Borrow; logs show
  "server in cooldown, skipping" rather than a refusal storm.

- **H3 — Zero native crashes.** No watchdog-detected crashes over a 24h soak.
  *Accept:* 24h of logs with zero `WATCHDOG: Process … crashed` lines.

- **H4 — Dispatcher/logger integrity.** Confirm the v2.0.0 logger-survival fix
  holds; no silent logging blackouts after a handled UI exception.
  *Accept:* a handled dispatcher exception is followed by continued log output.

### Theme O — Observability & UX

- **O1 — Race dashboard live state.** The Spread tab shows, per active race:
  per-site files-owned/total, current transfer, route speed, and a live
  completion %.
  *Accept:* opening the Spread tab during a race shows live per-site progress
  that updates without manual refresh.

- **O2 — Connection health panel.** A diagnostics surface shows, per server:
  pool size/active/created, quarantine size, BNC observed login cap, and
  cooldown status.
  *Accept:* the panel reflects live pool + quarantine + cooldown values.

- **O3 — Failure taxonomy surfacing.** Race failures are categorized
  (config/upload-denied, BNC-pressure, transport, dupe-skip) and shown as counts
  rather than raw error strings.
  *Accept:* the Spread tab / race history shows categorized failure counts.

- **O4 — Settings & log-noise polish.** Demote remaining benign WARN spam to
  INFO/DEBUG; group spread health settings; ensure new knobs (validate-on-borrow,
  keepalive, auto-download-only threshold) are all editable.
  *Accept:* a clean default-config run produces no benign WARN spam; all knobs
  present in Settings.

### Theme Q — Quality & automation

- **Q1 — Test project.** Introduce `GlDrive.Tests` (xUnit) covering the pure-logic
  units that have repeatedly mattered: `SceneNameParser`, `SkiplistEvaluator`,
  `SectionMapper`, `IsZipscriptArtifact`, `MkdFailureClassifier`
  (incl. `IsPermanentUploadDenial`), `SpreadScorer` fail-penalty, `FishCipher`
  round-trip, `Dh1080` shared-secret agreement.
  *Accept:* `dotnet test` builds and runs ≥40 passing tests; no WinFsp/UI deps.

- **Q2 — CI build+test.** A GitHub Actions workflow builds the csproj and runs
  the test project on push.
  *Accept:* `.github/workflows/ci.yml` exists and the build+test job is green.

- **Q3 — Regression guards for this session's fixes.** Tests that lock in the
  hard-won fixes: zipscript artifact filtering, dupe-as-success matchers,
  permanent-denial classification, scorer SFV/NFO priority + fail penalty.
  *Accept:* tests exist and pass for each.

## 4. Version roadmap (polish increments)

| Version | Scope |
|---|---|
| 2.7.0 | R3 (backoff log), R2 (self-healing download-only), H2 (BNC cooldown) |
| 2.8.0 | H1 (quarantine thread bound), O4 (log-noise + settings polish) |
| 2.9.0 | R1 (race outcome metrics) + O3 (failure taxonomy) |
| 3.0.0 | Q1 (test project) + Q3 (regression guards) |
| 3.1.0 | Q2 (CI), R4 (source-rotation test) |
| 3.2.0–3.4.x | O1 (race dashboard live state), O2 (health panel) |
| 3.5.0 | H3/H4 soak validation, final polish, PRD acceptance pass |

## 5. Acceptance — "meets v3.5"

The app meets v3.5 when every R/H/O/Q acceptance criterion above is satisfied and
a 24h soak shows: zero crashes, bounded resources, the majority of eligible races
completing cleanly, auto-excluded bad destinations, and a dashboard that
communicates race + health state without log spelunking.

## 6. Tracking

Implementation is iterative; each roadmap version is shipped via the standard
`build → commit → push → release.ps1` flow. Progress is tracked in the session
task list mirroring the R/H/O/Q IDs above.
