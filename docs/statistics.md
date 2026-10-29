# Statistics and traffic ledger

Open **Statistics** from the dashboard and choose **Refresh**, or call the read-only
MCP tool `stats.summary` with `days` from 1 to 90 (default 7). Opening the window
does not read telemetry. Refresh reads existing local telemetry and retained race
history in the background. It never logs in, lists FTP directories, polls a server,
changes configuration, or counts traffic against an allowance.

The report includes:

- Daily endpoint payload GB, using decimal GB. Sent bytes include completed race
  transfers and completed downloads from that server. Received bytes describe
  completed race transfers to that server. Download bytes are a subset of sent
  bytes. Do not add sent and received endpoints together as WAN usage.
- Relay sent/received subtotals where the actual transfer protocol was recorded.
  Older transfer rows have unknown protocol, but remain included in total bytes.
- Route throughput from completed transfer bytes divided by their combined
  measured duration. Rows without a valid duration remain in payload totals but
  are excluded from the timed denominator.
- Recorded race completions, clean rate from retained history evidence, and the
  number of classified clean-rate samples. Legacy completions with no inventory
  evidence are unknown. A telemetry `complete` result alone does not prove clean
  completion on every destination.
- Queue-wait median/p95 with sample counts. Announce-to-start median/p95 uses the
  latest observed matching release and section announce at or before each start;
  no earlier observation in the selected window means unknown latency.
- Download success from the newest terminal outcome per server and download ID
  in the window. Retry/resume events are not additional failures or successes.
- Site-health sample coverage, with overlapping sample windows counted once.
  Existing pool samples do not measure uptime between observations, so uptime is
  shown as unknown.

Failed transfers can contain an expected file size rather than delivered bytes;
these rows are excluded. New transfer rows identify the actual direct/relay path
and duplicate skips. Known duplicate skips count no payload or route speed.
Historical rows cannot distinguish duplicate skips and may overstate completed
payload; this is a telemetry estimate, not an adapter meter. Failed partial bytes,
protocol overhead and unrecorded traffic are not estimated.

Every report reserves capacity for each stream within a total budget: 5 seconds of cooperative processing time,
64 MiB of decompressed input, 250,000 rows, 640 files and 128 KiB per row. Reads
check cancellation while streaming. Downloads and site-health are read first;
unused capacity passes to later streams, and announcements are read last.
Per-stream coverage includes rows, the observed date range and partial-read reasons.
Incomplete download evidence has an unknown success rate and explicitly partial counts.
Dictionaries and samples are also bounded
(10,000 race/download joins, 4,096 daily site keys, 256 routes/sites and 10,000
health intervals). These limits constrain the report only. Reaching a limit or
encountering unreadable, malformed or oversized input marks the report partial;
it never holds operational work. Narrow the window for more complete coverage.
The time budget is checked between local file reads and callbacks; it cannot
interrupt a filesystem operation already in progress.

The reader handles JSONL and gzip, shares access with live writers, and prefers
raw JSONL when rotation has left a compressed twin. Optional malformed fields
are treated as unknown without discarding independent valid evidence. Text
identifiers pass through the current diagnostic secret redactor. No report is
automatically sent to the agent or another service.

CSV export, measured historical uptime, adapter/WAN attribution and reconstruction
of missing historical transport/duplicate facts are not included.
