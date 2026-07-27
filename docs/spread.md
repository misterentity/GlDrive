---
title: Spread — FXP racing engine
domain: spread
status: active
last-reviewed: 2026-07-27
verified-against:
  - source: read at git 161d19f on 2026-07-27
---

# Spread — FXP racing engine

> **What's in this doc:** the cbftp-style site-to-site FXP race engine — job orchestration, race queueing/dedup, the four FXP transfer modes, scoring, skiplist/section routing, completion detection, and how auto-races are triggered from IRC and new-release polling.
>
> **What's NOT:** the per-server connection pools and login gate the engine borrows from (→ [[ftp]]); the LLM that tunes skiplist/section-mapping config (→ [[ai-agent]]); IRC announce parsing internals (→ [[irc]]); user-initiated single-server downloads (→ [[downloads]]).

## Orchestration

`SpreadManager` (`Spread/SpreadManager.cs:10`) owns the race lifecycle across servers. Each server gets a **dedicated FXP `FtpConnectionPool`** separate from the filesystem/download pools; the pool auto-scales to `max(SpreadPoolSize, maxSlots)`. `StartRace` (`Spread/SpreadManager.cs:217`) enqueues or launches a race; `SpreadJob` (`Spread/SpreadJob.cs:25`) drives one race via `RunAsync` (`Spread/SpreadJob.cs:369`).

### Race queue & dedup

`_raceQueue` (`Spread/SpreadManager.cs:16`) deduplicates by `(section, release)` so the same release can't be queued twice from IRC + notification polling (`Spread/SpreadManager.cs:240`). The dispatcher honors `MaxConcurrentRaces` (`Spread/SpreadManager.cs:548`) — it was previously hardcoded to 1. `StartRace` also dedups the participant list with `Distinct()`.

## FXP transfer modes

`FxpTransfer` (`Spread/FxpTransfer.cs:26`), mode chosen by `FxpModeDetector` (`Spread/FxpModeDetector.cs`). Four modes:

| Mode | Source | Dest |
|---|---|---|
| PASV-PASV | PASV | PASV |
| CPSV-PASV | CPSV | PASV |
| PASV-CPSV | PASV | CPSV |
| Relay | CPSV | CPSV (piped through local memory) |

`SSCN ON` is sent before PASV/PORT for secure FXP data channels. `SendTypeI` verifies the `TYPE I` response before CPSV/PASV to prevent a BNC response-queue desync. When both sites support CPSV, `CpsvPasv` is tried first (`Spread/FxpTransfer.cs:152`).

**Relay route memory:** Relay's per-file direct-transfer probe used to poison both connections on every *successful* transfer. `_relayOnlyRoutes` remembers a failed direct route for `RelayRouteRetry` = 6h (`Spread/FxpTransfer.cs:41`) so it isn't re-probed each file.

Failed transfers **poison** their connections (GnuTLS corruption) — see [[ftp#connection-pool]].

## Login budget

`maxConcurrentRaces` must be ≤ the source's spread-usable logins, or the race starves the source (BNC cooldown). The FXP transfer cap per source is the login gate's reserved permit count. `ExecuteTransfer` borrows both connections via `Task.WhenAll` + `IsCompletedSuccessfully` extraction so a one-sided failure doesn't orphan the peer connection, with a 30s borrow timeout to prevent slot leaks. See [[ftp#login-gate]].

## Scoring and routing

- `SpreadScorer` (`Spread/SpreadScorer.cs`) scores 0–65535 (SFV priority, file size, route speed, site priority, ownership).
- `SkiplistEvaluator` (`Spread/SkiplistEvaluator.cs`) applies cascading allow/deny rules; auto-race drops per-site denies rather than aborting the whole race.
- `SectionMapper` (`Spread/SectionMapper.cs`) resolves IRC section → remote folder before the fuzzy fallback, so learned [[ai-agent]] mappings take effect.
- `SectionBlacklistStore` (`Spread/SectionBlacklistStore.cs`) — dirscript denials are **release-scoped, never section-blacklisted** (blacklisting them once soft-locked a whole section).

## Completion detection

`CompletionDetector` (`Spread/CompletionDetector.cs`) decides when a race is done. Two load-bearing gotchas:

- **`-MISSING-` placeholders are inverse signals.** When zipscript validates an SFV and a file is absent, glftpd drops a 0-byte `-MISSING-<name>` stub. These mean the site *lacks* the file — `SpreadJob.IsMissingPlaceholder` filters `-MISSING-*` / `*.missing` / 0-byte `-*` stubs before counting owned files. Counting them caused false 100% completion.
- **Completion markers** must reject glftpd's `[ Incomplete ]` / `NN% Complete` bar text; a bare `"COMPLETE"` default matched both and ended races early.

`ScanSites` reconciles `FilesTotal` across every site against the final `_fileInfos.Count` each cycle so sites processed early don't freeze with a partial snapshot.

## Auto-race triggers

- `NewReleaseMonitor` (→ [[services]]) polls `/recent/` categories and passes source server + path.
- `IrcAnnounceListener` (`Spread/IrcAnnounceListener.cs`) is registered whenever `SpreadManager` exists, so the built-in `[ NEW ] in [ section ] Release` verbose pattern works without user rules; it passes the source server id. Falls back to `SpreadConfig.AutoRaceOnNotification` for the default autoRace flag. Announce parsing lives in `IrcPatternDetector` (`Spread/IrcPatternDetector.cs`) — see [[irc]].

`RaceHistoryStore` (`Spread/RaceHistoryStore.cs`) persists results to `race-history.json`.

## AI-assisted rule setup

`OpenRouterClient` (`Spread/OpenRouterClient.cs`) and `SiteRulesParser` (`Spread/SiteRulesParser.cs`) power the "AI Setup" button that infers skiplist/sections/section-mappings from a site's `SITE RULES` text. The distinct daily self-tuning loop is [[ai-agent]].
