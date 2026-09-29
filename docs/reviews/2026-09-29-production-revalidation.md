# GL Drive production revalidation — September 29, 2026

The application remains on **v3.10.138**, released from
`c3388e14dd4ed27300a60d41d336a129e7276904`. This follow-up review found no new
application defect requiring a code change. The remaining failures are an
unreachable FTP endpoint, unavailable SMB authentication, and insufficient
extraction space. They are not resolved by rebuilding the application.

No application source, production configuration, credentials, download
destinations, user media, or protected persistent application data were changed.
The WPF smoke refreshed screenshot artifacts. No new version, replacement release
asset, or deployment was created during this pass. The already-deployed signed
release was independently verified and retained.

## Log findings and root causes

The current-version window is September 28 **20:17:07.396 PDT** through
September 29 **10:04:22.199 PDT**: **3,521 records, 52 Warning, 0 Error,
0 Fatal**. Windows Application events contain no GL Drive Error/Critical
events from startup through the 10:04:26 PDT audit.

The full retained daily-log audit, frozen at **10:11:18.447 PDT**, also covers
the older application versions in those files:

| Daily log | Information | Warning | Error / Fatal |
| --- | ---: | ---: | ---: |
| September 27 | 3,757 | 86 | 0 / 0 |
| September 28 | 5,246 | 102 | 0 / 0 |
| September 29, through audit cutoff | 3,061 | 30 | 0 / 0 |

All **218 warnings** in the 12,282 retained records are classified: 132 T:
parking notices, 60 remount warnings, four initial mount timeouts, four D:
space notices, three IRC endpoint timeouts, ten paired source-relocation FTP
550 warnings plus three handled relocation notices, one historical NukePoller
cancellation already addressed by v3.10.137, and one external PreDB HTTP
timeout before the v3.10.138 startup. The PreDB request used the existing
30-second backoff; release polling was observed again after the restart and
the timeout did not recur. Windows Application records have no GL Drive
Error/Critical events since September 27 00:00 PDT.

| Finding | Evidence and disposition |
| --- | --- |
| SYN endpoint unavailable | 1 startup mount warning, 16 periodic remount warnings, and 140 throttled informational retries. A single unauthenticated TCP probe timed out after 5.019 seconds. Restore the remote endpoint/network; five-minute capped retries are working. |
| T: downloads parked | 34 warnings. Windows reports unavailable remembered mapping (1201), and share-root access fails with logon failure (1326). Restore valid Windows SMB access for the configured mapping. Retries are not consumed while downloads are parked. |
| D: extraction parked | 1 warning. Current free space is 17,239,916,544 bytes (16.056 GiB). The 47-part archive preflight requires 23,155,601,469 bytes plus a 1 GiB reserve: at least 24,229,343,293 bytes free. Minimum shortfall is 6,989,426,749 bytes (**6.509 GiB**). Expanded output and concurrent writes can require more. |
| Direct CPSV route unavailable | Two FTP 425 probe failures selected the existing six-hour Relay fallback. Four affected connections were quarantined; transfers completed through Relay. |
| Stale keepalive connection | One timeout at 08:07:16.507 PDT quarantined a connection. The later spread-pool reinitialization completed in 2.040 seconds at 08:20:26.448 PDT, followed by successful transfers/races. |

The window includes **861 successful transfers and 40 completed races**, with
zero failed races, transfer-deadline warnings, or NukePoller warnings. Transfer
telemetry contains 861 non-aborted records; the longest is 34.806 seconds.
There is no evidence of recurrence of the defects addressed by the prior
releases. The [v3.10.138 report](../releases/v3.10.138.md) documents its actual
code changes: correct attribution of transfer deadlines and release of
transfer tracking registrations.

The initial filesystem directory listing showed stale log metadata. Reading
the file established that logging was current; no logging repair was needed.

## Fresh validation

| Check | Result |
| --- | --- |
| `dotnet build GlDrive.sln -c Release -warnaserror` | Passed; 0 warnings, 0 errors |
| Full Release regression suite | **1,479 passed, 0 failed, 0 skipped**; TRX retained |
| Native loopback FTPS/WinFsp harness | **30 PASS lines**, including completion: stats recovery, 800 commands without re-login, race discovery, search filtering, notifications, data integrity, media ranges, shared handles, disk buffering, mounted create/write/flush/read/rename/delete, and negative-NOOP recovery |
| WPF rendering | Exit 0; **15 fresh images**, file version 3.10.138.0; representative dashboard, wizard, and settings images visually inspected |
| WPF production-state guard | Protected file hashes unchanged; existing app processes remained running |
| Dependency audit, including transitives | No known vulnerable packages reported by configured NuGet sources |
| Read-only production API smoke | **7 passed**: route index, status, sections, races, history, missing-race 404, unauthenticated status 401 |
| Process isolation | Installed app PID 14580 and watchdog PID 53232 both outside job objects |

The fresh five-minute production observation ran from **17:05:15.951 through
17:10:16.015 UTC** (10:05:15–10:10:16 PDT). All **11 samples** reported
v3.10.138.0, unchanged PID 14580, both baseline FTP servers connected, and an
active race. Maximum heartbeat age was **5.80 seconds**. The third server
remained disconnected, so this is a pass against the known degraded baseline,
not a claim that all configured services are healthy.

The independently audited log interval through 17:10:16.015 UTC contains
**99 Information, 0 Warning, 0 Error, 0 Fatal**, including **41 completed
transfers and one completed race**.

The native fixture served only a disposable local directory. Its process was
stopped after testing. Automatic approval review blocked recursive removal of
the verified fixture directory with only the reason “blocked by policy.” The
inactive directory remains at
`%LOCALAPPDATA%\Temp\gldrive-ftps-check-i6m92sw2`; no production files are there.

## Release and installation identity

[GitHub CI run 36516110014](https://github.com/misterentity/GlDrive/actions/runs/36516110014)
is successful for the exact release commit. The original standard
`installer/release.ps1` publication and installed-update procedure are recorded
in the [v3.10.138 deployment report](../releases/v3.10.138.md).

This pass independently downloaded all four assets from
[v3.10.138](https://github.com/misterentity/GlDrive/releases/tag/v3.10.138).
The release target and tag both resolve to the tested commit, and the manifest
RSA signature verifies against the application's embedded key at that commit.
All asset hashes and sizes match local output and GitHub's stored digests.
All **1,149 installed files** match the freshly downloaded ZIP; there are zero
extra installed files.

- Installer SHA-256: `55a692feddfc65924a89907507e7f6d9b28e09d896b99d8f14479fad48ea8823`
- ZIP SHA-256: `9473ef9cac3a082df7750a0294c6ec8ecc63341dc2ae67a161638a158ae9d5be`

The checkout used for the fresh tests was `57c3707`, which differs from the
release commit only in `docs/releases/v3.10.138.md`. The fresh build therefore
tests the released application source. The old verification helper's assumption
that release commit equals the latest documentation commit was corrected in
this pass's local verification script. Old hardcoded-date audit helpers were
not reused for the current observation window.

## Remaining work and limits

Restore SYN connectivity and T: SMB authentication, and make sufficient D:
space available or choose an appropriate replacement destination. No user
content was deleted or moved, and no new credential or destination was guessed.
Production is healthy relative to its two working baseline servers; it is
still degraded for the third server and the parked storage jobs.

The native harness does not reproduce a remote BNC's CPSV implementation.
No production transfer reached the 180-second ceiling during the reviewed
window, so the deadline fix remains covered by its existing regression tests,
not by a newly observed production timeout. A short observation cannot prove
multi-day stability. The rollback release remains v3.10.137, with no data
migration required.

Evidence is retained locally under
`.release-tools/verification-20260929-followup/`: build and regression logs,
TRX, native harness output, WPF renders/state hashes, dependency audit,
sanitized log/Windows-event/external-health audits, published and installed
verification reports, API smoke results, and production-health observations.
