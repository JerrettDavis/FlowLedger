# Self-Hosting with Docker Compose

This guide covers running FlowLedger from a fresh clone using Docker Compose.

## Prerequisites

- Docker Desktop 4.x or Docker Engine 24+ with Compose V2
- `docker compose version` should print `v2.x.x`

## Quick start — full stack

```bash
# 1. Clone and enter the repo
git clone https://github.com/your-org/FlowLedger.git
cd FlowLedger

# 2. Create your local env file
cp .env.example .env
# Edit .env — set a strong POSTGRES_PASSWORD at minimum.
# The default API__KEY is fine for local dev.

# 3. Build and start all services
docker compose --profile full up --build

# 4. In a second terminal, seed the demo household
./eng/scripts/seed.sh          # Linux / macOS
# or
./eng/scripts/seed.ps1         # Windows PowerShell
```

Services come up at:

| Service    | URL                       |
|------------|---------------------------|
| Web UI     | http://localhost:5002     |
| API        | http://localhost:5001     |
| PostgreSQL  | localhost:5432            |
| Redis      | localhost:6379            |

Health endpoints:
- `GET http://localhost:5001/alive` — API liveness
- `GET http://localhost:5001/health` — API readiness
- `GET http://localhost:5002/alive` — Web liveness

## Infra-only mode (for Aspire dev loop)

Start only PostgreSQL and Redis so the Aspire orchestrator can connect to them:

```bash
docker compose --profile infra up -d
```

Then run the Aspire dev loop in a separate terminal:

```bash
./eng/scripts/run.ps1    # Windows
./eng/scripts/run.sh     # Linux / macOS
```

See [local-dev.md](../development/local-dev.md) for the full Aspire workflow.

## Profiles summary

| Profile | Services started             | Use case                          |
|---------|------------------------------|-----------------------------------|
| `infra` | postgres, redis              | Aspire inner-loop dev             |
| `full`  | postgres, redis, api, web, worker | Self-hosted / CI smoke test  |

## Environment variables

All variables can be set in `.env` (copy from `.env.example`).

| Variable                        | Default                            | Description                                                          |
|---------------------------------|------------------------------------|----------------------------------------------------------------------|
| `POSTGRES_PASSWORD`             | `dev_only_change_in_prod`          | PostgreSQL superuser password. **Required** for `full` profile.       |
| `API__KEY`                      | `dev-local-key-not-for-production` | API authentication key. Use `openssl rand -hex 32` for production.   |
| `ConnectionStrings__flowledger` | Derived from `POSTGRES_PASSWORD`   | Full Npgsql connection string (auto-set in compose).                  |
| `ConnectionStrings__redis`      | `redis:6379`                       | Redis connection string.                                             |
| `OTEL_EXPORTER_OTLP_ENDPOINT`  | (unset)                            | Optional: OTLP endpoint for traces/metrics/logs.                     |

Connection string names used internally:
- Database: `flowledger` → `ConnectionStrings__flowledger`
- Redis:    `redis`      → `ConnectionStrings__redis`

## Applying migrations manually

When running the full stack, the API applies migrations automatically at startup
(it starts in `Development` environment in compose). To apply migrations manually
against a running compose database:

```bash
./eng/scripts/migrate.sh         # Linux / macOS
./eng/scripts/migrate.ps1        # Windows
```

## MX integration (real financial data)

By default `Mx__Enabled=false` — the stack uses the Simulated provider (no API key needed).

To enable real MX data, set these in `.env`:

```env
Mx__Enabled=true
Mx__ApiKey=your-mx-api-key
Mx__ClientId=your-mx-client-id
Mx__BaseUrl=https://api.mx.com
Mx__WebhookSecret=your-webhook-secret
```

Then restart: `docker compose --profile full up`.

## Stopping and cleaning up

```bash
# Stop and remove containers (keep volumes)
docker compose --profile full down

# Stop and remove containers + volumes (wipes database)
docker compose --profile full down -v
```
