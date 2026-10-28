# Waiting states

Downloads, queued races and the Mounts cards explain their current limiting constraint. The same diagnostic snapshots are available as `waiting` in `downloads.list` and `servers.list`/`servers.get`, and as `queuedWaiting` (keyed by request ID) in `spread.status`.

Snapshots expose a stable `reasonCode`, `limitingConstraint`, `nextCheckAtUtc`, `estimatedReadyAtUtc` and, for eligible races, `queuePosition`. They do not change admission or scheduling. The next check is a scheduled observation, not a promise that work will start. Site readiness and source completion have unknown ETAs. Race start estimates use observed completed-race durations and current race capacity; new arrivals, site failures and the size-aware queue can change them.

Download reasons include the configured schedule, concurrency slot, missing destination drive, network allowance, incomplete source, source relocation, SFV repair, racing priority and ordinary retries. Existing error detail remains visible. When a retry returns to the queue, its explanation changes to awaiting a download slot and normal admission, with no expired retry deadline. Runtime retry timestamps are not persisted as new gates; after restart a timestamp stays unknown until the scheduler establishes one.

Site explanations include account login usage and transfer ceilings. Authentication, login-budget and BNC holds remain visible even while existing pooled connections can be reused. The displayed deadline takes the latest applicable account, health and remount deadline. Disabled health or login supervision does not contribute a stale deadline.

This read-only view does not offer an early network **Check now**. `canCheckNow` is false with an explanation: checks retain the existing account and site retry schedule. Existing explicit recovery controls are unchanged. No new polling, connection pool, setting or default-on rate limit is introduced.

Starting a race from the dashboard or control API reports the actual admission outcome, including duplicates, suppressed requests and unavailable sites, instead of claiming that every request was queued. Login-budget inspection does not allocate new account state.
