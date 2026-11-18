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
# Edit .env — set POSTGRES_PASSWORD and API__KEY (both REQUIRED, for every profile):
#   openssl rand -hex 32
# Then either configure OIDC sign-in (Auth__Oidc__*, see multi-user-auth.md) or, for a
# trusted single-user machine only, set AUTH__ALLOWANONYMOUS=true. Without one of the two
# the Web UI refuses to start (compose runs the apps as Production).

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

Every port is published on **127.0.0.1 only**. To reach the Web UI from another device, put a
TLS reverse proxy in front of it (see [production.md](production.md)); OIDC sign-in cookies are
`Secure` outside Development, so sign-in over plain HTTP from a LAN IP will not work. Never
publish the API, PostgreSQL or Redis ports beyond the host.

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
| `POSTGRES_PASSWORD`             | (none: **required**)               | PostgreSQL password. Compose refuses to start without it.             |
| `API__KEY`                      | (none: **required**)               | API authentication key (`openssl rand -hex 32`). The API rejects the dev default outside Development. |
| `ASPNETCORE_ENVIRONMENT`        | `Production`                       | Set `Development` only for local debugging (enables the dev tenant fallback). |
| `AUTH__ALLOWANONYMOUS`          | `false`                            | `true` lets the Web UI run **without sign-in** outside Development. Trusted single-user host only. |
| `AUTH__OIDC__AUTHORITY` / `AUTH__OIDC__CLIENTID` / `AUTH__OIDC__CLIENTSECRET` | (unset) | OIDC sign-in, see [multi-user-auth.md](multi-user-auth.md). |
| `ConnectionStrings__flowledger` | Derived from `POSTGRES_PASSWORD`   | Full Npgsql connection string (auto-set in compose).                  |
| `ConnectionStrings__redis`      | `redis:6379`                       | Redis connection string.                                             |
| `OTEL_EXPORTER_OTLP_ENDPOINT`  | (unset)                            | Optional: OTLP endpoint for traces/metrics/logs.                     |

Connection string names used internally:
- Database: `flowledger` → `ConnectionStrings__flowledger`
- Redis:    `redis`      → `ConnectionStrings__redis`

## Upgrading an existing compose install

The compose file is now secure by default. Before `docker compose up` on an existing install:

1. **Postgres password.** Postgres applies `POSTGRES_PASSWORD` only when the volume is first
   created. If you never set one, your volume still uses the old default `dev_only_change_in_prod`:
   put exactly that in `.env` first, start the stack, then rotate it
   (`docker compose exec postgres psql -U flowledger -c "ALTER USER flowledger PASSWORD '<new>'"`)
   and update `.env`. Setting a new value without `ALTER USER` locks the apps out of your data.
2. **API key.** The old `dev-local-key-not-for-production` key is refused (apps now run as
   Production). Set `API__KEY` to a random value; Web and API read the same variable.
3. **Sign-in.** Configure OIDC (`AUTH__OIDC__*`) or set `AUTH__ALLOWANONYMOUS=true`, otherwise the
   Web UI will not start.
4. **Existing OIDC sessions.** Sign out and back in once: older cookies lack the household claim,
   so *Data & Backups* stays hidden for them until they are re-issued.

Seed/migrate scripts read `API__KEY` / `POSTGRES_PASSWORD` from the environment; export them
(e.g. `set -a; . ./.env; set +a`). Seeding a Production stack needs `--force`.

## Applying migrations manually

When running the full stack, the API applies migrations automatically at startup
(compose sets `Database__MigrateOnStartup=true`; a verified snapshot is taken first when
migrations are pending against existing data). To apply migrations manually
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
