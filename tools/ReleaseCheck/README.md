# Local release verification

The separate native sharing check runs with `dotnet run --project tools/ReleaseCheck -c Release -- --sharing-only <evidence-directory>`.
It creates its own fixture and needs no state JSON or Python server. It exercises actual GnuTLS control sessions, CPSV data TLS,
separate main/spread pools and their shared login gate, idle lending, blocked-read and blocked-TLS-setup yield,
double-reply abort synchronization, same-session reuse by a waiting race, reacquisition, exact payload bytes,
and poisoned-slot wake/deferred reclamation. A real `StartFxp` call reclaims a download loan while its destination
pool remains deliberately occupied, then cancellation releases its activity and source lease before any remote
transfer command. Opposite-direction pair acquisitions use real one-slot pools and must both complete without
deadlock or extra logins. This tests pair acquisition, not a complete server-to-server file copy.
Its control listener binds only to loopback. Because the production CPSV target guard rejects loopback addresses, its temporary
data endpoints bind an existing local RFC1918 IPv4 interface (virtual preferred), accept only that same host address, and pin
the application's CPSV TLS certificate. They serve only generated in-memory bytes, never files or production credentials.
The check fails explicitly when no suitable local interface exists; it is opt-in and does not change the normal unit suite.
The evidence directory contains assertion results and sanitized protocol traces; unique temporary trust/staging directories
are retained for diagnosis. It does not load production application configuration or mount a drive. Queued race state is
synthetic; this validates the sharing boundary, not a complete remote glftpd FXP race.
Clean handoff must reuse the same two physical sessions. Failed setup deliberately quarantines the old native
session while releasing its accounted login permit; the evidence distinguishes the temporary third physical
connection from the two accounted permits. Cleanup cancels and drains its own transfers and waiting borrowers
with an independent deadline. If they cannot settle, the isolated process fails without disposing live native
sessions. Run through `dotnet run` or the generated `ReleaseCheck.exe` apphost so the native DLL search path is
correct; invoking `dotnet ReleaseCheck.dll` can fail to locate the bundled GnuTLS DLLs on Windows.

Run on Windows with .NET 10, installed WinFsp, Python, pyftpdlib and pyOpenSSL. The [pyftpdlib FTPS fixture](https://pyftpdlib.readthedocs.io/en/latest/tutorial.html#ftps-ftp-over-tls-ssl-server) binds only to loopback and serves a newly created temporary directory. It never reads the app's server configuration. Certificate trust is pinned to the generated fixture fingerprint in a separate temporary store.

1. Start `python tools/ReleaseCheck/local_ftps.py <temporary-state-json-path>`.
2. Run `dotnet run --project tools/ReleaseCheck -c Release -- <temporary-state-json-path>`.
3. Stop the fixture process and remove its directory (the `root` in the state JSON) after verifying that it is under the system temporary directory.

The harness first checks source relocation through real FTPS scans, directory probes and downloads. It establishes ownership, physically moves a payload while retaining the old release directory, and checks both a rejected RETR and an empty LIST. The watch path must win over a populated section decoy; the next scan must restore ownership and a file observed before the move must download intact from its new path. Missing watch and section candidates must preserve the same reusable pooled connection without a new login. To run just these checks, append `--relocation-only` to the harness command.

These relocation checks seed the source role and original path normally established by race discovery, then invoke the actual scanner and migration handler. They cover the scan/relocation/download boundary, not a complete server-to-server FXP race. The same harness compiled against the published v3.10.152 application fails the retained-directory relocation assertion; v3.10.154 passes.

The full harness also runs 800 commands on one borrowed FTPS session, crossing FluentFTP's default TLS transaction limit and verifying that no re-login occurs. It checks real GnuTLS/FluentFTP transfers, mounted-file disk buffering, shared open handles, dirty rename and volume flush. It mounts an unused drive letter to exercise Windows create/write/flush/read/rename/delete through WinFsp and unmounts its own drive in `finally`.

It also retains one Windows directory handle across multi-page enumeration, mounted rename, and delete/create operations. Restarting enumeration on that same handle must reflect each change; this catches stale handle snapshots that ordinary `Directory.GetFiles` calls on fresh handles miss.

It also rejects two NOOP probes through the disposable fixture: the monitor must signal connection loss, reject the first reconnect probe, and wait for a positive reply before reporting recovery. Shutdown must not emit another connection-loss event.

Cached and live search are exercised against actual FTPS directories: decorated completion markers are excluded while real release names containing COMPLETE or a bracketed group prefix remain searchable. A root search also verifies that a reused session's non-root working directory cannot redirect a configured `/` listing.

Race discovery also checks the case where the sole receiving site already has the release and its peer is download-only: the job completes without an error event or a transfer to the excluded peer.

Account-stat checks inject a refused passive data endpoint, then restore a LIST trailer carrying synthetic credits and ratio. The next refresh must recover immediately, while clean replies without stats must still activate the bounded no-access cache.

Manual folder FXP enumeration also traverses nested folders using a pool with exactly one connection and verifies that every listing reused the same session. The download planner and actual segmented FTP downloader then copy a tree containing a root NFO, two different payloads with identical basenames in CD1/CD2, a deeper sample, and an empty folder. All local payloads and the network-usage ledger remain inside the disposable fixture directory.

Download failure checks make the fixture advertise 1,000 bytes but send ten, then separately send the complete payload followed by FTP `426`. Both downloads must fail after bounded retries, retain their staging files, and leave the final filenames absent. Restoring valid data and completion replies must make a subsequent retry publish the exact expected bytes. Append `--downloads-only` to run certificate, relocation, enumeration and download checks without mounting WinFsp.

An arriving-release check starts with one valid local part and a remote SFV whose other part is still missing. The real download manager must park without consuming retries, release its sole transfer slot for an unrelated download, and persist the wait across a manager restart. After the fixture supplies the missing part, the scheduled rescan must complete and verify both parts without downloading placeholder files.

Race arrival checks inspect two existing partial release directories through FTPS, verify that they wait without deleting landed files, then add the missing inventory and check that a resumed probe completes. They exercise discovery and completion; the fixture does not implement glftpd's FXP extensions.

Append `--search-only` to exercise query word matching, cached results with the sole connection occupied, newly arrived releases beyond an old index, empty or unfamiliar SITE SEARCH replies, partial results from denied roots, and a busy live search whose connection becomes available after the first borrow deadline. These checks also run in the full harness.

The full WinFsp checks also verify that nonrecursive deletion rejects a populated folder, removes an empty folder, and preserves a file that arrives after the deletion preflight. The certificate preamble applies the connection-test validation callback to a changed pin and verifies rejection before any FTP authentication command.

This does not exercise a remote glftpd BNC's CPSV behavior or replace an extended live-server soak.
