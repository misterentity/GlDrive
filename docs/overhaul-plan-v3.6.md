# GlDrive Racing/Downloads Overhaul — Master Implementation Plan (v3.6)

> Synthesis of 7 workstream design specs into one sequenced, conflict-aware plan.
> Repo convention: **one atomic commit + release per phase** (`git commit` → `git push` → `powershell -File installer/release.ps1`).
> MANDATORY before every commit: `git status --short | grep -v "^??"` — confirm ONLY intended files show `M`; if any `D ` entries appear (OneDrive index corruption), `git reset --mixed HEAD` and re-stage by name.

---

## 1. Executive Summary

**The through-line: an account-wide live-login gate is the root-cause fix.** Every recurring failure mode in the racing/downloads stack traces back to one thing — multiple independent subsystems (main WinFsp pool, spread FXP pool, ghost-kill, IRC SITE INVITE, downloads, search, monitors, media player) each open FTP logins to the *same glftpd account* with no shared ceiling. The BNC enforces a per-account login cap; when the subsystems collectively exceed it, glftpd returns `530 restricted to N simultaneous logins`, connections get poisoned, the per-pool BNC cooldown trips (~2h), and the whole site goes dark from self-contention.

Everything else in this overhaul is either (a) **enabled by** that gate, (b) a **cost-reducer that lowers login pressure**, or (c) an **isolated robustness/safety win** that can land in parallel.

What changes and why:

| Workstream | What it does | Why (root-cause link) |
|---|---|---|
| **account-login-gate** | Process-wide `ServerLoginGate` per account (host:port:username), shared across all pools via a registry. Caps total LIVE logins. | THE foundation. Without it, every other pool-adding change re-creates the 530 storm. |
| **downloads-overhaul** | Gives downloads their own pool (off the WinFsp main pool) + per-server `DownloadGate` + `DiskReservation`; releases network slot before SFV/extract. | Adds logins → REQUIRES the login gate to stay under cap. Keeps the mounted drive responsive during downloads. |
| **poison-attribution** | Poisons only the side that actually died on a failed FXP transfer (not the blameless peer). | Each needless poison burns a login + reconnect + feeds cooldown. Halving wasted poisons directly relieves login pressure. |
| **autorace-feasibility-precheck** | FTP-free structural gate at top of `TryAutoRace`; kills 339 wasted auto-race spin-ups/day. | Each wasted spin-up borrows connections/logins. Killing them removes login pressure on the announce hot path. |
| **findbesttransfer-refactor** | Extracts the 230-line scheduling loop into a pure, testable `CandidateFilter` over an immutable snapshot. Behavior-preserving. | Makes the spread scheduler testable so the poison/credit/cap logic that protects logins is actually covered. |
| **gnutls-reflection-guardrail** | Startup self-check that fails loud if a FluentFTP/GnuTLS package bump silently breaks the private-member reflection that prevents native crashes. | Protects the native-crash avoidance the whole pool/quarantine machinery depends on. Fully isolated. |
| **download-robustness** | Two surgical `DownloadManager` fixes: observe faulted tasks; idempotent generation-token retry scheme. | Hardening for the downloads-overhaul edits; prevents lost/double-enqueued retries across Stop/Start. |

**Net effect:** total live logins to any one account can never exceed the configured cap, the mounted drive stays responsive during heavy races/downloads, failed transfers stop punishing innocent peers, and the announce path stops wasting hundreds of connection attempts a day — all on a refactored, finally-testable scheduler protected by a loud guardrail against the native-crash regression.

---

## 2. Dependency DAG (implementation order)

Derived from each spec's `dependsOn`. Workstreams in the same phase have **no inter-workstream dependency**, but may still be forced serial by **file conflicts** (see §3). "Parallelizable" below means *dependency-free*; the conflict matrix is the final arbiter of whether they can literally run on separate branches.

```
PHASE 0  (no deps, fully isolated, zero file overlap with anything heavy)
  ├── gnutls-reflection-guardrail        (deps: none)
  └── autorace-feasibility-precheck      (deps: none*; soft only)

PHASE 1  (FOUNDATION — everything downstream needs the gate type)
  └── account-login-gate                 (deps: none)

PHASE 2  (consumes the login gate; large)
  └── downloads-overhaul                 (deps: account-login-gate)
        └── download-robustness          (deps: downloads-overhaul — SAME FILE)

PHASE 3  (SpreadJob-heavy; serialized against each other)
  ├── poison-attribution                 (deps: findbest soft-orders first)
  └── findbesttransfer-refactor          (deps: poison-attribution soft-orders first)
        ^^^ MUTUAL soft-dependency; pick a fixed order (see §4 Phase 3)
```

*`autorace-feasibility-precheck` lists a *soft* dep on account-login-accounting (only if `GetConnectedServerIds`/pool keying changes shape) and on whoever owns `_recentlyDeadRaces`. Neither is a hard ordering — it touches only `SpreadManager.cs`, which no other workstream edits, so it is safe in Phase 0.

**Phase ordering rationale:**
- **Phase 0 first** — isolated, low-risk, conflict-free wins that de-risk later phases (the guardrail protects the native-crash surface every later pool edit relies on; the precheck immediately cuts login pressure before the gate even lands).
- **Phase 1 = the gate** — the root-cause foundation. It must exist (the `IAccountLoginGate` type + registry) before downloads can correctly enforce the cap.
- **Phase 2 = downloads** — depends on the gate; download-robustness is glued onto the same file so it is a sub-step, not parallel.
- **Phase 3 = spread scheduler** — the two SpreadJob.cs workstreams are textually disjoint but conceptually coupled on shared state; serialize them.

---

## 3. File-Conflict Matrix

Source file → workstreams that touch it. **Any file touched by >1 workstream = SERIALIZE** (those workstreams cannot share a parallel branch without a 3-way merge; do them in the phase order above and rebase).

| File | Workstreams touching it | Verdict |
|---|---|---|
| `src/GlDrive/Ftp/ServerLoginGate.cs` | login-gate (NEW) | single |
| `src/GlDrive/Ftp/FtpConnectionPool.cs` | **login-gate**, **gnutls-reflection-guardrail** | **SERIALIZE** ⚠️ |
| `src/GlDrive/Services/MountService.cs` | **login-gate**, **downloads-overhaul** | **SERIALIZE** ⚠️ |
| `src/GlDrive/Spread/SpreadManager.cs` | **login-gate**, **autorace-feasibility-precheck** | **SERIALIZE** ⚠️ |
| `src/GlDrive/Config/AppConfig.cs` | **login-gate** (PoolConfig), **downloads-overhaul** (DownloadConfig) | **SERIALIZE** ⚠️ (disjoint regions, but same file) |
| `src/GlDrive/Spread/SpreadJob.cs` | **findbesttransfer-refactor** (FindBestTransfer ~1271-1526), **poison-attribution** (ExecuteTransfer ~1680-1806 + helper ~1839) | **SERIALIZE** ⚠️ (disjoint line ranges, same file) |
| `src/GlDrive/Downloads/DownloadManager.cs` | **downloads-overhaul**, **download-robustness** | **SERIALIZE** ⚠️ |
| `src/GlDrive/Ftp/StreamingDownloader.cs` | downloads-overhaul | single |
| `src/GlDrive/Downloads/DownloadGate.cs` | downloads-overhaul (NEW) | single |
| `src/GlDrive/Downloads/DiskReservation.cs` | downloads-overhaul (NEW) | single |
| `src/GlDrive/Ftp/GnuTlsReflectionGuard.cs` | gnutls-guardrail (NEW) | single |
| `src/GlDrive/App.xaml.cs` | gnutls-guardrail | single |
| `src/GlDrive/GlDrive.csproj` | gnutls-guardrail | single |
| `src/GlDrive/Spread/FxpTransfer.cs` | poison-attribution | single |
| `src/GlDrive/Spread/CandidateFilter.cs` | findbest-refactor (NEW) | single |
| Test files (`*.Tests.cs`) | each workstream adds its own NEW test file | single each |

**Explicitly called-out collisions (the four the brief demanded):**

1. **`SpreadJob.cs` — findbest + poison.** Disjoint line ranges (FindBestTransfer vs ExecuteTransfer+helper), BUT they share the **scheduling state dictionaries** (`_destFailureCount`, `_destRetryAt`, `_sourceCreditDenied`, `_destDirscriptDenied`, `_failureCounts`). Poison rewrites the WRITE side; findbest snapshots the READ side. **Land poison-attribution FIRST**, then findbest snapshots the final post-poison field shape. SERIALIZE.

2. **`DownloadManager.cs` — downloads-overhaul + download-robustness.** Both rewrite `ProcessLoop`/`ProcessItem`/the retry block — head-on index-shifting conflict. **Land download-robustness FIRST as the small isolated patch, then downloads-overhaul rebases onto it** (or fold robustness into the overhaul PR). Do NOT branch in parallel. SERIALIZE.

3. **`MountService.cs` + `FtpConnectionPool.cs` — login-gate + downloads.**
   - `MountService.cs`: login-gate adds the gate to the main pool (Mount block); downloads adds the second `_downloadPool`. Same constructor/cleanup region. SERIALIZE — login-gate first (Phase 1), downloads rebases (Phase 2).
   - `FtpConnectionPool.cs`: login-gate edits the top/middle (ctor, connect sites, quarantine, dispose); gnutls-guardrail edits ONLY the two bottom reflection helpers (`IsGnuTlsHealthy` ~678-728, `NeutralizeGnuTls` ~738-802). Disjoint, but same file. **Land gnutls-guardrail (Phase 0) BEFORE login-gate (Phase 1)** so the larger pool edit rebases onto a clean bottom-of-file. SERIALIZE.

**Parallelism truth:** Within Phase 0, gnutls-guardrail and autorace-precheck touch *different* files (`FtpConnectionPool.cs`+`App.xaml.cs`+`csproj` vs `SpreadManager.cs`) — **genuinely parallel.** Everything else in the DAG is serialized by file overlap. There is effectively NO cross-phase parallelism beyond Phase 0.

---

## 4. Sequenced Phases

Each phase = one branch off `master`, one verification gate, one atomic commit, one release. Bump `<Version>` in `src/GlDrive/GlDrive.csproj` per phase.

---

### PHASE 0 — Isolated foundations (guardrail + autorace precheck)
**Workstreams:** `gnutls-reflection-guardrail`, `autorace-feasibility-precheck`
**Parallelize?** YES — different files, no shared state. Can be two branches or one combined commit. (Recommend two separate commits/releases for clean rollback granularity, but they may be batched.)

**File changes — gnutls-reflection-guardrail:**
- NEW `src/GlDrive/Ftp/GnuTlsReflectionGuard.cs` — `internal static` class: declarative `Map` of reflected members (`FtpSocketStream.m_customStream`/`m_socket`, `GnuTlsStream.BaseStream`, `GnuTlsInternalStream.IsSessionUsable` + `<IsSessionUsable>k__BackingField`, `_session`/`session`), `Resolve()` resolver populating cached `MemberInfo`, `VerifyOrFail(fatalSink, userSurface)` one-time startup self-check. OR-groups for the `_session`/`session` and property-or-backing-field pairs.
- `src/GlDrive/Ftp/FtpConnectionPool.cs` — `IsGnuTlsHealthy` (~678-728) and `NeutralizeGnuTls` (~738-802): mechanical substitution of inline string-literal reflection for `GnuTlsReflectionGuard.*` cached members. **Confine edits to these two bottom helpers** (login-gate touches the rest later).
- `src/GlDrive/App.xaml.cs` — `OnStartup` after version log (~line 174): single `GnuTlsReflectionGuard.VerifyOrFail(...)` call (Log.Fatal + MessageBox; do NOT Shutdown — see open Q).
- `src/GlDrive/GlDrive.csproj` — pin `FluentFTP` → `[53.0.2]`, `FluentFTP.GnuTLS` → `[1.0.38]` (exact-version brackets) + numbered upgrade-checklist XML comment.
- NEW `src/GlDrive.Tests/GnuTlsReflectionGuardTests.cs` — `Resolve()` returns empty `missing`; all cached members non-null.

**File changes — autorace-feasibility-precheck:**
- `src/GlDrive/Spread/SpreadManager.cs` only:
  - Fields near `_recentlyDeadRaces` (~48): per-section negative cache `ConcurrentDictionary<string,(DateTime expiry,int eligibleCount,string detail)>` (OrdinalIgnoreCase) + `SectionInfeasibleTtl=10min`, `AutoRaceInfeasibleTtl=30min`.
  - NEW private `EvaluateStructuralFeasibility(category, serverIds)` above `TryAutoRace` (~493): in-memory, FTP-free; skip blacklisted (`_blacklist.IsBlacklisted`) + non-section-capable (`SectionMapper.HasSectionFor`); feasible when ≥2. Strict SUPERSET of the async gate (comment-documented).
  - `TryAutoRace` between the `<2` guard (513-517) and `Task.Run` (522): recently-dead check → per-section cache → evaluate → on infeasible park `_recentlyDeadRaces` with reason `autorace-infeasible`, fire `AutoRaceAttempted`, return (no Task.Run).
  - `ClassifyDeadRace` (~954): additive branch for `autorace-infeasible` → `AutoRaceInfeasibleTtl`.
  - NEW `src/GlDrive.Tests/AutoRaceFeasibilityTests.cs`.

**Verification gate:**
- `dotnet build src/GlDrive/GlDrive.csproj` (win-x64) — clean; bracket-pinned packages still restore against lock file.
- `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj` — 102 existing + new guard/feasibility tests green.
- Manual runtime: launch → confirm version line then NO "TLS crash-guard broken" Fatal/MessageBox; mount a server, confirm quarantine churn logs unchanged. For precheck: a mono-section setup logs exactly ONE "structurally-infeasible" Information line per section-release pair, no per-poll re-fires, no spread Borrow for the suppressed release.
- **Commit boundary:** commit + push + release each (or batched). Version bump.

**Rollback:** Both are additive/isolated. Revert the commit; guardrail removal restores inline reflection (no behavior change); precheck removal restores the old (wasteful but correct) async-path bail.

---

### PHASE 1 — Account-wide login gate (FOUNDATION)
**Workstream:** `account-login-gate`
**Parallelize?** NO — solo phase. Must land before downloads.

**File changes:**
- NEW `src/GlDrive/Ftp/ServerLoginGate.cs` — `ServerLoginGate` (SemaphoreSlim(initial, max); `TryAcquireAsync`, guarded `Release`, shrink-only `TightenTo`, `Held`/`Limit`) + `ServerLoginGateRegistry` (static `ConcurrentDictionary<accountKey, gate>`, `GetOrCreate(host,port,user,cap,headroom)`, key = `host.ToLower:port:user.ToLower`). Expose an `IAccountLoginGate` interface here (downloads consumes it as a nullable dep).
- `src/GlDrive/Ftp/FtpConnectionPool.cs`:
  - Fields + SECOND ctor overload `(factory, maxSize, ServerLoginGate? gate)`; **KEEP the 2-arg ctor** (102 tests compile, gate=null no-op). Add `int _permitsHeld`.
  - `AcquireAndConnect(ct)` choke point wrapping all 5 `_factory.CreateAndConnect` call sites (293,330,401,498,550) + Initialize/Reinitialize seed connects (304-306, 336-338). Acquire permit → on failure release + rethrow.
  - `ReleasePermit()` (clamp ≥0, log-assert on under-release) called in `QuarantineDecCreated` (628-670) + `Discard` (601-606) — the single teardown funnel.
  - `Return` healthy path: NO release (idle pooled conn keeps its login) — one-line comment.
  - `DisposeAsync` (840-854): after drain, `Interlocked.Exchange(_permitsHeld,0)` releases.
  - LoginLimitObserved (450): self-tighten `_loginGate?.TightenTo(observed-headroom)`.
- `src/GlDrive/Services/MountService.cs` — Mount (70-90): `ServerLoginGateRegistry.GetOrCreate(...)` → pass to `new FtpConnectionPool(_factory, mainPoolSize, gate)`. Keep the `Math.Min(mainPoolSize,2)` heuristic (now redundant safety).
- `src/GlDrive/Spread/SpreadManager.cs` — InitializePool (104-167) + ReinitDeadPools replacement (445-455): resolve the SAME gate from the registry for the spread pool → `new FtpConnectionPool(factory, poolSize, gate)`. In LoginLimitObserved handler (139-148) also `gate.TightenTo(safe)`. **(This is the linchpin: main + spread pools to one account now share one gate.)**
- `src/GlDrive/Config/AppConfig.cs` — PoolConfig (79-85): `LoginCap=4`, `LoginHeadroom=1`.
- NEW `src/GlDrive.Tests/ServerLoginGateTests.cs` — see §6.

**Verification gate:**
- Build (win-x64) — clean. `dotnet test` — 2-arg-ctor tests still pass (gate=null no-op) + new gate tests green.
- Manual runtime (CRITICAL): mount a server WITH spread configured against a real BNC; trigger an auto-race + browse the drive + let IRC SITE INVITE fire **simultaneously**. Watch logs for ZERO self-inflicted `530 restricted to N simultaneous logins`; confirm concurrent logins observed at the BNC ≤ `LoginCap`. Then set `LoginCap` higher than the real cap, force a 530, confirm the gate **self-tightens** and stops over-subscribing.
- **Commit boundary:** one atomic commit + push + release. Version bump.

**Rollback:** Revert. Because the gate is opt-in via the 3-arg ctor and registry wiring, reverting MountService/SpreadManager wiring (passing `null`/old ctor) restores exact prior behavior. The new file can stay (dead code) or be removed.

---

### PHASE 2 — Downloads overhaul + robustness
**Workstreams:** `downloads-overhaul`, then `download-robustness`
**Parallelize?** NO — both rewrite `DownloadManager.cs`. **Land download-robustness FIRST (small isolated patch), then downloads-overhaul rebases onto it.** (Alternatively fold robustness into the same PR. Either way: single serial sequence on this file.)

**Sub-step 2a — download-robustness (DownloadManager.cs only):**
- Fields (15-22): `int _generation`, `List<Task> _retryTasks`, `object _retryLock`.
- `Start()`: `Interlocked.Increment(ref _generation)` BEFORE re-enqueue; route re-enqueue through new `EnqueueForProcessing(item, signal)`.
- NEW `EnqueueForProcessing(item, signal)` (dedup by `item.Id` under `_queueLock`); route Enqueue/Retry/Start/retry through it.
- `ProcessLoop` (200-246): replace `tasks.RemoveAll(t=>t.IsCompleted)` with `ObserveAndPrune(tasks)` (logs faults, marks `t.Exception` observed); call once more before final `WhenAll` (wrap in try/catch).
- Retry block (451-479): capture `gen`; build tracked `ScheduleRetryAsync(item, delay, gen)`; self-removing continuation.
- NEW `ScheduleRetryAsync` — generation-guarded, no enqueue if `_generation != gen`.
- `StopAsync` (55-78): snapshot + `WhenAll(retry).WaitAsync(1s)` before dispose. `Stop()` (80-91): clear `_retryTasks` under lock; generation guard makes survivors no-op.

**Sub-step 2b — downloads-overhaul (rebased onto 2a):**
- `src/GlDrive/Services/MountService.cs` — add `_downloadPool` (second pool from same `_factory`, `DownloadPoolSize` clamped); `Initialize` + `ConfigureHealth`; pass to `StreamingDownloader` + `new FtpOperations(_downloadPool)` for `DownloadManager`. Pass `DownloadGate`, `DiskReservation`, and the `IAccountLoginGate` from Phase 1's registry. Dispose `_downloadPool` in both Cleanup paths (after manager stop, before `_pool`).
- `src/GlDrive/Downloads/DownloadManager.cs` — ctor adds `DownloadGate`, `DiskReservation`, `IAccountLoginGate?`. Split `ProcessItem` → `RunNetworkPhase` (gated: DownloadGate + login gate + disk reservation) + `RunPostProcessPhase` (ungated SFV/extract). Intra-release bounded-parallel fan-out (`MaxFilesPerReleaseParallel`), Interlocked progress aggregation, cooldown-aware re-queue (15s, no RetryCount bump).
- NEW `src/GlDrive/Downloads/DownloadGate.cs` — per-server slot gate (ServerGate-modeled, `TightenTo`, IAsyncDisposable handle).
- NEW `src/GlDrive/Downloads/DiskReservation.cs` — process-wide reservation keyed by drive root; `TryReserve`/`Release`/`Adjust`; 64MB headroom.
- `src/GlDrive/Ftp/StreamingDownloader.cs` — `IsPoolInCooldown` + `Pool` accessors.
- `src/GlDrive/Config/AppConfig.cs` — DownloadConfig: `DownloadPoolSize=2`, `DownloadKeepaliveSeconds=30`, `ValidateDownloadConnOnBorrow=true`, `MaxFilesPerReleaseParallel=2`, `DiskReserveHeadroomMb=64`.
- NEW tests: `DiskReservationTests.cs`, `DownloadGateTests.cs`, parallelism + cooldown-defer + retry-idempotency tests.

**Verification gate:**
- Build (win-x64). `dotnet test` — full suite, re-run any known-flaky once.
- Manual runtime: enqueue a multi-file release → confirm (a) a separate download pool initialized, (b) the mounted WinFsp drive stays responsive (`ls` the drive) DURING a large download, (c) SFV/extract runs AFTER network slots released (log ordering), (d) a forced/simulated BNC cooldown DEFERS downloads (no error), (e) disk-near-full reports clean "insufficient disk space" with no overcommit across two concurrent releases, (f) **login accounting holds: main(2)+spread(3)+download(2) never exceeds LoginCap at the BNC** — the gate serializes across pools. Robustness: kill a connection mid-transfer → "retry N/Max" once; Unmount/Remount during a retry delay → item resumes exactly once (no double-row, no loss).
- **Commit boundary:** 2a and 2b may be two commits+releases (cleaner rollback) or one. Version bump(s).

**Rollback:** Revert 2b first (restores single-pool downloads), then 2a if needed. New files (`DownloadGate`, `DiskReservation`) become dead code on revert. Critical watch: confirm post-revert downloads still borrow from the main pool (no dangling `_downloadPool` reference).

---

### PHASE 3 — Spread scheduler: poison attribution + FindBestTransfer refactor
**Workstreams:** `poison-attribution`, then `findbesttransfer-refactor`
**Parallelize?** NO — both edit `SpreadJob.cs` and share the scheduling-state dictionaries. **Fixed order: poison-attribution FIRST (rewrites the WRITE side), then findbesttransfer-refactor (snapshots the post-poison READ shape).**

**Sub-step 3a — poison-attribution:**
- `src/GlDrive/Spread/FxpTransfer.cs` — NEW `enum FxpFaultSide { None, Source, Dest, Both }` + `FaultSide` property. Set at each throw site by physical FXP role (mode-aware: PASV-on-dest⇒Dest, PORT-on-src⇒Source, STOR⇒Dest, RETR⇒Source; PasvCpsv swaps PASV⇒Source/PORT⇒Dest). WaitForTransferComplete timeout ⇒ Both. CPSV-fallback catch ⇒ Both (keep existing direct poison). Top-level catch: None ⇒ Both, never overwrite a set Source/Dest. Relay data-loop: **defer to None⇒Both in v1** (do not touch the tuned hot loop); attribute only discrete RETR/STOR reply throws.
- `src/GlDrive/Spread/SpreadJob.cs` — ExecuteTransfer failure branch (1688-1689): replace unconditional both-poison with `ApplyPoisonAttribution(transfer, srcConn, dstConn)`. NEW private static helper near IsMkdError (~1839): switch on FaultSide (Source⇒src only, Dest⇒dst only, Both/None⇒both); Log.Debug which side + why. KEEP both-poison on the three ambiguous catches (OperationCanceled cancel 1764-1765, borrow-timeout 1773-1774, generic 1804-1805) with explanatory comments.
- NEW `src/GlDrive.Tests/FxpFaultAttributionTests.cs`.

**Sub-step 3b — findbesttransfer-refactor (rebased onto 3a's final state shape):**
- NEW `src/GlDrive/Spread/CandidateFilter.cs` — immutable `SchedulingSnapshot` record (all FindBestTransfer inputs, pool-cooldown PRE-RESOLVED so no pool dep), `CandidatePredicates` (one pure named bool per skip, including `SourceCreditDenied` and the post-poison `DestInBackoff`/`DirscriptDenied` shapes from 3a), `CandidateFilter.SelectBest(snap, scorer, rng)` reproducing exact iteration order + coin-flip tie-break, `ServerScheduleInfo`, `CandidateResult`, `FilterStats`. Reuse existing tuple comparers.
- `src/GlDrive/Spread/SpreadJob.cs` — `FindBestTransfer` (1271-1526): build `SchedulingSnapshot` under the existing `_ownershipLock` (copy mutable state into immutable collections inside the lock), resolve `poolsInCooldown` once, then call `SelectBest` OUTSIDE the lock; map `FilterStats` back into the verbatim Log.Debug. Method shrinks ~255→~60 lines, zero behavior change. **Confine the edit to this method body** so the diff doesn't overlap 3a's ExecuteTransfer region.
- NEW `src/GlDrive.Tests/CandidateFilterTests.cs` — ~25-30 pure tests (see §6).

**Verification gate:**
- Build (win-x64). `dotnet test` — 102 + new attribution + ~25-30 candidate-filter tests; MkdFailureClassifierTests unaffected.
- Golden-equivalence: one representative `SchedulingSnapshot` → `SelectBest` returns hand-computed (file,src,dst) + FilterStats (behavior-preservation guard).
- Manual runtime: 2-site race. Force a one-sided STOR-553 (dest at a no-upload section) → logs show ONLY the dest poisoned (`FaultSide=Dest`), source returned clean (no reconnect/extra login). Force RETR-550 credit-exhaustion → only src poisoned, `_sourceCreditDenied` parking still fires. Confirm `FindBestTransfer` logs identical "N files, M candidates. Skipped: owned=.. ..." diagnostics and identical priority order (SFV first, then score) as before. Sustained race → no native GnuTLS crash.
- **Commit boundary:** 3a and 3b as two commits+releases (poison is the higher-value safety fix; ship it first). Version bump(s).

**Rollback:** Revert 3b (restores inline loop; CandidateFilter.cs becomes dead code) — safe because behavior-preserving. Revert 3a (restores unconditional both-poison — the conservative-safe default; never under-poisons). Either revert independently because their SpreadJob.cs regions are disjoint.

---

## 5. Risk Register

| Risk | Phase | Likelihood/Impact | Mitigation | Rollback note |
|---|---|---|---|---|
| **Native GnuTLS crash regression** — a package bump or a reflection edit silently breaks `IsGnuTlsHealthy`/`NeutralizeGnuTls`, returning a poisoned session to the pool → native crash on next borrow. | 0, all | Low / Catastrophic | Land the guardrail FIRST (Phase 0); bracket-pin packages; startup self-check fails loud. Phase 1/3 edits to the pool/poison logic NEVER under-poison ambiguous cases (default Both). | Revert offending phase; guardrail's loud failure tells you immediately if a member vanished. |
| **Login-gate permit leak / accounting drift** — release a permit never acquired, or fail to release one → CurrentCount drifts → deadlock for that account. | 1 | Med / High | Acquire ONLY in `AcquireAndConnect` (single choke point, increments `_permitsHeld`); release ONLY in `QuarantineDecCreated`+`DisposeAsync`; NEVER in healthy Return. `ReleasePermit` clamps ≥0 + log-asserts under-release. Unit tests assert Held never negative + DisposeAsync zeroes it. | Revert Phase 1 (3-arg ctor opt-in → null gate = exact prior behavior). |
| **Login-gate double-release on quarantine-FIFO eviction** — evicting an already-quarantined victim releases twice. | 1 | Med / High | Tie release to the quarantine ACTION (the Add), not the victim's eventual Dispose; eviction path touches only `victim.Dispose()`, never the gate. | Same as above. |
| **Login-gate self-deadlock via headroom=0** — gate equals desired conns AND ghost-kill needs a slot. | 1 | Low / High | `LoginHeadroom=1` default; KillGhosts left UNGATED (transient, bypasses pool) so it can always run. | Raise headroom; or revert. |
| **Download pool starvation / login-cap regression** — the new download pool ADDS logins, re-creating the 530 storm if the gate doesn't serialize across pools. | 2 | Med / High | Hard dep on Phase 1 gate; `DownloadPoolSize=2` + `ValidateDownloadConnOnBorrow=true`; `DownloadGate.TightenTo` wired to LoginLimitObserved as belt-and-braces. Manual gate verifies main+spread+download ≤ cap at the BNC. | Revert Phase 2b → downloads return to main pool. |
| **Download disk-reservation leak** — throw between TryReserve and Release permanently under-reports free space. | 2 | Med / Med | reserve/release in try/finally per file; `Adjust` reconciles actual-vs-reserved on success. | Revert 2b; DiskReservation becomes dead. |
| **Download retry lost/doubled across Stop/Start** — disposed-CTS/signal hazard. | 2a | Med / Med | Generation-token + EnqueueForProcessing dedup by Id; StopAsync drains retry tasks. | Revert 2a (small isolated patch). |
| **Multithreaded progress corruption** — parallel files race `item.DownloadedBytes`. | 2 | Med / Low | Interlocked.Add on completedBytes; atomic recompute. | Lower `MaxFilesPerReleaseParallel` to 1 (config, no code revert). |
| **Under-poisoning a corrupt connection** — narrowing FaultSide to one side returns a corrupt GnuTLS session → native crash. | 3a | Low / Catastrophic | Narrow ONLY on clean protocol-reply 5xx throws (STOR/RETR/PASV/PORT) where no data flowed; ALL data-channel/GnuTLS/ambiguous cases default Both. | Revert 3a → unconditional both-poison (the safe over-poison default). |
| **Behavior drift in FindBestTransfer refactor** — subtle iteration/tie-break/counter change alters scheduling. | 3b | Med / Med | Port line-by-line; inject rng for deterministic tie-break tests; golden-equivalence test; assert Log.Debug field mapping. | Revert 3b → inline loop restored (behavior-preserving extract). |
| **OneDrive git-index corruption** — staging 130+ phantom deletions. | ALL | Med / High | MANDATORY `git status --short \| grep -v "^??"` pre-commit; `git reset --mixed HEAD` + re-stage by name on any `D `. New files are append-only (low risk). | N/A — pre-commit gate prevents the bad commit. |
| **Merge collision on shared files** — parallel branches on SpreadJob/DownloadManager/MountService/FtpConnectionPool/AppConfig. | 1,2,3 | High if ignored / High | SERIALIZE per §3; never branch two conflicting workstreams in parallel; rebase in phase order. | N/A — sequencing prevents it. |

---

## 6. Test Strategy

### New unit tests (the two priority suites the brief flagged)

**`ServerLoginGateTests.cs` (Phase 1) — permit accounting is the #1 correctness concern:**
- Registry returns SAME instance for identical host:port:username (case-insensitive); DIFFERENT for different accounts.
- Acquire up to Limit succeeds; (Limit+1)th `TryAcquireAsync` with short timeout returns false; one Release re-enables next acquire; `Held` = acquired − released.
- `TightenTo(2)` from 3 absorbs one permit; caps at 2; idempotent when newLimit≥current; never throws `SemaphoreFullException` at max sizing.
- Pool with stub factory + real gate(limit=2): Borrow×2 ⇒ Held==2; Return both ⇒ Held STILL 2 (idle pooled conns retain logins); 3rd Borrow reuses pooled conn, NO new permit.
- poison+Discard one ⇒ exactly one release (Held −1); DisposeAsync releases all (Held==0); Held never negative (no over-release).
- TWO pools sharing one gate(limit=3): A takes 2, B can take only 1 more; B's 2nd concurrent Borrow throws/blocks (account-wide cap proven across pools).
- `AcquireAndConnect` releases the permit when `CreateAndConnect` throws (no leak on connect failure).

**`CandidateFilterTests.cs` (Phase 3b) — finally gives the spread scheduler real coverage:**
- Per-predicate boundaries: `PairRetryCapped` at 3 vs 4; `FileRetryCapped` summed across routes at 6 vs 7; `DestInBackoff` now< vs now≥ until + DateTime.MaxValue-dropped; `DirscriptDenied` prefix StartsWith; `SfvFirstBlocked` lets .sfv/.nfo through; `UniqueSimilar` ext/basename hit; slots at limit.
- Pipeline: `SelectBest` picks highest score; deterministic tie-break via injected rng; empty fileInfos ⇒ null; SFV-needing-dest forces sfv selection; owned file never re-shipped; credit-denied source excluded for all candidates; cooldown src/dest excluded.
- Golden-equivalence: hand-built snapshot ⇒ assert chosen (file,src,dst) + FilterStats.

**Other new suites (by phase):**
- Phase 0: `GnuTlsReflectionGuardTests.cs` (Resolve empty-missing, all members non-null); `AutoRaceFeasibilityTests.cs` (1-eligible⇒infeasible, ≥2⇒feasible, dedup park, per-section cache hit).
- Phase 2: `DiskReservationTests.cs` (no over-commit under N threads, fake free-space provider); `DownloadGateTests.cs` (block beyond limit, TightenTo, handle dispose); DownloadManager intra-release parallelism / cooldown-defer / retry-idempotency.
- Phase 3a: `FxpFaultAttributionTests.cs` (Source⇒src only, Dest⇒dst only, Both/None⇒both; timeout⇒Both; CPSV-fallback⇒Both; 553-dupe leaves None+returns true no-poison).

### Manual runtime checklist (per phase gate)
1. **P0:** launch → version line, NO "TLS crash-guard broken"; mono-section autorace logs ONE infeasible line, no per-poll re-fires, no Borrow for suppressed release.
2. **P1:** simultaneous auto-race + drive browse + IRC INVITE → ZERO self-inflicted 530; BNC concurrent logins ≤ LoginCap; force-530 ⇒ gate self-tightens.
3. **P2:** multi-file release → separate download pool initialized; drive stays responsive during download; SFV/extract AFTER network release; cooldown defers (no error); disk-full clean message no overcommit; login accounting holds across pools; retry-once + Unmount/Remount-resumes-exactly-once.
4. **P3:** STOR-553 ⇒ only dest poisoned, source clean; RETR-550 ⇒ only src poisoned + parking fires; identical FindBestTransfer diagnostics + priority order; sustained race no native crash.

### Regression
Every phase: `dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj` (102 baseline; re-run known-flaky spread tests once). New tests must be pure/synchronous — no Task.Delay — to avoid joining the flaky set.

---

## 7. Open Questions (decide BEFORE implementation)

**Login gate (Phase 1):**
1. **KillGhosts gating** — leave the transient `!username` connect UNGATED (current design, relies on `LoginHeadroom=1`), or best-effort `TryAcquire(0-timeout)` (skip ghost-kill if no headroom)? Ungated risks momentary cap+1 on sites with zero spare; gating risks deadlock. **Recommend: ungated** + document headroom is what keeps total under cap.
2. **Spread ServerGate fate** — keep it (caps concurrent FXP *transfer pairs*, a different concern the login gate doesn't model) but remove its login-cap `TightenTo` wiring once the login gate is validated? **Recommend: keep ServerGate, drop its TightenTo later** (flag for Phase 3).
3. **configuredCap source** for SiteImporter-imported servers without an explicit `LoginCap` — default 4 (glftpd norm) but some sites are 2/3. Seed from first observed "restricted to N" and persist back to config? (Persistence = separate workstream.)
4. **Interactive-borrow starvation** — should filesystem (WinFsp) borrows get a reserved priority lane over FXP/download borrows when the gate is saturated, to keep the drive letter from hanging during a heavy race? Deferred — note for v2.

**Downloads (Phase 2):**
5. **IAccountLoginGate shape** — exact type/namespace, keyed per-account or per-server? Confirm the combined-handle acquire ordering vs spread's gate ordering to avoid deadlock. (Phase 1 owns this; pin it before Phase 2.)
6. **StreamingDownloader poisoning** — add explicit poison on mid-transfer download failure, or rely on `IsGnuTlsHealthy` gating on next borrow? Adding it widens the StreamingDownloader diff.
7. **DownloadPoolSize/headroom defaults** — confirm main(2)+spread(3)+download(2) vs a 4-login cap: on paper it exceeds, so the login gate MUST serialize across pools. Validate the accounting numbers with the cap owner.
8. **MaxConcurrentDownloads (release-level)** — raise it now that the network slot releases before CPU work, or keep at 1 for predictable release ordering?

**Spread (Phase 3):**
9. **Relay data-loop attribution** — v1 defer (None⇒Both) to avoid touching the tuned hot loop, or attribute read⇒Source/write⇒Dest? **Recommend: defer.**
10. **Borrow-timeout poison** — the successfully-borrowed-but-idle conn was never used; return it CLEAN to save a login, or keep conservative both-poison? **Spec keeps both-poison;** decide if an idle borrowed conn can be trusted.
11. **ErrorMessage-sniffing fallback in ApplyPoisonAttribution** — keep the fragile string fallback for None cases, or strictly map None⇒Both? **Recommend: None⇒Both only** unless telemetry shows many one-sided None cases.
12. **FindBestTransfer snapshot field shapes** — confirm with poison-attribution (lands first) the FINAL semantics of `_destRetryAt` (does DateTime.MaxValue still mean "dropped"?) and `_destDirscriptDenied` (still prefix-StartsWith?) so the snapshot mirrors the post-poison shape.
13. **ServerScheduleInfo carries display Name?** — or keep the name lookup in SpreadJob to keep the snapshot config-free? **Leaning: keep in SpreadJob.**

**Guardrail (Phase 0):**
14. **Hard-pin vs degrade-and-warn** — `VerifyOrFail` keeps the app running after a loud failure (so the user can read logs/downgrade), or call `Shutdown()` (refuse to run without crash protection)? Single-line decision at the App.xaml.cs insertion point.
15. **FtpSocketStream namespace** in FluentFTP 53.0.2 — confirm at impl time (likely `FluentFTP.Streams.FtpSocketStream`); verify the SOCKS5 proxy client path exposes the same `m_customStream`/`m_socket`.

**Autorace precheck (Phase 0):**
16. **Cache invalidation on Mount/Unmount** — immediate invalidation vs 10-min TTL? Immediate is cleaner but expands the touched-file set beyond SpreadManager.cs (OneDrive risk). **Defer unless 10-min staleness bites.**
17. **AutoRaceInfeasibleTtl** — 30 min or match the 10-min section cache?
18. **Source-hint semantics** — should the gate consult `sourceServerId` (announcing site may hold the release even without the section), or require two section-capable servers (current)? Confirm.
