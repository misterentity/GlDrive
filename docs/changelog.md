---
vault-doctor: skip
---

# Changelog

Notable versions; see `git log` for the full history. Commit messages in this project do
not follow conventional-commit syntax — versions are split into **Features**, **Fixes**,
**Security**, and **Refactors / infrastructure** by reading the message text.

> The middle of the history (v1.45 → v3.10.24) is not yet curated here — see `git log` for
> that range. The section below covers the recent v3.10 reliability arc; the v1.44 section
> and earlier follow it.

## v3.10 — AI self-tuning revival, extractor & auto-update reliability (2026-07)

### Features
- **v3.10.53** — `NeverSource`, the mirror of `DownloadOnly`. `DownloadOnly` bars a site
  from being a DESTINATION; there was no way to say the opposite — "seed this site, never
  leech from it". A site can enter the source role by three independent routes, so the flag
  is enforced in all three or it leaks in through the side door: Phase 1 discovery (the site
  simply has the release — its path is still recorded so it can receive, it just isn't
  counted as a source), `FindBestTransfer`'s per-file route scoring (with a `neverSource=`
  counter in the skip summary so a stalled race says why instead of only "0 candidates"),
  and `SpreadManager`'s mid-race alternate-source failover. Editable per site in the server
  dialog.
- **v3.10.52** — a loopback control API and a per-race detail expander. Starting or
  inspecting a race previously required the WPF Dashboard, and driving the tray icon
  through UI Automation is unreliable (the notification-area flyout breaks the element
  tree between calls), so there was no scriptable way to re-run a race that had failed.
  `ControlApi` binds **only** `http://127.0.0.1:{port}/`, requires
  `Authorization: Bearer <token>` compared in fixed time, additionally refuses any request
  whose remote endpoint is not loopback, and is **off by default** with the token minted on
  first enable and never logged. Endpoints: `GET /status`, `GET /sections`, `GET /races`,
  `GET /races/{id}` (full detail), `GET /history?limit=N`, `POST /races {section,release}`,
  `POST /races/{id}/stop`. The same `SpreadJob.GetDetail()` snapshot backs a new expander on
  each card in the Spread tab: every file with its owners, who's still missing it, what's in
  flight and to where, per-route failure counts, plus per-site resolved path, backoff state,
  MKD/dirscript denials and credit denials. The snapshot copies several dictionaries under
  the job's locks, so it is taken **only while a card is expanded** rather than every tick
  for every card.

### Security
- **v3.10.60** — a caller could erase the log. `POST /races` had no length validation, so a
  2,000,000-character `section` was accepted (`202`), started a real race, and was written
  to `gldrive-{date}.log` as a single 2,000,172-byte line. The log rolls at 10 MB keeping 3
  files, so about five such calls erase every trace of what the app was doing — the same
  evidence-destruction that already cost this project a day of history twice (v3.10.47,
  v3.10.54), except with a caller holding the pen. `section` and `release` are now bounded
  (128 / 512 chars) *before* they reach either the race engine or the logger, and the
  ordering is pinned by test. Request bodies are capped at 64 KB and refused with `413`
  rather than parsed truncated and blamed on the caller's JSON — `ReadBodyAsync` used an
  unbounded `ReadToEndAsync`, and a 20 MB body took the app from 238 MB to 585 MB resident.
  Found by driving the live API against the running app, not by reading code.
- **v3.10.58** — the IRC trace redacted exactly one command. `IrcClient` special-cased
  `PASS`, so `JOIN #chan <key>` wrote the FiSH channel key — the key deliberately kept
  DPAPI-encrypted in `fish-keys-{serverId}.json` — as cleartext into the log, and the
  inbound trace had no redaction at all while the server echoes the key back in `MODE` and
  `324 RPL_CHANNELMODEIS`. Enabling Verbose to diagnose an IRC fault would have dumped every
  channel key and services password to disk. `IrcLineRedactor` now keys on *which parameter
  of a command is a credential* rather than a list of commands observed to leak — the same
  enumeration-vs-defining-property mistake that left the zipscript sidecar filter broken for
  a month (v3.10.44). Separately, `GET /sections` returned `perServer` as the whole
  `SpreadSite.Sections` map, handing every site's real remote directory layout to anything
  holding the token; it returns keys only, which is all the endpoint's stated purpose needs.
  Both found by mapping subsystem surfaces for a feature, not from any symptom.

### Fixes
- **v3.10.81** — aggregate spread-scan logging now preserves the distinction between
  login contention and genuine faults. Previously, an all-contention scan batch first
  logged each deferral correctly at information level and then contradicted itself with
  `ALL scans failed` at warning level; the next cycle normally succeeded. Duplicate
  announces discovered after a release already exists on every candidate site are also
  recorded as informational no-work outcomes instead of failed-race warnings. Real pool,
  I/O, transport, and policy failures retain warning-level evidence.
- **v3.10.80** — telemetry JSONL files are now created as UTF-8 without a BOM, so their
  first row is valid JSON for raw-byte readers as well as `StreamReader`. The digester now
  counts and warns about malformed rows instead of silently dropping evidence at Debug.
- **v3.10.79** — spread ownership now requires an exact file-size match and ignores
  destination files while their transfer is in flight. A relay reset could leave a
  same-named truncated file; filename-only ownership then suppressed the retry and held
  the race for the full 10-minute completion-marker wait. Later full-size observations
  also revoke any smaller copy provisionally seen first. Startup mount failures that are
  queued for recovery are logged as warnings rather than false application errors, and
  unchanged steady-state remount summaries are informational while the opening, changed,
  and hourly failures retain warning-level stack evidence.
- **v3.10.78** — auto-race destination preflight now uses the live spread-pool registry,
  not configuration eligibility alone. A configured server whose FTP endpoint had been
  unreachable for eleven days still counted as a viable receiver, so 128 impossible jobs
  started over three days and later blamed the remaining site's affiliation filter. A
  disconnected destination is now reported as such and skipped before consuming a race
  slot; the check fails open during the ambiguous all-disconnected startup window.
- **v3.10.77** — auto-race now performs the same destination-role preflight as the
  race engine before starting a job. A download-only source paired with an affil-blocked
  destination previously produced hundreds of guaranteed `No viable destinations`
  failures; it is now recorded as an informational skip without consuming a race slot or
  adding a failed history row. Windows notification-area failures are also contained at
  the H.NotifyIcon boundary, so a transient Explorer/notification glitch can no longer
  surface as an unhandled UI exception.
- **v3.10.75** — connection creation now obeys the account login gate's effective
  ceiling instead of the pool's larger nominal size. With the production shape
  (`pool max=3`, `priority limit=1`), checking out the sole FXP connection used to
  trigger a doomed second login, wait 30 seconds, and arm login-cap backoff; the pool
  now waits for the live connection to return. The AI agent also preflights the known
  retired `openai/gpt-oss-120b:free` slug to its working replacement, avoiding one 404
  on every restart, and provider error logging now retains only the actionable error
  code/message rather than durable account/request identifiers. The screenshot renderer
  now runs without a watchdog or shared crash-marker lifecycle; previously its clean exit
  deleted the `.running` marker belonging to the live production instance and disabled
  crash recovery until the next normal restart.
- **v3.10.74** — a transient spread-pool handshake failure no longer disables FXP racing
  for that server until the application restarts. Production v3.10.73 showed the main FTP
  mount recovering on its second attempt while the independently initialized spread pool
  timed out once and was then permanently absent from `/status` and auto-race eligibility.
  Failed initializations now retain their factory and enter a single-flight background
  recovery loop using the existing 30-second exponential backoff capped at 5 minutes.
  Recovery is serialized against concurrent initialization, stale attempts cannot publish
  after unmount or configuration replacement, and unmount/shutdown cancels and drains the
  retry. Repeated failures log compact summaries instead of duplicate stack traces.
- **v3.10.61** — three give-up paths that never expired, all the same shape: a decision made
  once and never revisited, on a resource that was still perfectly healthy. **IRC
  invite-only joins:** `#ent` is `+i`, so GlDrive can only enter when the site's announce bot
  sends an `INVITE`. On 2026-08-12 that bot was absent; GlDrive retried three times over
  ~30 s, gave up, and then sat *out* of the channel for 8 h 21 m on a live connection until a
  human invited it by hand — `AutoJoinChannelsAsync` runs only on connect, so "gave up" meant
  gave up for the lifetime of the connection. There is now a standing retry backing off
  5/15/30 minutes and staying there, and it re-issues `SITE INVITE` rather than only
  re-sending `JOIN`, which can never succeed on a `+i` channel by itself. **Invisible
  diagnostics:** every outcome of the invite path went only to the in-app system tab, so
  grepping the log for `SITE INVITE` returned zero hits whether or not it had run and absence
  proved nothing; it now logs. **Silent PM loss:** `SendMessage` bare-returned on a
  disconnected client, so the message was never sent, never echoed locally (the `AddMessage`
  calls sit below that guard) and never logged — a PM simply evaporated. It now returns
  `false` and says so. **Update suppression:** a granted UAC prompt now clears
  `.update-declined`; the auto-install prompt fires around 05:00 when nobody is at the
  machine, gets declined, and the 24 h suppression then also blocked every *later* release,
  so a successful manual tray install still left the next version needing another manual one.
- **v3.10.60** — `POST /races/{id}/stop` answered `200 {"stopped": id}` for a race that never
  existed, because `StopJob` is fire-and-forget with no existence check, while
  `GET /races/{id}` correctly `404`'d the same id. The two endpoints disagreed about reality
  and a script could not tell a real stop from a no-op. Stop now verifies the race exists and
  reports an unknown id the same way the read endpoint does.
- **v3.10.59** — the control API's error responses were inconsistent: the loopback/token
  gate rejections and the router's 404/405 stayed close to a `{error, code, path}` shape
  while every endpoint handler already used the richer `{error, code, detail, path}`
  envelope from `ControlRequest.ErrorAsync`, and the unhandled-exception catch-all in
  `ControlApi.Handle` emitted only `{error}` — no `code`, no `path`, nothing to correlate a
  500 against the request that caused it. The catch-all now uses the same four-field
  envelope as everything else. Two fields that used to be their own top-level property
  folded into `detail` in the process: the 404's `id` (which race wasn't found) and the
  409's `connected` server list, now a comma-joined string rather than an array. The API
  has never been enabled on any machine, so nothing consumes today's shape — this is the
  last release where changing it is free. Also de-duplicated the `JsonSerializerOptions`
  block that had drifted into a byte-for-byte copy in both `ControlApi.cs` and
  `ControlRequest.cs`; there is now exactly one definition (`ControlRequest.Json`).
- **v3.10.51** — the directory scan raided the FXP pool and starved every transfer it was
  scanning for. `ScanSites` falls back to the dedicated spread pool when the main pool's
  borrow times out — but that fallback was unconditional, and a scan re-runs every ~2s
  while an FXP borrow waits up to 30s, so on a login-capped account the scan won the
  permit race every single time. zephyr (`loginCap` 4 = main pool 1 + spread pool 3, zero
  headroom) spent 2026-08-09 emitting **1,393 "main pool exhausted … falling back to
  spread pool", 2,779 FXP borrow timeouts and 1,464 "scan FAILED (both pools
  unavailable)" — against ONE FXP transfer error and TWO MKD failures in the entire day.**
  Both endpoints were healthy; the engine was starving itself. Every destination
  eventually exhausted its `_destRetryAt` backoff ladder and was dropped for the race, so
  releases got an empty directory created on the dest and then `SITE WIPE`d: **321 races,
  4 delivered any files.** `CandidatePredicates.ScanMayBorrowSpreadPool` now lets a scan
  take a spread permit only while one would remain for a transfer — scans are best-effort,
  transfers are the point. The original recovery case (main pool genuinely dead, not
  merely busy) still bypasses the guard. A deliberate yield logs at INF, not the WRN that
  produced 1,464 lines/day of noise. Same fault line as v3.10.43, which fixed the scan
  deadlocking against itself and already observed it "stealing transfer slots".
- **v3.10.50** — the account login gate reserved logins in only ONE direction, so the
  spread pool could take every usable login and leave a site permanently unmountable.
  `ServerLoginGate` capped non-priority (main mount pool) callers at `limit − reserved`
  so FXP always had a permit, but priority callers waited on the *total* semaphore with
  no sub-cap of their own — the exact inverse of the v3.7.2 bug where the main pool
  squatted the permits FXP needed. On 2026-08-07 SYN ran at `usable=3, fxpReserved=2`
  with three live spread logins while its mount retried **226 times**. Priority callers
  now hold a mirror sub-cap of `limit − 1`, so one login is always obtainable by the
  mount. Found alongside it: `TightenTo` computed each sub-cap as `max(0, limit − reserved)`,
  so tightening a reserved gate to 1 drove **both** sub-caps to zero and deadlocked every
  caller — a tighten meant to protect the account instead closed it. Both sub-caps now
  keep at least one obtainable permit.
- **v3.10.50** — v3.10.49's forever-retry wrote two full stack traces per attempt (an ERR
  from `MountService.Mount` and a WRN from `ServerManager.RemountLoop`). With two sites
  down that was **7,244 of 16,084 lines — 45% of the day's log** — restating a condition
  whose first occurrence had already said everything useful, and re-running the v3.10.47
  failure where volume rolls the log and evicts the history needed for the *next*
  diagnosis. `MountService` now logs its failure at Debug (every caller logs it, and only
  the caller knows whether this is attempt 1 or 226), and `RemountLoop` throttles via
  `MountFailureLogPolicy`: full detail on the first 3 attempts, every 12th after that
  (one per hour at the 5-min cap), and always when the error message *changes* — that
  transition being the genuinely diagnostic moment. ERR volume for a down site drops from
  ~200/day to 1.
- **v3.10.49** — a server whose mount failed at startup was never retried, so one bad
  moment cost the whole process its FTP stack. `ServerManager.MountAll` caught the
  failure, logged an error and moved on; `MountServer`'s own catch disposed the
  `MountService` — and the `ConnectionMonitor` that would have reconnected lives *inside*
  that service. On 2026-08-06 the box resumed from sleep at 09:04 and GlDrive started at
  09:07 before the network was up: all three servers failed inside 200 ms with
  `SocketException 10065 (unreachable host)` / `11001 (No such host is known)`, and the
  app then did **zero FTP work for the following 10 hours** — no spread scan, no pool, no
  remount. IRC recovered by itself over the same window (398 failures, 31 successful
  reconnects), and that asymmetry is what localized the fault: IRC has a reconnect loop,
  mounting had none. Failed mounts are now queued and retried on a capped exponential
  backoff (30s → 60s → 120s → 240s → pinned at 5 min, `MountRetryPolicy`) until they
  succeed or the server stops being eligible — the cap keeps a BNC clear of its ~2 h
  rate-limit cooldown. The retry never gives up permanently: an unreachable network is
  precisely the condition that resolves on its own.
- **v3.10.49** — the watchdog asserted `[FTL] ... crashed` from the mere absence of a
  clean-exit marker, and its restart was unobservable. On 2026-08-05 07:39:13 it logged
  `Process 10176 crashed — unknown (no matching event log entry found)` **41 seconds
  before** Kernel-Power logged `The system is entering sleep`, with no 1026/1000 event
  anywhere — an OS sleep, not a crash. It now reports an unidentified exit as a `WRN`
  naming the actual evidence, and reserves `FTL` for exits with a real event-log
  signature. The restart also moved out of the shared bare `catch {}` into its own block
  that logs the new PID on success and an `ERR` on failure — previously a restart that
  never happened was indistinguishable from one that did.

- **v3.10.48** — the FXP borrow-timeout warning asserted one fixed cause for every timeout:
  "pool exhausted, server may have ghost connections (try `!username` login to kill them)".
  It fired 197 times on 2026-08-04, 194 of them `superbnc -> SYN`, and the lines printed
  right beside it said something else entirely — `created=2, max=3`, `server entering 20s
  login-cap backoff`, `Account login cap reached`. `created=2` of `max=3` is not an exhausted
  pool and a login-cap stall is not a ghost session; acting on the advice kills the operator's
  own live logins and leaves the real contention untouched. The handler's own comment already
  said "Congestion (no login permit free), NOT corruption" one line below the message claiming
  the opposite. The cause is now read off the pool's counters and separates the four states
  that lead a reader somewhere different — cooldown (clears on a timer), empty pool (the only
  case where ghost sessions are plausible, and where `!username` is still suggested), at
  capacity (concurrency pressure, nothing broken), and login cap (another pool on the same
  account holds the permits). Only the starved side is named, and every verdict carries the
  numbers it was derived from. Same shape as v3.10.45(b) and v3.10.46: *a verdict asserting a
  cause its own counters contradict.* 506 → 521 tests.
- **v3.10.47** — **the whole `[tv-hd]` category had been unraceable for five weeks**: 375 of
  377 races on 2026-08-03 died with "Release not found on any server" while every sibling
  section on the same pool succeeded. Nothing was wrong with the release names or the section
  paths the message told you to check. A section blacklist is a *destination* rule, but
  `SpreadManager` dropped a blacklisted site from the race participant list outright — and
  Phase 1 only probes participants, so the one site actually holding the release was never
  asked. The offending entry was itself fallout from v3.10.44: superbnc had refused an
  `.imdbinfoname` sidecar GlDrive should never have offered, that got recorded as a permanent
  section-wide upload denial, and while .44 removed the cause it left the entry — whose
  14-day TTL every repeat failure refreshed, so it renewed itself for 5+ weeks
  (`failureCount` 46). Three changes, each keyed on the property that defines the class:
  a blacklisted site stays a race participant (destination exclusion still happens in
  SpreadJob's Phase 2, which also handles fill-only); denials naming a **leading-dot** file
  are never recorded and are scrubbed at load; and `IsSectionStructurallyFeasible` counts a
  blacklisted site toward eligibility while still requiring one site able to receive.
  Also: races that die "All destinations denied this release" are now parked like their
  fill-only sibling instead of re-firing on every announce (333/day), and the auto-race log
  line names *which* servers raced and which were excluded rather than a bare count — the
  missing detail that made 431 unique failures unreadable and produced a wrong root-cause
  note in v3.10.46.
- **v3.10.47** — auto-update was wedged on 3.10.45 the same way v3.10.41 was supposed to have
  fixed. The persisted 24h decline marker works, but the skip chain tests the in-memory
  `_autoInstallAttemptedTag` latch *before* it, and a declined UAC prompt is swallowed inside
  `LaunchUpdater` (it returns normally), so the latch is never cleared and suppresses that
  version for the life of the process — a flag with no expiry sitting in front of the
  expiry that was built for it. The latch is now handed back to the persisted marker, once
  per version per process, and only when the marker is actually readable. Separately, a
  declined prompt left `.update-attempt` on disk, so the next start counted a failed
  *install* that never happened; three of those permanently block auto-install **and** the
  tray menu the decline warning points you to. Cleared on the declined branch only — the
  generic launch-failure branch keeps it, because there it is the only brake on re-downloading
  ~150 MB every poll.
- **v3.10.47** — log noise no longer destroys the diagnostics. A destination that
  deterministically refuses MKD is an expected, already-classified outcome, but every one
  logged a full 7-frame stack at Warning: 902 of the day's 937 "FXP transfer failed"
  warnings, 1.5 MB, 14% of the file. That pushed the log past its 10 MB cap and rolled it
  mid-day, and because retention counted *files* rather than days the extra roll evicted a
  whole day of history — the record is destroyed exactly when something goes wrong. The
  denial now logs one line at Debug (level only; classification and control flow are
  unchanged), retention is expressed as a real time limit, and the file-count cap is only a
  disk-blowout stop. The shared `IsExpectedReleaseScopedDenial` predicate is now the single
  definition used by both the fast-skip gate and the logging gate, so they cannot drift
  apart the way the two dest gates did in v3.10.45.
- **v3.10.35** — revive the AI self-tuning loop, dead ~a week. OpenRouter retired the
  `openai/gpt-oss-120b:free` slug; the 404 body names its successor (`use this slug
  instead: …`), now parsed, retried, and cached for the process. Also: HTTP 402 was
  misread as "out of credits" when it means `max_tokens` too large (the balance covered
  27229 of a flat 32000 request) — now retried inside the quoted budget minus 10% headroom.
  A stuck loop now escalates to ERR at 5 consecutive failures instead of staying invisible
  at INF.
- **v3.10.36** — key the healed-model cache by the slug it replaced, so switching models in
  Settings isn't overridden by a heal learned for the previous one.
- **v3.10.37** — classify truncated-payload (`unpacked file size does not match header`)
  and CRC (`UnRAR.exe failed (exit 3)` / `(exit 11)`) extract failures as permanent, so the
  watch folder stops re-reading two hopeless archives five times per restart. Exit 6 (open
  error) stays transient.
- **v3.10.38** — stop the auto-update busy gate from starving updates indefinitely. The
  spread-idle gate is sampled once every 3h; a box that races nonstop is never idle, so an
  update could defer forever. Now forces the install after 12h and logs the elapsed hold.
  (Manual tray update bypasses the gate entirely.)
- **v3.10.39** — persist the 12h deferral clock to `%AppData%\GlDrive\.update-deferred`; it
  lived in a field, so every restart reset it and the deadline never fired on a box that
  restarts within 12h.
- **v3.10.40** — relax the update gate to block only on **in-flight FXP transfers**, not on
  any active spread job (a queued/scanning job loses nothing on restart, so those windows
  are now installable). Plus visible escalation: a still-stuck hold logs ERR + raises a tray
  notification at 6h, before the forced install interrupts a transfer.
- **v3.10.42** — a stale "release dir confirmed" scan result no longer re-admits a dest that
  cannot create the dir, forever. `_destDirConfirmed` exists so a *fill-only* site (denied
  MKD but able to receive into a dir another racer created) isn't locked out — but it was
  add-only and never revoked, so it overrode the MKD-denial gate permanently. On 2026-07-27
  superbnc was confirmed for one MLB release at 07:35 (scan saw 34 files), the dir was
  removed site-side by 07:37 (every later scan: 0 files), and the race then re-attempted
  `MKD /incoming/tv-sports/MLB…` — denied `550 Not allowed to make directories here` — **278
  times in 29 minutes**, 98% of that day's FXP transfer failures on a single release. The
  fast-skip handler logged "dropped for this release" but recorded nothing, so nothing
  actually dropped. The dir-confirmed override is now bounded by the dest's own denials on
  that path (`MaxMkdDenialsWithDirConfirmed = 3`): the genuine fill-only case still costs
  zero denials and is unaffected, while a stale confirmation is abandoned after 3 and logged
  once. Same family as the v3.10.33/.41 loops — a decision that isn't recorded is no decision
  — inverted: a *confirmation* that is never invalidated is a permanent exemption.
- **v3.10.44** — the spread engine kept trying to race glftpd's *hidden* per-site metadata.
  `IsZipscriptArtifact` filtered the imdb sidecars it had happened to observe by suffix
  (`.imdb.html`, `.imdb.nfo`) but missed the dot-prefixed family entirely, so `.imdbinfoname`
  and `.imdb` were admitted into `_fileInfos` as real release content — **86 doomed transfer
  attempts in one day**. Both endpoints had already proved these are site-local state that is
  regenerated per site and must never move: the source answers `RETR 550 No such file or
  directory` (it never had the file), and the destination answers `STOR 553 .imdb: path-filter
  denied permission. (Filename deny)` (it actively refuses it). Beyond the wasted slots, each
  phantom entry inflates the expected file total, holding a race short of 100%. The filter now
  keys on a **leading** dot rather than an enumerated name list — scene naming is dot-separated
  but always puts a basename before the first dot, so a leading dot only ever means a hidden
  site file. Also demoted the `main pool exhausted … falling back to spread pool` line from
  WRN to INF: on a busy destination the main pool is *legitimately* saturated, the fallback
  recovers in ~13ms and loses nothing, yet it emitted ~1700 warnings/day — the noise that let
  v3.10.42 and v3.10.43 hide behind a clean 0-ERR record. The real failure (both pools
  unavailable) stays at WRN. **Meta-lesson: a filter written as a list of the cases you have
  seen will keep missing the ones you haven't — find the property that defines the class.**
- **v3.10.43** — the spread scanner deadlocked against its own connection pool on every
  cycle. `ScanDirectoryRecursive` held its borrowed connection across the recursive call, so
  walking a release of depth N pinned N+1 connections at once — but the account login gate
  (`LoginCap − LoginHeadroom`) leaves the main pool only ~2 usable logins. Any release with a
  subdirectory therefore could not converge: the parent could not release until the child
  borrowed, and the child could not borrow because the parents held every slot. Hold-and-wait,
  so it failed *deterministically*, not intermittently — the tell was that the site with files
  to walk (superbnc, 16 files) timed out at ~21s on **every** scan while an empty dest (SYN,
  0 files, nothing to recurse into) returned instantly. Each occurrence burned the full 20s
  borrow timeout, then re-ran the entire scan on the FXP *spread* pool, stealing transfer
  slots from live races: **2176 pool-exhausted fallbacks, 73 total scan failures, and a
  matching crop of `FXP borrow timeout … pool exhausted` in one day — with zero ERR lines**.
  The borrow is now scoped to the LIST alone and subdirectories are walked after it is
  returned, capping concurrent connections per scan at exactly 1 for any depth. **Meta-lesson:
  a bug can run at 100% duty cycle and still show up only as WRN volume — the severity of a
  log line reflects what the author expected, not what is actually broken.**
- **v3.10.41** — a declined UAC elevation prompt no longer disables auto-install forever.
  v3.10.33 made the decline persistent to kill a restart nag loop, but persistent meant
  *permanent* for that release tag, and the skip logged nothing — so one dismissed prompt
  silently stranded this box on 3.10.39 through **18 polls across 51h** while v3.10.40 sat
  published. `.update-declined` now carries a timestamp and expires after 24h (declining
  again just re-stamps it, so the nag loop stays dead); legacy tag-only markers count as
  expired so an already-stranded install self-heals on the first poll. Every auto-install
  skip now logs its reason — the silence is what hid this, since "Update available" with no
  follow-up was indistinguishable from a healthy deferral. Note the v3.10.38/.39 forced-install
  deadline was correct all along; it simply never ran, because this guard short-circuited
  upstream of it.
- **v3.10.33 / v3.10.34** — earlier passes on the same failure loops: three self-perpetuating
  log-error loops (extractor, AI loop, UAC re-prompt), then trade-login starvation and
  denied-race reporting.

### Security / infrastructure
- Update packages are downloaded only from an allowlisted GitHub host set, verified by
  SHA-256 against a signed `checksums.sha256`, and applied under an HMAC-authorized,
  manifest-sealed elevation handoff (see `UpdateChecker` / `UpdateMarkerHmac`).

## v1.44 — GnuTLS stabilization, spread engine maturity, security fixes

### Security
- **v1.44.55** — ghost-kill TLS validation, media server FTP path-injection fix
- **v1.44.7** — TOFU cert-change rejection, FTP path sanitization, loopback-only torrent ports
- **v1.36.0** — efficiency pass, path sanitization, security hardening
- **v1.32.0** — 12 findings from a security review

### Features
- **v1.44.52** — spread auto-detects glftpd dated directories for 0DAY/MP3 sections
- **v1.44.49** — watchdog logs the crash reason from Windows Event Log before restart
- **v1.44.40** — completion sweep for races + IRC announce trace logging
- **v1.44.35** — chain mode for spread (one route per release at a time)
- **v1.44.33** — auto-reinitialize dead spread pools before race start
- **v1.44.30** — SSCN encryption for FXP control channel
- **v1.44.23** — watchdog subprocess replaces unreliable `RegisterApplicationRestart`
- **v1.44.20** — race history tab with skiplist evaluation trace popup
- **v1.44.0**  — AI-powered rule setup via OpenRouter API (auto-infer sections + skiplist)

### Fixes (highlights)
- **v1.44.53 / v1.44.46 / v1.44.12 / v1.44.11 / v1.44.5** — GnuTLS native-crash family. `NeutralizeGnuTls` before disposal, try/catch around disposal, fix stale temp-zip lock during update
- **v1.44.51** — watchdog crash: avoid `ConfigManager` in the watchdog path (it depends on System.Text.Json)
- **v1.44.45** — pool exhaustion: reduce main pool to 2 when spread is active
- **v1.44.44** — enforce SFV-first transfer order (block data files until SFV is delivered)
- **v1.44.43** — races never completing: reinitialize pools on completion sweep, count borrow timeouts
- **v1.44.41** — IRC announces not detected: built-in verbose glftpd pattern
- **v1.44.39** — torrent streaming stuck on metadata: bind DHT/listen to 0.0.0.0
- **v1.44.32** — duplicate FXP transfers, TVMaze null episode deserialization
- **v1.44.31** — fail fast when pool fully exhausted instead of hanging 30 s
- **v1.44.28** — Streems tab locking up app: serialize WebView2 initialization
- **v1.44.24** — poison FTP connections after *all* failed FXP transfers, not just unhandled exceptions
- **v1.44.13** — discard poisoned GnuTLS connections instead of returning to pool
- **v1.44.8**  — DH1080 key exchange crash that disconnects from IRC

## v1.43 — IRC FiSH hardening, site rules parser

- **v1.43.8** — PreDB refresh always runs (don't require tab active)
- **v1.43.6** — SITE RULES parser: handle Nuke rules and Disallowed Groups
- **v1.43.4** — fix FiSH PM mode mismatch: DH1080 uses CBC, auto-detect peer mode
- **v1.43.2** — Enable/Disable buttons for servers in Settings
- **v1.43.1** — FXP tries CpsvPasv first, falls back to Relay for BNC-to-direct
- **v1.43.0** — auto-race: pass all servers with known source, auto-discover paths

## v1.42 — Spread engine correctness + parallel startup

- **v1.42.9** — force CpsvPasv mode, discard poisoned connections after failure
- **v1.42.7** — remove duplicate TYPE I causing FXP response desync
- **v1.42.6** — spread scan uses main server pools instead of dead spread pools
- **v1.42.5** — 15 s borrow timeout, detailed scan logging
- **v1.42.1** — parallel server mounting off UI thread (fix startup lag/lockup)
- **v1.42.0** — auto-discover release paths across servers for spread races

## v1.41 — Auto-race activity, fuzzy section matching

- **v1.41.4** — case-insensitive sections, catch crashed jobs, logging
- **v1.41.3** — fuzzy section matching for notifications/PreDB in right-click Race
- **v1.41.2** — multi-select server deletion in Settings with confirmation
- **v1.41.0** — Auto-Race Activity log to Spread tab

## v1.40 — Player RAR + WebView2 stability

- **v1.40.9** — group IRC channels by server in sidebar
- **v1.40.8** — RAR playback: download with progress UI, extract locally, play file
- **v1.40.7** — player RAR streaming for BNC servers uses CPSV for data connections
- **v1.40.5** — Streems/Discord login: allow cross-origin navigation for OAuth
- **v1.40.3** — library playback: FromPath for VLC, RAR extraction, file validation
- **v1.40.1** — VLC init error handling, FTP stream timeouts, better errors
- **v1.40.0** — multi-download status display, search multi-select

## v1.39 — Dashboard UX, threading overhaul, IRC robustness

- **v1.39.9** — Downloads UI overhaul: multi-select, clear buttons, context menu, status colors
- **v1.39.8** — total used/free disk across all servers in status bar
- **v1.39.6** — threading and I/O performance overhaul
- **v1.39.5** — FiSH for channels with bare `[key]` format (no `cbc:/ecb:` prefix)
- **v1.39.4** — decouple IRC from FTP: start IRC even when FTP mount fails
- **v1.39.0** — IRC stability overhaul, PreDB improvements, FTPRush XML import fixes

## v1.38 — Folder cleaner

- **v1.38.0** — Folder Cleaner added to Extractor (scan and delete leftover archives)

## v1.37 — Extractor persistence, TOFU auto-accept, site imports

- **v1.37.5** — archive deletion: filter multi-part volumes, add retry
- **v1.37.4/3/2** — FTPRush XML + JSON import with skiplists, TLS, auto-detect
- **v1.37.1** — overhaul extractor: persist all settings, auto-start watchers, fix delete
- **v1.37.0** — auto-accept TLS certs (true TOFU), per-server "Clear Certificate" button

## v1.36 — Efficiency / security pass, site importers

- **v1.36.9** — fix startup deadlock: cert prompt blocking UI thread via Dispatcher.Invoke
- **v1.36.3** — Import Sites from FTPRush and FlashFXP
- **v1.36.2** — Torrents-CSV search backend
- **v1.36.0** — efficiency overhaul, path sanitization, security hardening

## v1.35 — IRC announce detection, auto-updater fixes

- **v1.35.10** — SITE DISKFREE lockup: 5 s timeout, delay first query
- **v1.35.3** — auto-update hash verification failing on filename mismatch
- **v1.35.2** — detect site rules via SITE RULES for auto-configuring spreader
- **v1.35.1** — auto-detect IRC announce patterns from channel logs
- **v1.35.0** — IRC announce detection for auto-racing

## v1.34 — Spread engine feature expansion

- **v1.34.2** — auto-detect sections from server + default skiplist rules
- **v1.34.0** — major spread engine feature expansion

## v1.33 — Spread engine performance

- **v1.33.0** — spread engine performance overhaul

## v1.32 — Update resilience + security review

- **v1.32.3** — 5 s unmount timeout + force-exit fallback
- **v1.32.2** — update loop: stop re-downloading same version repeatedly
- **v1.32.1** — updater not restarting app after update
- **v1.32.0** — all 12 findings from security review

## v1.31 — FXP spread engine introduction

- **v1.31.7** — persist extraction watch folders to disk
- **v1.31.6** — Spread tab lockup on tab switching, resilient refresh
- **v1.31.5** — app lockup during update download and extraction
- **v1.31.3** — live transfers, race from notifications, affils
- **v1.31.2** — Browse tab perf, per-site skiplist, Spread setup guide
- **v1.31.0** — FXP spread engine with race jobs, dual-pane browser, skiplist

## v1.30 — Watch-folder auto-extract

- **v1.30.1** — delete-after-extract cleanup for RAR volume sets
- **v1.30.0** — watch folder auto-extract for drives and network paths

## v1.29 — Multi-format extractor

- **v1.29.2** — RAR multi-volume: total set size, modern `.partNN` naming
- **v1.29.0** — multi-format archive extractor with drag-drop and queue

## v1.28 — Remote glftpd installer

- **v1.28.0** — glftpd remote installer panel via SSH

## v1.27 — Torrent backends

- **v1.27.0** — replace broken 1337x scraper with apibay + SolidTorrents APIs

## v1.26 — Cast + DHT + torrent plumbing

- **v1.26.8** — UI freeze on pause/stop: move VLC calls off UI thread
- **v1.26.3** — Cast To context menu for Chromecast / DLNA / UPnP devices
- **v1.26.0** — enable DHT, listen endpoints, port forwarding for torrent peer discovery

## v1.25 — Upcoming tab, torrent search, IRC hyperlinks

- **v1.25.5** — memory leaks, deadlocks, hot-path inefficiencies
- **v1.25.1** — Upcoming tab includes streaming releases (Netflix, Amazon, etc.)
- **v1.25.0** — clickable release names in IRC, search FTP and download on click

## v1.24 — PreDB polish, parallel RAR playback

- **v1.24.3** — PreDB auto-refresh always runs, merge new releases, countdown bar
- **v1.24.2/1** — play first `.rar` immediately, download remaining volumes in background
- **v1.24.0** — parallel download + extract, library shows RAR files

## v1.23 — First streaming-from-RAR wins

- **v1.23.2** — start VLC playback as soon as first RAR volume downloads
- **v1.23.0** — fix data connection refused: CPSV for RAR downloads, throttle monitor

## v1.22 — Connection pool resilience

- **v1.22.4** — full download retry with connection resilience
- **v1.22.2** — pool: wait for existing conn when server refuses new ones
- **v1.22.1** — show active FTP connections per server in dashboard status bar
- **v1.22.0** — background RAR download + extract with live progress

## v1.21 — Threading + security

- **v1.21.6** — UI freeze during player loading: move FTP calls to background thread
- **v1.21.2** — dashboard crash fix (missing `ModeTabStyle`), security hardening

## See also

- [project-overview-pdr.md](project-overview-pdr.md) — what the app is, why it exists
- [system-architecture.md](system-architecture.md) — the subsystems most of these fixes touched
- `git log --oneline` from the repo root — full history
