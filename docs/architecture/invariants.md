# System invariants

What FlowLedger promises must always hold, and which tests claim to check it. Tests declare the invariant they
support with `[Trait("Invariant", "INV-xx")]` (class or method level). `eng/check-invariants.py` runs in CI and
fails when an invariant listed here has no tagged test, except the explicit known gaps below.

Most evidence today is example-based. The "Next" column is the stronger technique to add (tracked in #178).

| ID | Invariant | Tagged tests | Next |
|---|---|---|---|
| INV-01 | No cross-tenant read or write on any route or store | `SimpleFinTenantIsolationTests` (feature level) | route-matrix integration test, SQL lint, optional Postgres RLS |
| INV-02 | Sync is idempotent: replaying any page sequence yields the same transaction set | `FinancialSyncServiceTests` | FsCheck model test with random replay, duplicate and reorder |
| INV-03 | A transfer pair sums to zero and both legs are excluded from income and expense | `TransferDetectorTests` | property tests |
| INV-04 | No mixed-currency arithmetic; allocations sum to the total | `MoneyTests` | property tests |
| INV-05 | Forecast closing balance = opening + sum(occurrences), deterministic | `ForecastEngineTests` | property test plus differential against a naive reference |
| INV-06 | Recurrence expansion is sorted, bounded, terminating, and month-end/leap-year safe | `RecurrenceExpanderTests` | property tests |
| INV-07 | Import is idempotent and never throws on arbitrary input | `ImportNormalizerTests` | fuzz/property tests |
| INV-08 | Migrations preserve data; the EF model equals the snapshot | none (**known gap**, #140) | upgrade tests, `HasPendingModelChanges` |
| INV-09 | At most one sync per (tenant, connection); the cursor advances only after a durable commit | `GuardedSyncServiceTests` | TCS-gated concurrency tests; small TLA+ model of lock, cursor commit, retry and crash |
| INV-10 | A webhook event is accepted at most once (signature, timestamp, replay window) | `WebhookReplayGuardTests` | property test over replay windows; HTTP negatives |
| INV-11 | Snapshot export/restore is lossless, including ad-hoc tables | `AdHocSchemaRestoreTests` | round-trip property test with random data |
