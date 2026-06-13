# Local Development Guide

FlowLedger offers two dev loop options. Pick one — they are **mutually exclusive** and
should not run simultaneously.

## Option A — Aspire (recommended for active development)

Aspire orchestrates all services with hot-reload, the Aspire dashboard, and automatic
service discovery wiring.

**Prerequisites:** .NET 10 SDK, Docker Desktop, Aspire workload

```bash
# Install the Aspire workload (once)
dotnet workload install aspire

# Start everything
./eng/scripts/run.ps1    # Windows
./eng/scripts/run.sh     # Linux / macOS
```

This starts:
- **Postgres** and **Redis** as Aspire-managed containers
- **API** at https://localhost:5001
- **Web** at https://localhost:5002
- **Worker** (background job host)
- **Aspire dashboard** at https://localhost:15888

Migrations are applied automatically at startup (Development environment).
Demo data is loaded on first connect+sync from the Web UI or by calling the seed script.

### Aspire inner loop with pre-started infra

If you want infra managed by compose (e.g., to preserve data across Aspire restarts):

```bash
# Terminal 1 — start only infra
docker compose --profile infra up -d

# Terminal 2 — run Aspire (it will connect to the existing compose postgres + redis)
./eng/scripts/run.ps1
```

Aspire uses the connection strings injected by the Aspire PostgreSQL/Redis resources,
which point to `localhost:5432` and `localhost:6379` — the same ports exposed by compose.

## Option B — Docker Compose (full stack)

Use when you want a fully containerised environment without a local .NET SDK.

```bash
cp .env.example .env
docker compose --profile full up --build
```

See [docker-compose.md](../self-hosting/docker-compose.md) for the complete guide.

**Note:** Aspire and compose CANNOT run at the same time. Compose exposes Postgres on
`5432` and Redis on `6379`. If Aspire is already running its own containers on those
ports, `docker compose --profile full up` will fail with a port conflict. Stop one before
starting the other.

## Dev credentials

| Secret           | Value                                  | Notes                                      |
|------------------|----------------------------------------|--------------------------------------------|
| API key          | `dev-local-key-not-for-production`     | Defined in `appsettings.Development.json` |
| Tenant ID        | `00000000-0000-0000-0000-000000000001` | Hard-coded demo tenant                     |
| Postgres user    | `flowledger`                           | Database: `flowledger`                     |
| Postgres password | `dev_only_change_in_prod` (compose)   | Or Aspire-generated in Aspire mode         |

## Running tests

```bash
./eng/scripts/test.ps1    # Windows
./eng/scripts/test.sh     # Linux / macOS
```

Integration tests use Testcontainers to spin up an isolated Postgres instance.
They do NOT require a running compose stack or Aspire session.

## Seeding demo data

```bash
# After the stack is up (Aspire or compose):
./eng/scripts/seed.ps1    # Windows
./eng/scripts/seed.sh     # Linux / macOS
```

This calls `POST /api/connect` and `POST /api/sync` against the Simulated provider,
which generates a deterministic demo household (checking, savings, credit, mortgage
accounts + 4 months of transaction history).

## Useful endpoints (Development)

| Endpoint                        | Description                      |
|---------------------------------|----------------------------------|
| `GET  /alive`                   | Liveness check (always 200)      |
| `GET  /health`                  | Readiness check                  |
| `GET  /openapi/v1.json`         | OpenAPI spec                     |
| `POST /api/connect`             | Register Simulated provider      |
| `POST /api/sync`                | Run full/incremental sync        |
| `GET  /api/accounts`            | List accounts                    |
| `GET  /api/transactions`        | List transactions                |
