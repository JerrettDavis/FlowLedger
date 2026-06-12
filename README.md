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
| Observability | OpenTelemetry, Serilog, Aspire dashboard |
| Testing | xUnit, FluentAssertions, Testcontainers, Playwright |
| CI | GitHub Actions |

## Prerequisites

- [.NET 10 SDK](https://dot.net/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop) (or Podman) — Aspire manages PostgreSQL and Redis containers
- Aspire workload: `dotnet workload install aspire`

## Quick start

```powershell
# Windows
./eng/scripts/run.ps1
```

```bash
# Linux / macOS / WSL
chmod +x eng/scripts/run.sh
./eng/scripts/run.sh
```

The script validates prerequisites, restores packages, builds, and launches the Aspire AppHost which orchestrates all services.

| URL | Purpose |
|---|---|
| https://localhost:15888 | Aspire dashboard (logs, traces, metrics, resources) |
| https://localhost:5001 | FlowLedger API |
| https://localhost:5002 | FlowLedger Web |

## Run tests

```powershell
# Windows
./eng/scripts/test.ps1
```

```bash
# Linux / macOS / WSL
./eng/scripts/test.sh
```

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

## Milestones

| Milestone | Status | Description |
|---|---|---|
| 0 — Repo foundation | In progress | Solution scaffold, Aspire, CI, one-script run |
| 1 — Core domain | Planned | Money, Account, Transaction, RecurringFlow domain model |
| 2 — Persistence & APIs | Planned | EF Core, migrations, account/transaction CRUD |
| 3 — Money Plan | Planned | Spreadsheet view, running balances, row statuses |
| 4 — Forecasting | Planned | Deterministic forecast engine, scenarios |
| 5 — Imports & matching | Planned | CSV import, duplicate detection, plan reconciliation |
| 6 — Goals & reports | Planned | Savings goals, trends, net-worth dashboard |
| 7 — MX integration | Planned | Bank aggregation behind feature flag |
| 8 — Self-hosted RC | Planned | Docker Compose, backup docs, full E2E |
| 9 — SaaS foundation | Planned | Multi-tenancy, admin console, billing abstraction |

## Contributing

See [CONTRIBUTING.md](./CONTRIBUTING.md).

## Security

See [SECURITY.md](./SECURITY.md) for the vulnerability disclosure policy.
