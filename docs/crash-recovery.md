# Crash recovery and update probation

A single unclean exit keeps the existing watchdog recovery, including its three-second wait. Crash-loop protection starts only after three distinct crashed sessions in the same launch chain within 15 minutes. Windows Error Reporting and the watchdog share a session identity so competing restarts count once. Manual launches, requested restarts and update restarts begin a new chain; stale history cannot turn their next single crash into a loop.

Safe mode opens before configuration migrations or engine construction. It starts no site logins, races, scheduled agents, watched-folder extraction or control API. The existing signed HTTPS updater continues checking and can install a newer release, respecting `autoInstallUpdates`. The local window explains the recovery state and offers diagnostics, the Recovery Center, normal restart, exit and eligible rollback. Damaged settings or unavailable logging storage cannot prevent these controls from appearing. Saved settings are not rewritten to disable services.

Safe mode automatically retries normal operation after the sum of the configured backoff delays (21 minutes by default, bounded to one minute–one hour; an empty list uses 15 minutes). It waits for an active recovery operation or installer to finish. Recovery, installation and normal restart use a shared admission lock, so an update cannot interrupt an import or rollback. A failed restart handoff leaves update checks available and is retried.

Safe-mode update preparation has a ten-minute deadline covering segmented downloads, streamed fallback, checksum/signature downloads and hashing. A stalled response releases the recovery admission slot when canceled. Once the verified installer handoff starts, it owns shutdown and is not canceled by this deadline. Ordinary-mode downloads keep their existing timing.

If the safe-mode process itself crashes, the watchdog waits one, five and fifteen minutes on successive retries, then gives normal startup a fresh chance. Windows crash-restart registration is disabled in safe mode so it cannot bypass that backoff. Normal restart begins a fresh chain. Ordinary WER/Restart Manager registration keeps patch and reboot relaunch enabled; missing or intended-exit markers prevent those relaunches from being counted as crashes. Unreadable restart history is preserved as `.corrupt` and starts empty; failed writes retain bounded memory and warn once. History capacity evicts the oldest evidence. These limits apply to crash recovery, never login or racing rates.

## Previous builds and rollback

Verified previous installed files are retained for 24 hours. New updates record exact file generations, lengths and hashes in the installation's `.update-recovery` directory without changing store formats. A complete protected generation left by an older updater can be adopted; first successful observation starts its retention period. Cleanup skips individual untrusted or linked subfolders and continues through trusted paths. Orphan `.old` files outside retained receipts are removed; changed receipt payloads and generations with damaged receipts remain for local review. An incomplete, ambiguous, modified or unprotected previous build cannot be restored by the rollback helper.

Rollback is a local, explicit choice. It is offered in safe mode and in the offline Recovery Center, reachable from Settings → Diagnostics → Recovery Center. The online Recovery Center can inspect availability and directs the user to restart offline before rollback. Site outages and other service-health signals never trigger rollback.

The helper requires administrator approval and verifies the fixed protected installation directory, ownership, effective permissions, paths and file hashes. Portable or writable installations are ineligible for this privileged operation. Using a different administrator account is refused when the requesting user's reinstall guard cannot be verified. No caller-supplied profile or installation path crosses elevation.

Before shutdown, the app writes and verifies the rejected version in the existing `.update-failures` format and clears the old `.update-attempt` marker. Earlier builds therefore recognize the rejection instead of immediately reinstalling that release. The helper preserves a restricted, verified settings copy and projects the optional new sections out of the down-level profile. It stages and verifies the previous files, consumes the recovery record before changing installed files, and restores the current files and profile if replacement fails. A consumed rollback cannot be replayed. Successful stages are removed immediately or on the next cleanup if a mapped file is still locked. Cleanup retains at most the newest recoverable failed stage for up to 24 hours. An incomplete reversal is explicitly retained for manual repair because it may contain the only surviving copy of a file.

## Automatic updater interaction

The existing three-hour polling interval and idle gate remain, with the first update check 30 seconds after startup, including safe mode. Previous-build retention never holds a newer automatic installation: hotfixes remain eligible immediately. Missing or unreadable recovery bookkeeping does not block subsequent updates. Installation, cleanup and rollback share a local mutation lock.

## Settings

These settings live in the optional `recovery` block and appear in `config_schema`. They do not change the legacy control token or grant automation access to recovery controls.

| Setting | Default | Reason |
| --- | --- | --- |
| `crashLoopProtectionEnabled` | `true` | Requires positive evidence from at least three distinct crash restarts; never reacts to site health. |
| `crashLoopThreshold` | `3` | Minimum three, maximum twenty; a single crash cannot qualify. |
| `crashLoopWindowMinutes` | `15` | Detect repeated crashes within a bounded window; configurable from one to sixty minutes. |
| `safeModeRetryDelaysSeconds` | `[60,300,900]` | Recovery-UI crash backoff and automatic normal-resume delay. At most eight delays, each 3–3600 seconds. Empty keeps normal crash recovery available. |
| `updateProbationEnabled` | `true` | Retained for settings compatibility; it never delays a newer update or shortens backup retention. |
| `autoInstallUpdates` | `true` | Preserves existing automatic-update behavior. |

A malformed optional recovery block is retained for repair without discarding the rest of the profile. Crash-loop gating is disabled for that malformed block; explicit update opt-outs are preserved. Backup deletion and rollback decisions require valid local evidence.

Bootstrap recovery reads are bounded to 8 MiB. An oversized or unreadable profile uses in-memory recovery defaults without rewriting its source; the ordinary configuration loader is unchanged.

Pure-policy, isolated-file and source lifecycle tests cover the recovery boundaries. Native watchdog/WER, WPF controls, UAC, installation permissions and actual binary replacement still require outside-sandbox verification. No live site health is used by the recovery policy.
