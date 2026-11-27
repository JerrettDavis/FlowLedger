# INV-09 model: sync lock and cursor commit

`SyncLock.tla` is a small TLA+ model of INV-09 (issue #178): **at most one sync runs per tenant, and the cursor advances only after a
durable commit**. It abstracts `GuardedSyncService` (the in-process gate plus the database advisory lock held for the whole sync) and the
cursor store.

## What is modelled

- Three processes (the API, the webhook job, the Worker) contend for one tenant's advisory lock (`TryLock`, `Skip`: skipped, never queued).
- A holder fetches (`Fetch`), then commits rows and cursor in one transaction (`Commit`), then releases (`Release`).
- The holder can fail after taking the lock (`Fail`, e.g. provider error or throttle) and the process can crash at any step (`Crash`).
  The advisory lock is session scoped, so a crash drops it; staged, uncommitted pages are lost.
- Retry is the process looping back to `idle`.

## Properties checked

| Name | Kind | Meaning |
|---|---|---|
| `MutualExclusion` | invariant | at most one process is past the lock |
| `LockMatchesPhase` | invariant | only the lock holder is past the lock |
| `CursorOnlyAfterCommit` | invariant | cursor equals the number of durable commits: it never moves without one, and a fetched but uncommitted page set never shows through it |
| `CursorMonotonic` | temporal property | the cursor never goes backwards |

`Commit` stages `cursor + 1` at fetch time and commits it later, so `CursorOnlyAfterCommit` also fails if the lock is ever removed (two
fetches would stage the same value): the model is sensitive to the lock, not vacuous. To see that, delete the `lockHolder = "none"` guard
from `TryLock` and run TLC; it reports a `MutualExclusion` violation.

## How to run TLC

Requires Java 11 or later.

```bash
# fetch the TLA+ tools jar once (https://github.com/tlaplus/tlaplus/releases), then from this folder:
java -cp tla2tools.jar tlc2.TLC -config SyncLock.cfg SyncLock.tla
```

Or open `SyncLock.tla` in the TLA+ Toolbox / VS Code TLA+ extension and run the model checker with `SyncLock.cfg`. Expected result:
`Model checking completed. No error has been found.` The state space is small (three processes, `MaxCursor = 4`); raise `MaxCursor` in `SyncLock.cfg` to explore longer histories.

## Status and limits

- The model has not been run through TLC yet (no TLC runner was available when it was written) and is not part of CI; run it once with
  the command above before relying on it. The deterministic counterpart that does run in CI is
  `SyncExclusionModelTests.cs` and `GuardedSyncServiceTests.cs` (tagged `INV-09`).
- Not modelled: the minimum-interval throttle (it only adds more `Fail` paths), several connections per tenant (the guard is per tenant
  today), and database failover.
