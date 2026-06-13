# ADR-0002: BDD Test Library Selection

**Date:** 2026-06-12  
**Status:** Accepted  
**Deciders:** FlowLedger maintainers

## Context

Phase 6b adds a behaviour-driven (BDD) acceptance suite that exercises FlowLedger's real
Application and Infrastructure layers — forecasting, planned-vs-actual matching, CSV import/dedup,
and provider sync (Simulated and MX) — directly against a Testcontainers PostgreSQL database,
without going through HTTP. We needed a BDD library that:

- Targets **.NET 10** (the solution's only TFM; `TreatWarningsAsErrors=true` and
  `EnforceCodeStyleInBuild=true` globally).
- Integrates with the existing **xUnit + Testcontainers + Respawn** harness used by the other
  integration test projects.
- Expresses Given/When/Then scenarios that drive real handlers and EF repositories.
- Avoids a heavyweight `.feature`/Gherkin parsing toolchain and code-generation step when a
  code-first fluent DSL is sufficient for an engineering-owned acceptance suite.

Two candidates were considered:

1. **Reqnroll.xUnit** — the actively-maintained SpecFlow successor. Gherkin `.feature` files plus
   generated step-binding classes.
2. **TinyBDD + TinyBDD.Xunit** (`0.19.23`, by JerrettDavis) — a lightweight, code-first fluent
   Given/When/Then library with a dedicated xUnit adapter base class.

A time-boxed spike confirmed TinyBDD `0.19.23` **restores, builds, and runs on net10.0**: both
packages ship a native `net10.0` `lib/` TFM (alongside net8.0, net9.0, netstandard2.0/2.1, and
net46x/47x/48x). The trivial spike scenario compiled and passed under xUnit on .NET 10.0.9.

## Decision

Adopt **TinyBDD `0.19.23` + TinyBDD.Xunit `0.19.23`** for the BDD acceptance suite
(`tests/FlowLedger.Bdd.Tests`).

- Scenarios inherit `TinyBDD.Xunit.TinyBddXunitBase` (ctor takes `ITestOutputHelper`) and use the
  ambient fluent API: `await Given(...).When(...).Then(...).And(...).AssertPassed()`.
- Each scenario is a `[Feature]`-annotated **`partial`** class with `[Scenario]`-annotated test
  methods (here `[DockerFact]` so they skip cleanly without Docker).
- Scenarios drive the real `GetForecastHandler`, `MatchingEngine`, `ImportTransactionsHandler`, and
  `IFinancialSyncService` resolved from a DI container that composes the genuine `AddApplication()`
  + `AddInfrastructure(config)` registrations, overriding only the connection string (Testcontainers
  Postgres), the `ITenantContext` (a deterministic test tenant), and provider configuration. A
  shared `BddTestFixture` is wired via an `ICollectionFixture` (`[Collection("BddIntegration")]`)
  so the Postgres container is started once for the whole suite.
- The MX sync scenario wires the real MX provider through the **public `AddMxProvider(config)` DI
  extension** (config `BaseUrl` pointed at an in-project WireMock server) — option (b). No
  `InternalsVisibleTo` is added to `FlowLedger.Integrations.Mx` for the BDD project.
- **Reqnroll was not needed.** The acceptance suite is engineering-owned and benefits more from a
  code-first DSL that lives next to the handlers it drives than from a separate Gherkin toolchain.
  Reqnroll does ship a .NET 8/9-compatible release and would have been the fallback had TinyBDD
  failed to build or run on net10.0; the spike showed it did not fail, so the lighter dependency won.

### Required workarounds (TinyBDD 0.19.23)

1. **Source-generator nullable bug — scoped `<NoWarn>`.** TinyBDD ships a source generator
   (`TinyBDD.SourceGenerators`) that, for a `partial` scenario class, emits an optimised
   `#nullable enable` method whose generated code itself violates nullable analysis and declares an
   unused catch variable. Under the solution's `TreatWarningsAsErrors=true` these become build
   errors. The BDD project therefore scopes:

   ```xml
   <NoWarn>$(NoWarn);CS8602;CS8625;CS0168</NoWarn>
   ```

   This suppression is local to `FlowLedger.Bdd.Tests.csproj` with an explanatory comment; remove it
   once the generator is fixed upstream.

2. **`partial`-class requirement.** The generator hard-errors (`TBDD010`) on a non-partial scenario
   class. All scenario classes are declared `partial`. Class-level opt-out is not available
   (`[DisableOptimization]` is method-level only).

3. **`[DisableOptimization]` on scenarios that use local-function step delegates.** The source
   generator inlines lambda bodies into a generated method where **local functions / method groups
   used as step delegates are out of scope**, producing "name does not exist" errors. Routing such
   scenarios through the runtime ambient path with the method-level `[DisableOptimization]` attribute
   resolves them correctly. The scenarios use local async functions (rather than inline async
   lambdas) because TinyBDD's `Given/When/And` expose both `Func<Task<T>>` and `Func<ValueTask<T>>`
   overloads, making bare `async` lambdas ambiguous; a typed local function binds unambiguously.

## Scenario 6 deferral — LowBalanceWarning domain event

Scenario 6 ("forecast surfaces a low-balance signal") was specified against a first-class
`LowBalanceWarning` domain event. **That domain event is intentionally deferred.** The forecast
engine (`ForecastEngine`/`IForecastEngine`) is a pure, deterministic, side-effect-free read model —
raising a domain event from it would violate that contract, and introducing the event would require
a new `src` domain type that is out of scope for this phase.

Instead, the scenario delivers the **behaviour**: given an account driven below zero by a large
recurring debit, the forecast's existing `OverdraftWarnings` collection is populated and/or
`AggregateLowWaterMark.MinBalance` is negative. The overdraft / low-water-mark output already
provides the low-balance signal, so no new domain event is needed to satisfy the intent. A
`// TODO (Phase 6 deferral)` comment in `ForecastScenarios.cs` records this. A first-class
`LowBalanceWarning` `IDomainEvent` + handler can be added in a later phase if a push/notification
side effect is required; it would consume the same forecast output.

## Consequences

### Positive

- Native `net10.0` binaries — no netstandard fallback, no TFM friction.
- Code-first scenarios live beside the handlers they exercise; no `.feature` files or binding
  generation to maintain.
- Reuses the established xUnit + Testcontainers + Respawn harness; scenarios share a single
  Postgres container via an `ICollectionFixture`.
- Driving the real DI graph (`AddApplication` + `AddInfrastructure`) means the suite validates the
  same wiring the API host uses, not a hand-rolled substitute.
- Zero new heavyweight dependencies; TinyBDD is small and self-contained.

### Negative

- The source generator's nullable bug forces a scoped `<NoWarn>` and a `[DisableOptimization]`
  attribute on local-function-based scenarios — minor papercuts tracked for removal once fixed
  upstream.
- TinyBDD's documentation advertises ".NET 8 or 9"; net10.0 support is empirically confirmed by the
  spike but is not an advertised guarantee, so a future TinyBDD release could regress it.
- `Then`/`And` cannot transform the carried scenario type (they assert on the value produced by the
  last `When`), so scenarios thread state via closures rather than re-typing through the chain.

### Neutral

- Reqnroll remains a viable fallback if TinyBDD is ever dropped; the scenarios are plain xUnit tests
  driving public handlers, so the BDD wrapper could be swapped without touching the system under test.
- The BDD project is registered in `FlowLedger.slnx` under `/tests/` and skips cleanly (via
  `[DockerFact]`) in environments without Docker.

## Alternatives considered

| Library | Outcome |
|---------|---------|
| **TinyBDD + TinyBDD.Xunit 0.19.23** | **Adopted** — native net10.0, code-first, integrates with xUnit + Testcontainers. |
| `Reqnroll.xUnit` (Gherkin .feature files) | Considered fallback; not needed. Ships a .NET 8/9-compatible release, but the lighter code-first dependency won once the TinyBDD net10.0 spike passed. |

## References

- Phase 6b spike report (TinyBDD net10.0 compile-and-run verification).
- `tests/FlowLedger.Bdd.Tests/` — six scenarios across forecasting, matching, import, and sync.
