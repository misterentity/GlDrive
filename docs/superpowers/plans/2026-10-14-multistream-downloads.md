# Multi-stream / multi-network downloads (plexo-style)

Source idea: https://github.com/anmolkapil/plexo — one file over several connections and
several networks, block scheduling, staged writes, validated resume, per-network limits.

**Scope: downloads only.** FXP/spread racing transfers are untouched. The only racing-side
change is two read-only accessors so a download can borrow the race login while the site is
not racing (decision 2026-10-14: "Borrow idle race login").

## Constraints that shape the design

| Fact | Consequence |
|---|---|
| Each site allows 2 usable logins (`loginCap 4`, `loginHeadroom 2`): 1 general (mount + downloads + search), 1 priority (spread pool, held idle with keepalive). | FTP can have at most **2 lanes per site**: the main-pool connection and the spread pool's idle connection. No new logins are ever created for downloads. |
| A race's `Borrow` on a full spread pool waits on the channel; a `Discard` does not wake it. | The borrowed race connection must come back by a clean `Return`, so an early stop has to leave the control channel in sync: close the data socket, read the 426/226, then confirm with NOOP→200. Anything else poisons it, and the race falls back to its own reconnect. |
| Every site here is CPSV (BNC). CPSV data TLS is .NET `SslStream`, not GnuTLS. | Closing a CPSV data channel mid-file is safe. Standard-PASV (FluentFTP GnuTLS data) sites get file-level parallelism only and never borrow. |
| CPSV data already connects from a different IP than the control channel (control goes via the BNC), so glftpd accepts foreign-IP data. | Binding the **data socket** to another adapter is how FTP gets multi-network. Control stays on the default route. |
| MonoTorrent 3.0.2 cannot bind outgoing peer sockets (see `VpnBinding`). | Torrents only get the speed caps (total and slow mode). No multi-network peers. |
| This PC has one real uplink (Ethernet). ProtonVPN rides on top of it. | Networks default to the default-route adapter only, so existing behavior is unchanged. Extra adapters (tether, second ISP, VPN) are opt-in in Settings. |

## Components (`src/GlDrive/Downloads/Segmented/`)

1. **`ByteRangeSet`**: merged `[start,end)` set of completed bytes, used for persistence and finding gaps.
2. **`RangeScheduler`**: work-stealing over a list of files.
   - `Take` hands out the next untouched gap, file order first.
   - When no gaps are left, it splits the largest in-flight remainder at its midpoint, but only if both halves are at least `minSplit`. The victim's `End` shrinks and it stops there.
   - FTP lanes therefore mostly take whole RAR volumes and end cleanly on a 226. Splits happen only at the tail.
3. **`StagedFile`**: `<name>.gdpart` plus a `<name>.gdpart.json` sidecar holding size, validator and done ranges.
   - Positional `RandomAccess` writes from several lanes.
   - A validator mismatch on resume (FTP SIZE+MDTM, HTTP ETag/Last-Modified) restarts the file instead of splicing two versions together.
   - Atomic rename on commit.
   - A legacy partial final-named file is adopted as the prefix `[0,len)`.
4. **`TransferPacer`**: token buckets for the total limit, slow mode, the per-server limit and per-network limits.
5. **`NetworkCatalog` / `NetworkPath`**: lists the adapters that are up, have IPv4 and have a gateway or tunnel. Each is matched to settings by interface Id, falling back to name. The address is re-resolved on every lane open, never cached.
6. **`NetworkUsageStore`**: per-network bytes for each day/week/month period, saved to `%AppData%\GlDrive\network-usage.json`. A network over its allowance gets no new lanes.
7. **`SegmentedTransfer`**: the engine.
   - Runs lanes against the scheduler with retries.
   - Requeues a failed lane's remainder.
   - Aggregates progress and per-lane stats into `DownloadProgress.Lanes`.
   - Lanes can join mid-transfer (the borrowed race login becomes free) and leave (yield).
8. **Lanes**
   - **`FtpLane`**: a `PooledConnection` plus an optional bound data address.
     - CPSV opens with `REST` and `RETR`.
     - An early stop is a clean abort followed by a NOOP sync check.
     - `ShouldYield` (borrowed lane only) turns true the moment the site has an active, starting or queued race. The lane finishes if under about 3 s remain, otherwise it stops early. Either way it returns the connection in under about 1 s.
   - **`HttpLane`**: a per-network `HttpClient` whose `SocketsHttpHandler.ConnectCallback` binds to the network's address.
     - Lanes send `Range` requests.
     - A 403, 429 or 503 parks the lane for 60 s, and the network regains one lane per minute (plexo).
9. **`KeepAwake`**: a ref-counted `SetThreadExecutionState(ES_SYSTEM_REQUIRED)` call held while any transfer runs.

## Wiring

- `StreamingDownloader` becomes a thin facade over `SegmentedTransfer` for both single files and releases. `DownloadManager` hands it the whole file list at once, so lanes parallelise across RAR volumes.
- `SpreadManager` gets `IsServerRaceIdle(id)` and `TryBorrowIdleRaceConnection(id, timeout)` through the `IRaceLoginLender` interface, which `ServerManager` wires into `MountService`.
- `CpsvDataHelper.OpenDataTcp` takes an optional local bind address.
- `UpdateChecker` downloads the update zip with `SegmentedHttpDownloader`, falling back to the old single GET.
- `TorrentStreamService` sets `MaximumDownloadRate` from the total limit or slow mode.
- Config gets `Downloads.MultiStream` (enabled, borrow-race-login, HTTP lanes per network, FTP min split, slow mode) and `Downloads.Networks[]` (use, name, per-network KB/s, allowance in GB per period).
- UI:
  - Settings gets a **Networks & streams** section.
  - The downloads list shows each download's lanes, e.g. "2 streams · Ethernet ×1, race login ×1".
  - The tray gets a slow-mode toggle.

## Tests

`ByteRangeSet` merge and gaps, `RangeScheduler` take, steal and requeue (including min-split and the
multi-file order), `StagedFile` resume, validator mismatch and legacy adoption, `TransferPacer`
math, `NetworkCatalog.Choose`, `NetworkUsageStore` period rollover, `SegmentedTransfer` with fake
lanes (a lane failing mid-range, a lane joining, a lane yielding, bytes written exactly once), and
`FtpLane` yield policy.

## Out of scope (stated, not forgotten)

- Endgame duplicate-block racing (plexo "BACKUP"). It costs logins on FTP and is marginal on HTTP.
- Multi-network torrent peers, which needs MonoTorrent's `Factories` API (alpha only).
- Multi-source FTP, meaning the same release pulled from two sites at once. Worth a later phase.
