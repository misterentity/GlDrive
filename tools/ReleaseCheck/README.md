# Local release verification

Run on Windows with .NET 10, installed WinFsp, Python, pyftpdlib and pyOpenSSL. The [pyftpdlib FTPS fixture](https://pyftpdlib.readthedocs.io/en/latest/tutorial.html#ftps-ftp-over-tls-ssl-server) binds only to loopback and serves a newly created temporary directory. It never reads the app's server configuration. Certificate trust is pinned to the generated fixture fingerprint in a separate temporary store.

1. Start `python tools/ReleaseCheck/local_ftps.py <temporary-state-json-path>`.
2. Run `dotnet run --project tools/ReleaseCheck -c Release -- <temporary-state-json-path>`.
3. Stop the fixture process and remove its directory (the `root` in the state JSON) after verifying that it is under the system temporary directory.

The harness first runs 800 commands on one borrowed FTPS session, crossing FluentFTP's default TLS transaction limit and verifying that no re-login occurs. It then checks real GnuTLS/FluentFTP transfers, mounted-file disk buffering, shared open handles, dirty rename and volume flush. It mounts an unused drive letter to exercise Windows create/write/flush/read/rename/delete through WinFsp and unmounts its own drive in `finally`.

It also rejects two NOOP probes through the disposable fixture: the monitor must signal connection loss, reject the first reconnect probe, and wait for a positive reply before reporting recovery. Shutdown must not emit another connection-loss event.

Cached and live search are exercised against actual FTPS directories: decorated completion markers are excluded while real release names containing COMPLETE or a bracketed group prefix remain searchable.

Race discovery also checks the case where the sole receiving site already has the release and its peer is download-only: the job completes without an error event or a transfer to the excluded peer.

This does not exercise a remote glftpd BNC's CPSV behavior or replace an extended live-server soak.
