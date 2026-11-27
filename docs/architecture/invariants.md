# System invariants and their evidence

This page states the properties FlowLedger must preserve and maps each to the tests that exist today. It is the register for the
formal-invariants program (issue #178). The "Current evidence" column lists existing example-based tests; the "Target proof
technique" column is what is planned, **not** what exists. Where the evidence is "none", no test covers the invariant.

Status of the program: the register exists, every invariant has at least one test tagged `[Trait("Invariant","INV-xx")]`, and
`ci.yml` runs `eng/check-invariants.py`, which fails when an invariant has no tagged test. Property-based tests (CsCheck) cover
INV-03 to INV-07; there are no stateful-model or TLA+ artefacts yet. Filter a single invariant with
`dotnet test --filter "Invariant=INV-09"`.

| ID | Invariant | Current evidence (existing tests) | Target proof technique (planned) |
|---|---|---|---|
| INV-01 | No cross-tenant read or write on any route or store | Seeded model test `tests/FlowLedger.Infrastructure.Tests/Persistence/TenantIsolationModelTests.cs` (3 tenants, random rows, each reads exactly its own and cannot fetch another's by id); Per-feature tests: `tests/FlowLedger.Api.Tests/Security/DataAdminTenantTests.cs`, `ProductionTenantTests.cs`, `TenantAssertionTests.cs`; `tests/FlowLedger.Infrastructure.Tests/Sync/SimpleFinTenantIsolationTests.cs`. No route sweep | Route-matrix integration test, reflection test on filters, SQL lint, optional Postgres RLS |
| INV-02 | Sync is idempotent: replaying any page sequence yields the same transaction set | Example tests: `tests/FlowLedger.Infrastructure.Tests/Sync/FinancialSyncServiceTests.cs`, `EfSyncCursorStoreTests.cs`; stateful model test `SyncIdempotencyModelTests.cs` (seeded random page subsets, reorders and repeats against a set model) | Deterministic model test exists; a model with failures and cursor commits is not written |
| INV-03 | A transfer pair sums to zero and both legs are excluded from income and expense | Example tests: `tests/FlowLedger.Application.Tests/Transactions/TransferDetectorTests.cs`; property tests in `Properties/ApplicationPropertyTests.cs` (one-to-one pairing, pairs net to zero, both legs left out of income and spending) | Done |
| INV-04 | No mixed-currency arithmetic; allocations sum to the total | Example tests: `tests/FlowLedger.Domain.Tests/ValueObjects/MoneyTests.cs` | Property tests |
| INV-05 | Forecast closing = opening + sum of occurrences, deterministic | Example tests: `tests/FlowLedger.Application.Tests/Features/Forecasting/ForecastEngineTests.cs` | Property tests plus differential test against a naive reference |
| INV-06 | Recurrence expansion is sorted, bounded, terminating and month-end/leap-year safe | Example tests: `tests/FlowLedger.Domain.Tests/ValueObjects/RecurrenceExpanderTests.cs` | Property tests |
| INV-07 | Import is idempotent and never throws on arbitrary input | Example tests: `tests/FlowLedger.Application.Tests/Imports/ImportNormalizerTests.cs` | Fuzz / property tests |
| INV-08 | Migrations preserve data; the EF model equals the snapshot | `tests/FlowLedger.Infrastructure.Tests/Persistence/MigrationTests.cs` (model-snapshot drift, upgrade paths) | Upgrade tests, `HasPendingModelChanges` check |
| INV-09 | At most one sync per (tenant, connection); the cursor advances only after a durable commit | `tests/FlowLedger.Infrastructure.Tests/Sync/GuardedSyncServiceTests.cs`, `tests/FlowLedger.Worker.Tests/TenantFanOutTests.cs` (the latter is timing-based); stateful model test `SyncExclusionModelTests.cs` (random start/finish/fail/wait interleavings across 3 tenants checked against a model: one in-flight sync per tenant, nothing inside the minimum interval reaches the provider, the last-sync commit moves only on success, the slot is released) | Small TLA+ model of lock, cursor commit, retry and crash; deterministic concurrency tests gated on `TaskCompletionSource` |
| INV-10 | A webhook event is accepted at most once (signature, timestamp, replay window) | `tests/FlowLedger.Integrations.Tests/Mx/MxWebhookVerifierTests.cs`, `tests/FlowLedger.Api.Tests/Endpoints/WebhookReplayGuardTests.cs`; no HTTP-level negative tests for every endpoint | Property test over replay windows; HTTP negative tests |
| INV-11 | Snapshot export and restore is lossless, including the host-wide tables | `tests/FlowLedger.Infrastructure.Tests/Persistence/AdHocSchemaTests.cs` (`AdHocSchemaRestoreTests`), `DatabaseSnapshotTests.cs`; seeded random-data round trip `ExportImportRandomDataTests.cs` (random tenants, accounts and transactions with CSV-hostile text, exported and imported into an empty database, each table compared by content hash) | Done for accounts and transactions; extend to the remaining tenant tables |

## Why these

- A mis-detected transfer double-counts income and expense (INV-03).
- Sync replays happen in normal operation (webhooks plus scheduled syncs plus overlap windows), so idempotency must hold (INV-02, INV-09).
- Tenant isolation must hold on every route and store, not only the ones with a feature test (INV-01).
- TLA+ is worth the effort only for INV-09 (lock and cursor-commit ordering under retries and crashes); the rest are served by property tests and stateful model tests.

## Remaining work (not done)

1. A route sweep for INV-01 (a stateful model of INV-09 and a random-data round trip for INV-11 now exist).
2. A small TLA+ model for INV-09 (lock plus cursor-commit ordering under retry and crash).
3. The child testing issues referenced by #178 (mutation testing, coverage gates, E2E auth, wall-clock tests and others) remain open.
