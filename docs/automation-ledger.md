# Automation call ledger

`controlApi.security.ledgerEnabled` defaults to `true`. The ledger records local
automation calls without changing which operations may run. Disabling it stops new
recording; it does not remove existing evidence.

Accepted and terminal outcomes are separate, immutable events with the same call
identifier. Rows contain time, a stable hashed client identifier, the registered
operation, outcome and elapsed time. Client labels, bearer tokens, provider errors
and result bodies are excluded. Arguments retain bounded structure and value types;
their property names and scalar values are never stored.

The read-only `automation_ledger` tool returns up to 500 recent rows, storage health,
the pending-queue drop count and whether startup loading is still in progress.

`controlApi.security.ledgerCapacity` defaults to 2,000 retained in-memory events
(allowed range 100–10,000). This limits bookkeeping only: new events evict the
oldest. Background persistence appends to JSONL segments under `automation-ledger`,
retaining at most four 1 MiB segments. Older segments are removed without rewriting
surviving rows. A bounded pending queue also evicts its oldest event under pressure;
the ledger reports the dropped count.

Storage failures retain recent events in memory and warn once. Later writes retry.
Malformed segment data is copied to a restricted `.corrupt` companion when possible;
valid rows remain readable and fresh calls continue. Corrupt companions expire with
their corresponding segments. Ledger storage, saturation and recovery never gate
logins, races, joins or automation calls.
