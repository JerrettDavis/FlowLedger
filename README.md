# FlowLedger

> Know where your money went, where it is going, and when your plans become possible.

FlowLedger is a FOSS-first, self-hostable, cloud-native personal finance platform for forecasting, budgeting, asset tracking, transaction intelligence, and extensible money workflows.

## What it does

FlowLedger combines four traditionally separate tools into one system:

1. A spreadsheet-like temporal money plan.
2. A Mint/Simplifi-style transaction and account aggregator.
3. An asset, debt, net-worth, and savings planner.
4. An extensible workflow platform for financial automation.

## License

[AGPL-3.0-only](./LICENSE) — free and open source. The self-hosted version is first-class, not a crippled community edition.

## Tech stack

| Layer | Technology |
|---|---|
| Runtime | .NET 10, C# 14, ASP.NET Core 10 |
| Orchestration | .NET Aspire 13.4 |
| Frontend | Blazor Web App (interactive auto), MudBlazor 9 |
| API | ASP.NET Core Minimal APIs, vertical slices |
| Database | PostgreSQL (EF Core 10 + Npgsql) |
| Cache / locks | Redis |
| Scheduler | Quartz.NET |
| Identity / Auth | ASP.NET Core Identity + OpenIddict |
| Testing | xUnit, FluentAssertions, Testcontainers, Playwright E2E, TinyBDD, PatternKit.Core (dogfooded) |
| Observability | OpenTelemetry, Serilog, Aspire dashboard |
| Security | CodeQL, Dependabot, SCA via dotnet list package --vulnerable |
| CI / CD | GitHub Actions |

## Prerequisites

- [.NET 10 SDK](https://dot.net/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop) (or Podman)
- Aspire workload: `dotnet workload install aspire`

**Windows SDK Note:** Before running tests locally on Windows, set these environment variables to work around a JIT regression:

```powershell
$env:DOTNET_ReadyToRun = "0"
$env:COMPlus_ReadyToRun = "0"
```

CI (Linux) does not need these.

## Quick start

### Development (Aspire inner loop)

The fast inner loop. Orchestrates API, Worker, Web, Postgres, and Redis with service discovery and a dashboard.

```powershell
dotnet run --project src/FlowLedger.AppHost
```

Open the Aspire dashboard at `https://localhost:15888` to see all services and their logs.

| URL | Purpose |
|---|---|
| https://localhost:15888 | Aspire dashboard |
| https://localhost:5001 | FlowLedger API |
| https://localhost:5002 | FlowLedger Web |

### Full Stack (Docker Compose, self-hosting / E2E)

```bash
docker compose -f docker-compose.full.yml up
```

## Run tests

All tests except E2E (which require the full stack running):

```bash
dotnet test FlowLedger.slnx --filter "Category!=E2E" --configuration Release
```

**Test suite:** ~499+ passing tests across unit, integration, BDD, and performance categories.

See [docs/development/testing.md](docs/development/testing.md) for detailed testing guide.

## Architecture

FlowLedger uses a modular monolith with vertical slice feature organization and clean architecture dependency direction.

```
src/
  FlowLedger.AppHost/          # Aspire orchestration
  FlowLedger.ServiceDefaults/  # Shared OpenTelemetry, health checks, service discovery
  FlowLedger.Api/              # ASP.NET Core Minimal API (vertical slices)
  FlowLedger.Web/              # Blazor Web App (server)
  FlowLedger.Web.Client/       # Blazor WebAssembly (auto interactivity client)
  FlowLedger.Worker/           # Background worker host (Quartz jobs)
  FlowLedger.Application/      # Vertical slice handlers, validators
  FlowLedger.Domain/           # Domain model, aggregates, value objects, events
  FlowLedger.Infrastructure/   # EF Core, Npgsql, Redis, object storage
  FlowLedger.SharedKernel/     # Shared interfaces (ITenantContext, IObjectStorage, etc.)
  FlowLedger.Integrations.Mx/  # MX financial data aggregation boundary
  FlowLedger.Plugins.Abstractions/  # Plugin surface interfaces

tests/
  FlowLedger.Domain.Tests/
  FlowLedger.Application.Tests/
  FlowLedger.Architecture.Tests/
```

Dependency direction (enforced by architecture tests):

```
Web / Api / Worker -> Application -> Domain
Infrastructure -> Application abstractions + Domain
Integrations -> Application abstractions + Domain contracts (never the reverse)
```

## Status

**Phases 1–8 complete.** All core features implemented and verified green.

| Phase | Status | Coverage |
|---|---|---|
| 0 — Repo foundation | ✅ Complete | Aspire, CI, multi-run modes |
| 1 — Core domain | ✅ Complete | Money, Account, Transaction, RecurringFlow domain model |
| 2 — Persistence & APIs | ✅ Complete | EF Core, Npgsql, CRUD endpoints |
| 3 — Money Plan | ✅ Complete | Spreadsheet view, running balances, row statuses |
| 4 — Forecasting | ✅ Complete | Deterministic forecast engine, goal affordability |
| 5 — Imports & matching | ✅ Complete | RFC-4180 CSV, transaction matching |
| 6 — Goals & reports | ✅ Complete | Savings goal tracking, low-water marks, overdraft warnings |
| 7 — MX integration | ✅ Complete | Bank aggregation, feature-flagged, webhook sync |
| 8 — Final release | ✅ Complete | Performance gates, security CI, docs, full green sweep |

## MX Integration

By default, FlowLedger uses simulated (fake) bank data — no credentials needed. To enable real MX.com data, set `Mx:Enabled=true` and provide your API credentials. See [docs/architecture/mx-integration.md](docs/architecture/mx-integration.md) for the full guide.

## Contributing

See [CONTRIBUTING.md](./CONTRIBUTING.md).

## Security

See [SECURITY.md](./SECURITY.md) for the vulnerability disclosure policy.
