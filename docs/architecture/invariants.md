# System invariants and their evidence

This page states the properties FlowLedger must preserve and maps each to the tests that exist today. It is the register for the
formal-invariants program (issue #178). The "Current evidence" column lists existing example-based tests; the "Target proof
technique" column is what is planned, **not** what exists. Where the evidence is "none", no test covers the invariant.

Status of the program: the register exists. Tests are **not yet tagged** with `[Trait("Invariant","INV-xx")]` and there is **no CI step**
that fails when an invariant has no test. No property-based, fuzz, stateful-model or TLA+ artefacts exist in the repository yet.

| ID | Invariant | Current evidence (existing tests) | Target proof technique (planned) |
|---|---|---|---|
| INV-01 | No cross-tenant read or write on any route or store | Per-feature tests: `tests/FlowLedger.Api.Tests/Security/DataAdminTenantTests.cs`, `ProductionTenantTests.cs`, `TenantAssertionTests.cs`; `tests/FlowLedger.Infrastructure.Tests/Sync/SimpleFinTenantIsolationTests.cs`. No route sweep | Route-matrix integration test, reflection test on filters, SQL lint, optional Postgres RLS |
| INV-02 | Sync is idempotent: replaying any page sequence yields the same transaction set | Example tests: `tests/FlowLedger.Infrastructure.Tests/Sync/FinancialSyncServiceTests.cs`, `EfSyncCursorStoreTests.cs` | FsCheck model test with random replay, duplicate and reorder |
| INV-03 | A transfer pair sums to zero and both legs are excluded from income and expense | Example tests: `tests/FlowLedger.Application.Tests/Transactions/TransferDetectorTests.cs` | Property tests |
| INV-04 | No mixed-currency arithmetic; allocations sum to the total | Example tests: `tests/FlowLedger.Domain.Tests/ValueObjects/MoneyTests.cs` | Property tests |
| INV-05 | Forecast closing = opening + sum of occurrences, deterministic | Example tests: `tests/FlowLedger.Application.Tests/Features/Forecasting/ForecastEngineTests.cs` | Property tests plus differential test against a naive reference |
| INV-06 | Recurrence expansion is sorted, bounded, terminating and month-end/leap-year safe | Example tests: `tests/FlowLedger.Domain.Tests/ValueObjects/RecurrenceExpanderTests.cs` | Property tests |
| INV-07 | Import is idempotent and never throws on arbitrary input | Example tests: `tests/FlowLedger.Application.Tests/Imports/ImportNormalizerTests.cs` | Fuzz / property tests |
| INV-08 | Migrations preserve data; the EF model equals the snapshot | None found (no migration upgrade-path or model-drift test) | Upgrade tests, `HasPendingModelChanges` check |
| INV-09 | At most one sync per (tenant, connection); the cursor advances only after a durable commit | `tests/FlowLedger.Infrastructure.Tests/Sync/GuardedSyncServiceTests.cs`, `tests/FlowLedger.Worker.Tests/TenantFanOutTests.cs` (the latter is timing-based) | Small TLA+ model of lock, cursor commit, retry and crash; deterministic concurrency tests gated on `TaskCompletionSource` |
| INV-10 | A webhook event is accepted at most once (signature, timestamp, replay window) | `tests/FlowLedger.Integrations.Tests/Mx/MxWebhookVerifierTests.cs`, `tests/FlowLedger.Api.Tests/Endpoints/WebhookReplayGuardTests.cs`; no HTTP-level negative tests for every endpoint | Property test over replay windows; HTTP negative tests |
| INV-11 | Snapshot export and restore is lossless, including ad-hoc tables | `tests/FlowLedger.Infrastructure.Tests/Persistence/AdHocSchemaTests.cs` (`AdHocSchemaRestoreTests`), `DatabaseSnapshotTests.cs` | Round-trip property test on random data |

## Why these

- A mis-detected transfer double-counts income and expense (INV-03).
- Sync replays happen in normal operation (webhooks plus scheduled syncs plus overlap windows), so idempotency must hold (INV-02, INV-09).
- Tenant isolation must hold on every route and store, not only the ones with a feature test (INV-01).
- TLA+ is worth the effort only for INV-09 (lock and cursor-commit ordering under retries and crashes); the rest are served by property tests and stateful model tests.

## Remaining work (not done)

1. Tag the existing tests with `[Trait("Invariant","INV-xx")]` once the mapping above is reviewed.
2. Add a CI step that lists any invariant with no tagged test and fails.
3. Write the missing tests (INV-08 first, since nothing covers it) and the property/model tests in the table.
4. Child testing issues referenced by #178 (property tests, mutation testing, coverage gates, route sweep, migration tests, wall-clock tests, and others) remain open.
