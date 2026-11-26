# Database operations

FlowLedger's production database is **PostgreSQL only**. SQLite and other engines are not supported (the code uses Npgsql and
Postgres-specific features, and no other provider is referenced).

## Supported versions

| Where | Version |
|---|---|
| `docker-compose.yml` | `postgres:17-alpine` |
| Automated tests (Testcontainers) | `postgres:17-alpine` |

PostgreSQL 17 is the only version the project builds and tests against. Other versions may work but are untested; a managed
service should be pinned to 17. At startup the api and worker check `server_version_num`: they refuse to start on a
server older than PostgreSQL 16 (clear error naming the version) and log a warning on any major other than 17.

## Connecting

The API and Worker read the connection string `ConnectionStrings__flowledger` (the Web app does not connect to the database).
Compose builds it from `POSTGRES_PASSWORD` and hard-codes `Host=postgres;...` with no TLS option.

To point at an external or managed database, add a `docker-compose.override.yml` next to `docker-compose.yml` that replaces the
value for the `api` and `worker` services, and do not start the bundled `postgres` service:

```yaml
services:
  api:
    environment:
      ConnectionStrings__flowledger: "Host=db.example.com;Port=5432;Database=flowledger;Username=flowledger;Password=${FLOWLEDGER_DB_PASSWORD};SSL Mode=VerifyFull;Maximum Pool Size=20"
  worker:
    environment:
      ConnectionStrings__flowledger: "Host=db.example.com;Port=5432;Database=flowledger;Username=flowledger;Password=${FLOWLEDGER_DB_PASSWORD};SSL Mode=VerifyFull;Maximum Pool Size=20"
```

Notes:

- With Npgsql the TLS keyword is `SSL Mode` (`Require`, `VerifyCA`, `VerifyFull`). `sslmode=require` in older notes is the libpq
  spelling; prefer `SSL Mode=VerifyFull` with a trusted CA for a database that is not on the same private network as the app.
- The compose `api` and `worker` services `depends_on` the bundled `postgres` service, so an override that removes that
  dependency may also be needed; verify with `docker compose config` before relying on it (this override has not been run
  end to end).
- Keep `POSTGRES_PASSWORD` set in `.env`: compose validates the whole file and refuses to start without it.

## Connection pool sizing

Npgsql pools connections per process (default 100 per connection string). Each of the API and Worker has its own pool, so the
database must allow roughly `Maximum Pool Size` x (number of API + Worker processes) plus headroom for migrations and admin
tools. The Worker visits tenants serially by default; `Worker__MaxParallelTenants` (1-16) syncs several tenants at once and each
concurrent tenant holds its own connection, so keep that value small relative to the pool and to the server's `max_connections`.

A server-side pooler (PgBouncer) is not tested with FlowLedger. If you use one, use session pooling, because the application
takes session-level Postgres advisory locks (`src/FlowLedger.Infrastructure/DataSafety/PostgresAdvisoryLock.cs`), which do not
survive transaction-mode pooling.

## Backups: what you actually get

The built-in backup is a **logical snapshot**: a consistent `COPY ... TO CSV` of every public table taken in one
repeatable-read transaction, plus a restore drill. It is not a physical backup and has no point-in-time recovery.

| Metric | Value | Basis |
|---|---|---|
| RPO (data you can lose) | Up to 24 hours | Scheduled backup runs daily at 03:15 (`Backup__CronExpression`, default `0 15 3 * * ?`); data written after the last run is lost on a total loss unless you add WAL archiving |
| RPO with WAL archiving | Seconds to minutes | Only if you set up WAL-G or pgBackRest yourself (not provided or tested here) |
| RTO (time to restore) | Hours, manual | New machine, empty database, `POST /api/data/import`; time scales with data size and has not been measured |

See [backups-and-recovery.md](backups-and-recovery.md) for the procedure. Managed Postgres services (RDS, Cloud SQL, Azure,
Neon, Supabase, etc.) offer their own PITR; if you use one, enable it and still keep the FlowLedger off-site backup, because
the raw payload archive lives outside the database.

## Major version upgrade (for example 17 to 18)

Do **not** change the image tag on an existing compose volume. A Postgres major version cannot start on a data directory
created by another major version, so the container will refuse to start. Use a dump-and-restore into a new volume:

1. Run **Back up now** (Data & Backups page) and confirm the off-site copy succeeded. Also take a `pg_dump` with
   `eng/scripts/backup.ps1`.
2. Stop the stack (`docker compose --profile full down`; do not use `-v`, which deletes volumes).
3. Keep the old `postgres_data` volume untouched as your rollback.
4. Change the image tag and point `postgres` at a **new** named volume in a compose override.
5. Start only the new `postgres` service, then restore with a `pg_restore`/`psql` of the dump from step 1, or start the stack
   against the empty database and use the portable import (`POST /api/data/import`, empty database only).
6. Start the full stack, verify the Data & Backups page and a sync, then take a fresh backup.
7. Remove the old volume only after you are confident in the new one.

`pg_upgrade` is an alternative for experienced operators but is not documented or tested for this project.

## Migrations

In compose the API applies pending migrations at startup (`Database__MigrateOnStartup=true`). A verified `premigrate-*` snapshot is taken first for a database that
already has data, and startup aborts if the snapshot fails (see backups-and-recovery.md). Run only one version of the app against
a database during an upgrade.
