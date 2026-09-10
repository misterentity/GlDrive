# GlDrive efficiency and reliability review

Date: 2026-09-09. Baseline: `16cd469`, application version `3.10.110`.

The review found reproducible defects despite a passing baseline suite. This change set improves mounted-file writes, shutdown recovery, saved-state ordering, telemetry I/O, cache correctness, and dashboard accuracy. The initial review fixes and the follow-up reliability work are included in v3.10.111. See the release closure below for validation and remaining limits.

## Scope and evidence

Reviewed the architecture and selected failure paths across startup/watchdog/update handoff, FTP pools and TLS teardown, WinFsp file operations, download/extraction recovery, spread lifecycle/history, IRC event wiring, media HTTP streaming, AI telemetry/config changes, WPF navigation, and build/CI. The deepest examination and new regression coverage focus on the modified paths below. This is an app-wide engineering review, not an exhaustive protocol or security certification.

Evidence collected:

- Baseline: **1,182 tests passed**.
- Initial review added **26 behavioral regression cases** (1,208 passed); the release follow-up added **22 more**, for **1,230 passed, zero failed or skipped**.
- Release solution build: **zero warnings and zero errors**.
- NuGet audit, including transitive dependencies: **no known vulnerable packages reported by the configured feeds**. This does not assess application-level vulnerabilities.
- Read-only inspection of the running dashboard confirmed contradictory fixed metrics and unnamed navigation tabs.
- Built application ran successfully in isolated `--screenshots` mode and generated **15 screenshots**. Downloads and performance settings were visually inspected. This exercises WPF rendering, not real FTP transfers.
- One running-app snapshot: approximately **210 MiB private memory, 31 threads, 1,078 handles**. A single sample cannot establish whether memory is growing.
- Latest **20,000 lines** of `gldrive-20260909.log`: 0 ERR, 0 FTL, 26 WRN; 10 matches for connection-timeout text and 178 for mount-failure text. These are overlapping text-pattern counts, not distinct incidents or a measured failure rate. No native-session-leak messages appeared in that sample. No server identities, credentials, or raw logs are copied into this report.

## Implemented fixes

Priority describes the original impact: P1 risks data loss or materially incorrect behavior; P2 affects efficiency, resilience, or operability.

| Priority | Finding and trigger | Result after change | Evidence / implementation |
|---|---|---|---|
| P1 | Writing part of an existing mounted file without first reading it creates an empty write buffer, dropping untouched content. Resizing has the same problem. | Initialize write buffers from existing content before partial writes/resizes; retain existing read buffers when available. | [FileNode](../../src/GlDrive/Filesystem/FileNode.cs), [filesystem](../../src/GlDrive/Filesystem/GlDriveFileSystem.cs), `FileBufferReliabilityTests` |
| P1 | File `Flush` returns success without uploading pending writes. Cleanup uploads can fail after the caller has already been told its flush succeeded. | File flush uploads pending content and maps upload failures to NTSTATUS; cleanup shares the upload implementation. | [filesystem](../../src/GlDrive/Filesystem/GlDriveFileSystem.cs). Successful remote flush still needs a live-server test. |
| P1 | Normal shutdown cancellation marks downloads permanently cancelled, preventing automatic restart. | Global shutdown returns interrupted downloads to Queued; explicit user cancellation stays Cancelled. | [DownloadManager](../../src/GlDrive/Downloads/DownloadManager.cs), `DownloadShutdownReliabilityTests` |
| P1 | Concurrent saves can capture old state, wait, and overwrite a more recent snapshot. An atomic rename alone does not prevent this. | Snapshot creation and persistence are ordered together for downloads, notifications, race history, and route-speed history. | [SecureFile](../../src/GlDrive/Util/SecureFile.cs), associated stores, `SecureFileSnapshotTests` |
| P1 | Undo rewrites the audit trail directly, exposing the entire history to truncation on interrupted writes. | Undo rewrites use the existing restricted temporary-file replacement helper while preserving untouched rows. | [AuditTrail](../../src/GlDrive/AiAgent/AuditTrail.cs), existing durability tests |
| P2 | Root cache keys normalize inconsistently; capacities below four fail to evict; stale entries without a refresh callback remain hits forever; duplicate listing names throw during metadata lookup. | Consistent root keys, minimum eviction, proper expired misses, duplicate-tolerant case-sensitive lookup, and a captured refresh delegate. | [DirectoryCache](../../src/GlDrive/Filesystem/DirectoryCache.cs). Eight new cases failed before the fix; all now pass. |
| P2 | A single expanding write can allocate beyond the intended memory spill threshold; reads after local writes redundantly fetch remote content. | Account for the upcoming write length before allocation, preserve stream position when spilling, and serve buffered local writes directly. | [FileNode](../../src/GlDrive/Filesystem/FileNode.cs), `FileBufferReliabilityTests` |
| P2 | Telemetry opens/closes a file for every event; queue overflow silently evicts accepted rows without incrementing the drop count; disposal cancels before draining. | Append batches of up to 64 rows; reject/count overflow without blocking producers; count failed writes; complete all streams and allow a shared two-second drain before cancellation. | [TelemetryRecorder](../../src/GlDrive/AiAgent/TelemetryRecorder.cs), `TelemetryRecorderReliabilityTests` |
| P2 | Download disposal destroys semaphores while workers still release them; the store timer is not flushed/disposed by the manager. | Cancel workers, drain processor/progress/retry tasks, then save, flush, and dispose their resources. Repeated disposal is safe; overlapping starts are rejected. | [DownloadManager](../../src/GlDrive/Downloads/DownloadManager.cs), [DownloadStore](../../src/GlDrive/Downloads/DownloadStore.cs) |
| P2 | Media request logs and the player log contain a usable playback token. Disconnect/cancellation exits do not consistently close HTTP responses. | Log route/resource details without the token-bearing URL and close responses in `finally`. | [MediaStreamServer](../../src/GlDrive/Player/MediaStreamServer.cs), [PlayerViewModel](../../src/GlDrive/UI/PlayerViewModel.cs) |
| P2 | Every reopened Spread view leaves an anonymous event subscription on the long-lived server manager. | Named subscription is removed on disposal; queued UI callbacks ignore disposed views. | [SpreadViewModel](../../src/GlDrive/UI/SpreadViewModel.cs) |
| P2 | The live dashboard displays invented rates, RTTs, pool occupancy, active races, and fixed protocol/activity status. | Replace ticker claims with existing live status bindings, remove its perpetual animation, use capability labels, and name 17 navigation tabs for accessibility. | [DashboardWindow](../../src/GlDrive/UI/DashboardWindow.xaml), live inspection and rendered screenshots |
| P2 | `GlDrive.sln` contains no projects, so solution-level commands can report success without checking the app. Documentation also contradicts the existing test setup. | Add both projects to the solution; CI builds it and watches solution changes; use the stable .NET SDK channel; correct build/test guidance. | [solution](../../GlDrive.sln), [CI](../../.github/workflows/ci.yml), README and contributor docs |

The controlled telemetry saturation test accepts nine events, rejects/counts twelve, and writes the accepted events in **two append calls**. It demonstrates batching and accounting; it is not a production throughput benchmark. Root metadata lookup, buffered reads, removed view subscriptions, and the stationary status strip also eliminate specific unnecessary work without changing FTP connection limits.

The channel fix follows the documented distinction between `DropNewest`, which evicts a queued item, and `Wait` with `TryWrite`, which returns false at capacity. [Microsoft channel documentation](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels). The filesystem fix follows WinFsp's file-flush contract; its cleanup callback cannot report failure. [WinFsp API](https://winfsp.dev/doc/WinFsp-API-winfsp.h/).

## Remaining findings and next priorities

| Priority | Remaining issue | Concrete next work and acceptance criteria |
|---|---|---|
| P1 | Mounted reads still materialize whole files in memory. Per-file flush is improved, but volume flush returns success without a registry of dirty files; cleanup failures still have no durable recovery journal. Open handles also retain their original path after `Rename`. | Introduce shared per-path file state and disk-backed read/write staging. Test two open handles, rename while dirty, volume flush, network loss during save, and restart recovery against a disposable FTP directory. Memory use should stay bounded independently of file size. Sources: `GlDriveFileSystem.Read/Flush/Rename/Close`, `FileNode.RemotePath`. |
| P1 | Media streaming stops on early EOF without verifying the expected byte count before promoting `.partial` to a final cache file. Transfer control replies and connection poisoning are also inconsistent with the central FTP helpers. | Share a bounded copy/verification path and FTP completion handling. Reject truncated transfers, preserve partial files as incomplete, and prove that seek/disconnect cannot return an unsynchronized connection to the pool. Sources: `MediaStreamServer.HandleDirectStream`, `StreamStandard`, `StreamCpsv`. |
| P1 | Control API request bodies are size-limited, but body reads have no cancellation/deadline and accepted requests have no concurrency bound. | Bound concurrent handlers, add a request deadline tied to shutdown, and close rejected/expired contexts. Test stalled uploads and request bursts while downloads and IRC remain responsive. Sources: `ControlApi.AcceptLoop`, `ControlRequest.ReadBodyAsync`. |
| P1 | AI config mutation and its audit/save are separate operations. A mutation can occur before the audit append or config save fails; before-value checks skip unresolved and non-scalar targets. | Apply proposed changes to a detached config snapshot, verify current revision at commit, and define a recoverable audit/commit protocol. Inject audit and config-write failures and verify that no unrecorded configuration mutation becomes active. Sources: `ChangeApplier.Apply`, `AgentRunner.RunOnceAsync`, `ConfigManager.Save`. |
| P2 | Overall application shutdown still mixes bounded waits and background teardown. The Spread manager disposes gates before stopping jobs; synchronous server cleanup can outlive telemetry disposal. | Give services one asynchronous stop contract, stop producers first, drain jobs within one app-level deadline, then dispose pools and telemetry. Exercise quit/remount during transfer, extraction, and reconnect. Preserve the new download-specific ordering. |
| P2 | Some remaining stores still write directly to final files or default to empty after a load error. | Apply ordered atomic persistence and explicit recovery policy to `PlayerResumeStore`, `FreezeStore`, `NukeCursorStore`, and other small ledgers. Avoid treating a failed load as authorization to overwrite the original. Test full disk, sharing violations, and corrupt JSON. |
| P2 | Repeated mount/connection failures remain visible in the production sample. Existing cooldown and login-gate behavior must be preserved. | Group retries by failure class and time window, then measure attempt rate, recovery time, and pool borrow latency. Run a supervised 24â€“48 hour soak including a controlled disconnect/reconnect. The log sample alone does not identify the external cause. |
| P2 | Large classes concentrate unrelated responsibilities: `SpreadJob`, `DashboardViewModel`, `UpdateChecker`, and `ExtractorWindow`. Many existing tests assert source text rather than observable behavior. | Extract lifecycle, queue, persistence, and presentation boundaries incrementally, preserving protocol behavior. Add behavioral tests when changing a boundary; avoid a broad rewrite or unvalidated concurrency increases. |

## Verification and delivery limits

Commands executed successfully:

```powershell
dotnet test src/GlDrive.Tests/GlDrive.Tests.csproj --no-restore
dotnet test GlDrive.sln -c Release --no-restore
dotnet build GlDrive.sln -c Release --no-restore
dotnet list src/GlDrive/GlDrive.csproj package --vulnerable --include-transitive
git diff --check
```

The new tests use temporary files or uniquely named test stores and do not perform FTP/IRC operations. Screenshot mode exits before production startup and disables credentials/config writes. The existing installed application was observed, not restarted. Existing unrelated untracked files were left in place.

Before shipping the filesystem and lifecycle changes, exercise a disposable mounted directory: edit without a prior read, append, truncate, read after write, flush successfully, force a failed flush, and verify bytes from a separate client. Also quit/reopen with a partially downloaded release and explicitly cancel a separate item. Validate both standard FTPS and CPSV paths. The passing managed suite and synthetic UI render do not replace these native/network checks; sustained transfer throughput, memory growth, and remote CI execution have not been measured in this review.


## Release closure — v3.10.111

The follow-up implements disk-staged mounted reads, shared per-file buffers with serialized access, volume flush failure reporting, open-path rename tracking (including open descendant directories), and restricted recovery copies for failed close uploads. Recovery is deliberately manual: a failed upload can have partially overwritten the remote file, and automatically replaying it could overwrite a newer remote edit. These copies do not provide power-loss/crash journaling for every acknowledged write.

Media streaming now uses exact-length copy checks, validates FTP completion replies, discards interrupted sessions, and uses unique staging files with atomic cache promotion. Bounded range streams discard their interrupted control sessions without waiting for a full-file completion reply. The native harness verified the standard FTPS media completion path; remote BNC CPSV behavior remains unverified.

The control API now admits at most 16 concurrent requests, applies a 15-second deadline with HTTP.SYS response abort, and drains active contexts. Real loopback HTTP tests verified stalled-body expiry, overload rejection and cancellation on disposal. The media server admits at most eight handlers and keeps cancellation resources alive until they finish.

AI runs now derive the prompt and candidate from the same detached configuration snapshot, reject concurrent settings changes at commit, and use a durable pending-commit record with idempotent audit recovery. Correction to the initial finding: production already used ConfigManager.Load to obtain a detached config; the demonstrated design gap was concurrent overwrite and separate config/audit persistence. Transactional runs enforce supplied before-values for resolved targets and defer wishlist pruning/error-report mutations outside the config to manual action. Unreadable pending commits stop subsequent AI commits until resolved.

PlayerResumeStore, FreezeStore and NukeCursorStore now save atomically and preserve unreadable input. FreezeStore blocks AI mutations if its restrictions cannot be read and does not remove restrictions in memory after a failed save. SecureFile uses unique staging files and flushes content to disk before rename.

Spread jobs and metadata prechecks are tracked through shutdown. Transfer gates and pools are disposed after tracked workers finish, with an explicit timeout log when cleanup must continue in the background. Server shutdown invokes asynchronous unmounts concurrently. Download and telemetry fixes from the initial review remain covered by their regressions. This is bounded best-effort shutdown; it does not prove completion of every native or external operation before process exit.

Release validation:

- `dotnet test GlDrive.sln -c Release --no-restore`: **1,230 passed**, zero failures/skips, no build warnings.
- NuGet audit including transitive packages: no known vulnerable packages reported by configured feeds.
- `tools/ReleaseCheck`: real GnuTLS/FluentFTP upload/download and media completion; shared handles, 2 MiB disk spill, partial write preservation, dirty rename, volume flush and remote byte verification; installed WinFsp create/write/flush/read/rename/delete. All passed. Each repeat uses a fresh remote fixture directory.
- WPF screenshot mode rendered 15 images for v3.10.111; dashboard layout was inspected and config/trust-store hashes were unchanged.
- Review/release plan and reproducible native harness are checked in alongside the fixes.

Remaining follow-up: supervised remote CPSV and interrupted-network validation, 24–48 hour resource/reconnect soak, crash-safe mounted-write journaling, full application shutdown coordination, and incremental decomposition of large classes. Existing BNC cooldowns and account login limits were preserved; the prior log sample is insufficient to assign an external cause or justify faster retries. These are explicitly unverified follow-ups, not claimed release results.
