# System invariants and their evidence

This page states the properties FlowLedger must preserve and maps each to the tests that exist today. It is the register for the
formal-invariants program (issue #178). The "Current evidence" column lists existing example-based tests; the "Target proof
technique" column is what is planned, **not** what exists. Where the evidence is "none", no test covers the invariant.

Status of the program: the register exists, every invariant has at least one test tagged `[Trait("Invariant","INV-xx")]`, and
`ci.yml` runs `eng/check-invariants.py`, which fails when an invariant has no tagged test. Property-based tests (CsCheck) cover
INV-03 to INV-07; stateful model tests cover INV-01, INV-02, INV-09 and INV-11, and INV-09 also has a TLA+ model in `docs/architecture/inv-09-sync-lock/`. Filter a single invariant with
`dotnet test --filter "Invariant=INV-09"`.

| ID | Invariant | Current evidence (existing tests) | Target proof technique (planned) |
|---|---|---|---|
| INV-01 | No cross-tenant read or write on any route or store | Seeded model test `tests/FlowLedger.Infrastructure.Tests/Persistence/TenantIsolationModelTests.cs` (3 tenants, random rows, each reads exactly its own and cannot fetch another's by id); Per-feature tests: `tests/FlowLedger.Api.Tests/Security/DataAdminTenantTests.cs`, `ProductionTenantTests.cs`, `TenantAssertionTests.cs`; `tests/FlowLedger.Infrastructure.Tests/Sync/SimpleFinTenantIsolationTests.cs`; route sweep over the whole manifest `tests/FlowLedger.Api.Tests/Security/TenantIsolationMatrixTests.cs` (B sees none of A's rows and cannot change them) and, tagged `INV-01`, `TenantIsolationRouteSweepTests.cs` (both tenants seeded; every parameterless tenant-scoped GET route in `TenantIsolationManifest` (kind `List`) returns only the caller's rows; GETs by id are covered by the matrix test and the write-style GETs by `TenantIsolationWriteTests`) | Reflection test on filters, SQL lint, optional Postgres RLS |
| INV-02 | Sync is idempotent: replaying any page sequence yields the same transaction set | Example tests: `tests/FlowLedger.Infrastructure.Tests/Sync/FinancialSyncServiceTests.cs`, `EfSyncCursorStoreTests.cs`; stateful model test `SyncIdempotencyModelTests.cs` (seeded random page subsets, reorders and repeats against a set model) | Deterministic model test exists; a model with failures and cursor commits is not written |
| INV-03 | A transfer pair sums to zero and both legs are excluded from income and expense | Example tests: `tests/FlowLedger.Application.Tests/Transactions/TransferDetectorTests.cs`; property tests in `Properties/ApplicationPropertyTests.cs` (one-to-one pairing, pairs net to zero, both legs left out of income and spending) | Done |
| INV-04 | No mixed-currency arithmetic; allocations sum to the total | Example tests: `tests/FlowLedger.Domain.Tests/ValueObjects/MoneyTests.cs` | Property tests |
| INV-05 | Forecast closing = opening + sum of occurrences, deterministic | Example tests: `tests/FlowLedger.Application.Tests/Features/Forecasting/ForecastEngineTests.cs` | Property tests plus differential test against a naive reference |
| INV-06 | Recurrence expansion is sorted, bounded, terminating and month-end/leap-year safe | Example tests: `tests/FlowLedger.Domain.Tests/ValueObjects/RecurrenceExpanderTests.cs` | Property tests |
| INV-07 | Import is idempotent and never throws on arbitrary input | Example tests: `tests/FlowLedger.Application.Tests/Imports/ImportNormalizerTests.cs` | Fuzz / property tests |
| INV-08 | Migrations preserve data; the EF model equals the snapshot | `tests/FlowLedger.Infrastructure.Tests/Persistence/MigrationTests.cs` (model-snapshot drift, upgrade paths) | Upgrade tests, `HasPendingModelChanges` check |
| INV-09 | At most one sync per (tenant, connection); the cursor advances only after a durable commit | `tests/FlowLedger.Infrastructure.Tests/Sync/GuardedSyncServiceTests.cs`, `tests/FlowLedger.Worker.Tests/TenantFanOutTests.cs` (the latter is timing-based); stateful model test `SyncExclusionModelTests.cs` (random start/finish/fail/wait interleavings across 3 tenants checked against a model: one in-flight sync per tenant, nothing inside the minimum interval reaches the provider, the last-sync commit moves only on success, the slot is released) | TLA+ model of lock, cursor commit, retry and crash: `docs/architecture/inv-09-sync-lock/SyncLock.tla` (model-checked by TLC in the nightly `tlc-inv09` CI job and on `workflow_dispatch`; how to run it locally is in its README); deterministic concurrency tests gated on `TaskCompletionSource` |
| INV-10 | A webhook event is accepted at most once (signature, timestamp, replay window) | `tests/FlowLedger.Integrations.Tests/Mx/MxWebhookVerifierTests.cs`, `tests/FlowLedger.Api.Tests/Endpoints/WebhookReplayGuardTests.cs`; no HTTP-level negative tests for every endpoint | Property test over replay windows; HTTP negative tests |
| INV-11 | Snapshot export and restore is lossless, including the host-wide tables | `tests/FlowLedger.Infrastructure.Tests/Persistence/AdHocSchemaTests.cs` (`AdHocSchemaRestoreTests`), `DatabaseSnapshotTests.cs`; seeded random-data round trip `ExportImportRandomDataTests.cs` (random tenants, accounts and transactions with CSV-hostile text, exported and imported into an empty database, each table compared by content hash) | Done for accounts and transactions; extend to the remaining tenant tables |

## Why these

- A mis-detected transfer double-counts income and expense (INV-03).
- Sync replays happen in normal operation (webhooks plus scheduled syncs plus overlap windows), so idempotency must hold (INV-02, INV-09).
- Tenant isolation must hold on every route and store, not only the ones with a feature test (INV-01).
- TLA+ is worth the effort only for INV-09 (lock and cursor-commit ordering under retries and crashes); the rest are served by property tests and stateful model tests.

## Remaining work

1. Run `SyncLock.tla` through TLC once (it has been written but never model-checked) and consider a scheduled CI job for it (see `docs/architecture/inv-09-sync-lock/README.md`).
2. INV-01: reflection test on query filters, SQL lint and optional Postgres row-level security are not written.
3. INV-11 is covered for accounts and transactions only; the remaining tenant tables are not in the random round trip.

## Child issues

State as of this revision (from GitHub). Closed means the issue is closed; it does not by itself mean every idea in it shipped.

| Issue | State | Topic |
|---|---|---|
| #131 | closed | Property-based and fuzz tests for money, forecast, recurrence, transfer, import |
| #132 | closed | Mutation testing (Stryker) |
| #134 | closed | Coverage enforcement and tracking |
| #136 | closed | Systematic tenant isolation tests |
| #138 | open | E2E passes vacuously when unconfigured; CI stack uses anonymous auth |
| #140 | closed | Migration upgrade-path and model-drift tests |
| #144 | closed | Wall-clock reads in tests |
| #148 | closed | Sleep-based synchronisation in concurrency tests |
| #151 | closed | Docker-gated tests silently skipping in CI |
| #153 | closed | FluentAssertions licensing |
| #155 | closed | Provider contract tests |
| #158 | closed | Route-sweep authorisation and webhook negative tests |
| #161 | closed | BDD suite and ADR 0002 drift |
| #165 | closed | Test-with-fix policy and PR template |
| #167 | closed | Web component and JS tests in CI |
| #170 | closed | Wall-clock performance tests in the PR gate |
| #173 | open | Shared test data builders (single-argument requests and the wide response DTOs in the round-trip tests are done; positional constructions elsewhere remain) |
| #176 | closed | Scheduled nightly verification |

Open and therefore deferred: #138 (an OIDC-authenticated E2E flow) and #173 (the long tail of positional constructions in the other tests).
