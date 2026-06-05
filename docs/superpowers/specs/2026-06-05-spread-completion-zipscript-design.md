# Spread: race until destination zipscript-complete, with refresh + alternate-source failover

**Status:** Approved design (2026-06-05) — ready for implementation plan
**Target version:** v3.8.0
**Primary file:** `src/GlDrive/Spread/SpreadJob.cs` (2463 lines) + `SpreadManager.cs`, `Models.cs`, `Config/SpreadConfig.cs`, `RaceHistoryStore.cs`

## Problem

Today a race completes on **file-count ownership**, not on the destination actually being marked complete by glftpd's zipscript:

- `IsJobComplete()` (`SpreadJob.cs:2069`) returns true when `_fileInfos.Count > 0` and every non-download-only dest owns `>= _fileInfos.Count` files (plus `_expectedFileCount` when an SFV was parsed). It never reads any zipscript completion signal.
- The scanner *actively discards* the `[ COMPLETE ]` marker — `IsZipscriptArtifact` (`SpreadJob.cs:1127–1146`) catches it via the "0-byte name starting with `[`" rule (line 1139) and drops it before it enters state (`ScanDirectoryRecursive` lines 1199–1220).
- `SiteProgress.IsComplete` (per-site, set at lines 1104 + 1286) is computed but **ignored** by the race-level gate at line 622.
- If the release **moves off the source** mid-race, `_fileOwnership` still lists the dead source (line 41), so `FindBestTransfer` (lines 1330–1575) keeps picking `(file, deadSrc, dst)` tuples that fail RETR 550. Nothing purges ownership on source disappearance; the race only ends via the global `HardTimeoutSeconds` (lines 529–531, default 1200s).

## Goals

1. A race runs until each **destination** is confirmed zipscript-complete (markers + heuristic), not merely until source files were copied.
2. Periodic **directory refreshes** of source and dest drive completion detection and source-move detection.
3. When the release **leaves the source**, search connected sites for an alternate source and continue feeding the target from it.

## Decisions (confirmed with user 2026-06-05)

| # | Decision | Choice |
|---|----------|--------|
| 1 | What counts as "complete" | **Markers + heuristic** — configurable completion marker OR (all SFV files present AND no `-MISSING-` stubs) |
| 2 | All dests vs per-dest | **Per-dest, independent** — each dest completes or times out on its own; race ends when all dests terminal |
| 3 | Wait window vs hard timeout | **Separate completion timer** — `DestinationCompletionWaitMinutes`, independent of `HardTimeoutSeconds` |
| 4 | Failover trigger | **Reactive + confirm** — on source-side RETR-550, re-probe; if gone, purge + parallel-search alternate source |
| — | Completion window default | **10 minutes** |
| — | Refresh cadence during await | **30 seconds** |
| — | `WaitForDestinationComplete` default | **on** (auto-races now wait for real completion) |

## Design

### A. Completion detection — per-destination, zipscript-aware

Introduce per-destination completion state evaluated **after each scan's reconcile** (after `SpreadJob.cs:1097`), never inside `ProcessFiles` (early-processed sites see a partial `FilesTotal` — Report 1 gotcha).

A destination is **Complete** when *either*:

- **(a) Marker** — a configured completion marker (new `SpreadConfig.CompletionMarkers`, matched case-insensitively like `NukeMarkers`) is present in that dest's release directory, **or**
- **(b) Heuristic** — every SFV-declared file is present for that dest (`destOwned >= _expectedFileCount`, or `>= _fileInfos.Count` when no SFV was parsed) **and** zero `-MISSING-` stubs are present in that dest's current listing.

The scanner must now **capture** two signals it currently filters away, *per destination*, while still excluding both from `FilesTotal`/`_fileInfos`:

- completion-marker sightings (currently dropped at `SpreadJob.cs:1139`)
- `-MISSING-` stub presence (currently dropped at lines 1206–1216)

Implementation: `ScanDirectoryRecursive` collects these into the per-site scan result; they are folded into a per-dest completion structure during the post-scan reconcile pass (lines 1094–1106).

`IsZipscriptArtifact` is NOT loosened for `FilesTotal` purposes (markers/stubs still must not count as real files); a parallel capture path records "this site showed a completion marker" / "this site still has -MISSING stubs".

### B. Race lifecycle — per-dest terminal states

Replace the single all-dests gate (evaluated at `SpreadJob.cs:622`) with per-destination state:

```
Transferring → AwaitingCompletion → { Complete | TimedOut }
```

- A dest enters **AwaitingCompletion** when it owns all files (`destOwned >= total`); stamp `allFilesAt[dest]`.
- During AwaitingCompletion the loop polls that dest's listing each `CompletionRefreshIntervalSeconds`.
- The dest becomes **Complete** when the §A marker/heuristic confirms it.
- The dest becomes **TimedOut** when `DestinationCompletionWaitMinutes` elapse from `allFilesAt[dest]` without completion (recorded as partial for that dest).
- **The race ends when every participating dest is terminal** (Complete or TimedOut), OR `_isNuked` (lines 567–570), OR the absolute ceiling. "Participating" = non-download-only dests (same exclusion `IsJobComplete` applies at line 2080); download-only dests never enter the completion gate.

This makes the per-site `IsComplete`-style signal load-bearing instead of cosmetic.

### C. Directory refreshes

Reuse the existing scan loop (adaptive debounce 2s/5s, `SpreadJob.cs:549–564`, `_forceScan` line 1721 / 704):

- During AwaitingCompletion (no active transfers) cadence switches to `CompletionRefreshIntervalSeconds` (default 30s).
- **Release spread connections between polls** so a multi-minute wait does not squat FXP slots (matters under the account login cap — see `project_fxp_permit_reservation`). Re-borrow per scan.
- Force a scan on entering the await phase.

### D. Wait window vs. hard timeout

- `HardTimeoutSeconds` (default 1200) remains the **transfer-phase** budget.
- `DestinationCompletionWaitMinutes` (default 10) is the **per-dest await budget**, independent of the hard timeout.
- Absolute safety ceiling for the whole race = `HardTimeoutSeconds + DestinationCompletionWaitMinutes*60`, so the global timer (lines 529–531) cannot kill a legitimate await phase, but a wedged race still terminates.

### E. Source-move detection + alternate-source failover (reactive + confirm)

In the transfer-failure handler (`SpreadJob.cs:1757–1815`):

1. **Classify** the failure. A **source-side** `RETR 550 / file-not-found / no such file` (distinct from credit-exhaustion `_sourceCreditDenied` line 100, and distinct from dest-side failures) does **not** increment the dest backoff (`RegisterDestFailure` line 2205) — the dest is blameless.
2. **Confirming re-probe** — `DirectoryExists(sourcePath)` (or a listing) on the suspect source's release dir.
3. If confirmed gone (**source migrated**):
   - Purge that source from `_fileOwnership` (all files) and from the instance `sourceServers` set.
   - If no remaining source owns the still-missing files → trigger **alternate-source search**.
4. **Alternate-source search**:
   - Factor the existing `RequestFiller.TryFill` pattern (`RequestFiller.cs:111–155`) into a reusable `SpreadManager.SearchReleaseOnServers(release, excludeIds, ct)`.
   - Parallel `Search()` (`FtpSearchService.cs:45`) across `GetConnectedServerIds()` (`SpreadManager.cs:927`), excluding the dead source and the dests.
   - Filter candidates by skiplist rules (`SkiplistEvaluator`) + section blacklist (`SectionBlacklistStore`).
   - Pick the best exact-release-name match (most files / matching size). Probe its path, add to `sourceServers`/`sitePaths`; the next scan populates `_fileOwnership` and transfers resume.

**Guards:**
- If `_isNuked` — release is dead, abort; do **not** search (a nuke is not a move).
- Source-migrated is classified separately from the 60-min dead-race suppression (`SpreadManager.cs:38`) so a re-search is allowed.
- Search respects `AlternateSourceSearchTimeoutSeconds`; on no match, affected dests time out as partial.

**Scope note:** extend the **existing source-side failure classification** (same mechanism as the v3.5.3 credit-exhaust parking) rather than refactoring backoff to per-`(file,src,dst)`. Lower risk; achieves the same "don't punish the dest" outcome.

### F. Config surface

New `SpreadConfig` fields (all defaulted; placed after the existing fields at `Config/SpreadConfig.cs:12–31`):

```csharp
public bool WaitForDestinationComplete { get; set; } = true;
public int  DestinationCompletionWaitMinutes { get; set; } = 10;
public int  CompletionRefreshIntervalSeconds { get; set; } = 30;
public List<string> CompletionMarkers { get; set; } = new()
    { "[ COMPLETE ]", "[ COMPLETED ]", "(COMPLETE)", "COMPLETE", "-=COMPLETE=-" };
public bool AlternateSourceSearch { get; set; } = true;
public int  AlternateSourceSearchTimeoutSeconds { get; set; } = 20;
```

`RaceHistoryItem` (`RaceHistoryStore.cs:9–40`) gains `string DestinationState` — a per-dest summary, e.g. `"2 complete · 1 timeout"`.

### G. Models / job state

New `SpreadJob` fields (after line 124):

- `_destCompletion : ConcurrentDictionary<string, DestCompletion>` where `DestCompletion = { DestState State; DateTime? AllFilesAt; }` and `enum DestState { Transferring, AwaitingCompletion, Complete, TimedOut }`.
- per-scan capture buffers for marker sightings + `-MISSING-` presence per site.
- `sourceServers` promoted from a local (built once at line 265) to an **instance field** so it can be mutated on failover.
- a reference/callback to `SpreadManager` for `SearchReleaseOnServers` (mirror how the job already reaches pools).

`SpreadManager`: new `SearchReleaseOnServers(release, excludeIds, ct)` (factored from `RequestFiller`), reused by both RequestFiller and the failover path.

## Testing

Pure-logic unit tests (FTP/await timing stays manual per CLAUDE.md — WinFsp/live-FTP layers require runtime verification):

1. **Completion evaluator**: marker present → Complete; all files + no `-MISSING-` → Complete; missing file → Pending; `-MISSING-` stub present → Pending; marker absent + heuristic met → Complete.
2. **Completion-marker matching**: case-insensitive `contains` against the configurable list; custom marker; empty list falls back to heuristic only.
3. **Source-migrated classifier**: `RETR 550 not-found` → migrated; credit-`550` → NOT migrated (credit path owns it); transient timeout / `FaultSide=None` → NOT migrated.
4. **Per-dest terminal aggregation**: race done only when all dests terminal; mix of Complete + TimedOut ends the race; one Pending keeps it running.
5. **Alternate-source selection**: exact release-name match chosen; blacklisted/skiplist-denied candidates excluded; the dead source excluded; no-match → null.
6. **Timeout math**: dest times out at `allFilesAt + DestinationCompletionWaitMinutes`; absolute ceiling = hard + completion.

## Edge cases & invariants

- Nuke during await → abort; never search/wait.
- Source migrates but no alternate found → affected dest's missing files can't complete → that dest `TimedOut` (partial); race ends partial.
- Completion markers and `-MISSING-` stubs must **never** inflate `FilesTotal`/`_fileInfos` (keep the existing exclusions; only add a parallel capture).
- Await phase must not hold spread connections between polls (login-cap safety).
- Completion evaluation only after the post-scan reconcile (avoid partial-snapshot false-positives).
- Backward compat: `WaitForDestinationComplete=true` changes behavior (races now wait); intended.
- Don't collide source-migrated handling with `_isNuked` or the 60-min dead-race suppression map.
- Per-dest backoff stays per-dest; only source-side failures are rerouted (no full per-route refactor).

## Out of scope

- UI surfacing of per-dest await state beyond the existing scoreboard (a follow-up if desired).
- Per-`(file,src,dst)` backoff refactor (explicitly avoided).
- Proactive every-cycle source re-probe (chose reactive + confirm).
