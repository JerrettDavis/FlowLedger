# Backups, recovery and the production switch

FlowLedger holds data that is expensive or impossible to re-create (billable bank-aggregation payloads). This page is the
operating contract for keeping it safe. Everything below is implemented and covered by tests.

## What protects your data

| Layer | What it does | Where |
|---|---|---|
| Raw payload archive | Every provider response is written byte-for-byte **before** parsing; never deleted (ADR 0007). | `~/.flowledger/objects` |
| Pre-migration snapshot | Before any pending EF migration is applied to a database that has data, a verified snapshot is taken. If it fails, startup aborts and nothing is migrated. | `~/.flowledger/backups/premigrate-*` |
| Scheduled backup | Daily 03:15 (Worker): verified snapshot, **restore drill**, then an encrypted off-site copy via restic. Every run is recorded and shown on **Data & Backups**. | `~/.flowledger/backups/backup-*` |
| Restore drill | Each backup is restored into a scratch database (migrations applied up to the snapshot's version) and every table's row count is compared with the manifest. | automatic |
| Portable export | One CSV per table + `manifest.json` (SHA-256 and row counts), as a zip. Importable into an **empty** database only. | UI button / `GET /api/data/export` |
| `backup.sh` | Linux/macOS twin of `backup.ps1`; CI runs backup -> restore -> row-count compare with it on every push and PR | `eng/scripts/` |
| `restore.sh` | Linux/macOS twin of `restore.ps1` (restores into a scratch database, prints row counts) | `eng/scripts/` |
| `backup.ps1` | Independent `pg_dump` + archive copy (uses Docker); still available for manual full dumps. | `eng/scripts/` |

A snapshot is a consistent (single repeatable-read transaction) `COPY ... TO CSV` of every public table. It needs only the
app's own Postgres driver, no `pg_dump` on the host.

**Recovery objectives.** These backups are logical, point-in-time snapshots, not continuous. With the default schedule the
**RPO is up to 24 hours** (anything written since the last 03:15 run is lost on a total loss), and there is no point-in-time
recovery unless you add WAL archiving (WAL-G / pgBackRest) yourself. The **RTO is a manual restore** (see Disaster recovery
below) whose duration depends on data size and has not been measured. See [database.md](database.md) for the full table,
managed-Postgres guidance and the major-version upgrade procedure.

## Turning on off-site backups (restic → Backblaze B2)

The scheduled job copies each verified snapshot **and** the raw payload archive to a restic repository. Restic encrypts
client-side; Backblaze only ever sees ciphertext. Credentials come from the environment, never from the repo.

1. Install restic on the machine that runs the Worker (`winget install restic.restic` / `apt install restic`).
2. Create a B2 bucket and an application key restricted to it.
3. Put the repository password in a file **outside the repo** and keep a copy in your password manager.
   If this password is lost the off-site backups cannot be decrypted.
4. Configure the Worker (and API, for "Back up now"):

```
Backup__Restic__Repository=b2:YOUR-BUCKET:flowledger
Backup__Restic__PasswordFile=C:\secrets\flowledger-restic.pw
B2_ACCOUNT_ID=<key id>
B2_ACCOUNT_KEY=<application key>
```

Under Aspire (local dev / the AppHost), set the same values once as AppHost user-secrets and they are forwarded to the Api and Worker:

```
dotnet user-secrets set "Backup:Restic:Repository"   "b2:YOUR-BUCKET:flowledger" --project src/FlowLedger.AppHost
dotnet user-secrets set "Backup:Restic:PasswordFile" "C:\secrets\flowledger-restic.pw" --project src/FlowLedger.AppHost
dotnet user-secrets set "Backup:B2:AccountId"  "<key id>" --project src/FlowLedger.AppHost
dotnet user-secrets set "Backup:B2:AccountKey" "<application key>" --project src/FlowLedger.AppHost
```

Use a B2 application key scoped to one bucket and a `flowledger/` prefix, separate from any other project's key, so a leaked
key can only touch these backups. Keep the repository password in your password manager: without it the copies are unreadable.

The repository is initialised automatically on first use. Other restic backends (S3, sftp, a NAS path) work the same way.
With `Backup__Restic__Repository` unset the UI shows "No off-site backup configured" and, in production, a red warning chip.

Other settings: `Backup__Enabled` (default true), `Backup__CronExpression` (default `0 15 3 * * ?`),
`Backup__Directory`, `Backup__KeepLocalSnapshots` (default 30; pruning happens only after a confirmed off-site copy and never
touches `premigrate-*` snapshots or the raw archive), `Backup__DrillAfterBackup` (default true).

## Docker Compose (`--profile full`)

The compose stack gives the api and worker a persistent **`backups_data`** volume mounted at `/data/backups`, and sets
`Backup__Directory=/data/backups`, so `backup-*` and `premigrate-*` folders survive container recreation (the raw archive
lives on `objects_data` at `/data/objects`). The api and worker images ship the `restic` binary (a static build copied from
the official `restic/restic` image), so no host install is needed.

To enable the off-site copy, set these in `.env` (all optional; empty means "no off-site backup configured"):

```
Backup__Restic__Repository=b2:YOUR-BUCKET:flowledger
RESTIC_PASSWORD=<repository password>
B2_ACCOUNT_ID=<key id>
B2_ACCOUNT_KEY=<application key>
```

`RESTIC_PASSWORD` is read by restic directly from the environment (the `Backup__Restic__PasswordFile` setting also works if you
mount a file yourself). Check the binary and the repository from inside the worker:

```
docker compose --profile full exec worker restic version
docker compose --profile full exec -e RESTIC_REPOSITORY=... worker restic snapshots
```

Caveats: the chiseled runtime images have no shell, so `exec ... sh` is not available; `exec <binary>` as above works. The app
does not yet refuse to run in Production with a backup directory that is not on a mounted volume, so if you override
`Backup__Directory` away from `/data/backups`, keep it on a volume. Back up the `backups_data` volume itself only via the
off-site copy; do not treat a same-host volume as your only copy.

## Manual dump and restore scripts (Linux, macOS, Windows)

`eng/scripts/backup.sh` / `restore.sh` are the Linux/macOS twins of `backup.ps1` / `restore.ps1`: a `pg_dump -Fc` of the
database plus a copy of the raw archive, verified with `pg_restore -l`, and a restore into a **scratch** database that
prints row counts (it refuses to touch the live `flowledger` database). With the compose stack running:

```
./eng/scripts/backup.sh                       # prints the backup dir; OBJECTS_CONTAINER=<api container> copies /data/objects out of compose
./eng/scripts/restore.sh ~/.flowledger/backups/<timestamp>
./scripts/dr-drill.sh                         # backup -> restore into scratch -> compare every table's row count
```

`scripts/dr-drill.sh` exercises only this `pg_dump`/`pg_restore` path (not the in-app export/import, restic, or a wipe of the live
database), and compares counts taken just before the backup, so run it against an idle stack. It is not yet wired into CI.

## Migrations

* Aspire/dev: a verified `premigrate-*` snapshot is taken automatically before any pending migration is applied to a database that has data; startup aborts if it fails.
* Concurrent starters: auto-migration (Development, or `Database__MigrateOnStartup=true`) runs under a Postgres advisory lock (`flowledger:migration`). A second API starting at the same time waits (up to `Database__MigrationLockTimeoutSeconds`, default 300), then finds the schema current and migrates nothing. Verified by an integration test with two concurrent starters on an empty database.
* Rollback policy: migrations are **forward-only**; there is no supported "down" path. Write schema changes expand/contract (add the new column or table first, ship code that works with both shapes, remove the old one in a later release) so the previous release keeps working against the new schema. To undo a bad release, stop the stack, restore the `premigrate-*` snapshot or the last verified backup (see Restore below), and start the previous release image. Do not edit `__EFMigrationsHistory` by hand.
* Not yet done: a one-shot `migrate` compose service that api/worker wait on (`service_completed_successfully`), and a CI upgrade test from the previous release image with data present. Until then the worker can start against a pre-migration schema if it is started without the api; its `/ready`-style probe is the heartbeat only, so start the api first.
* Compose/production: `eng/scripts/migrate.sh|ps1` now takes a `pg_dump` first (via `docker compose exec postgres`) and aborts if it fails or is empty. Use `SKIP_BACKUP=1` / `-SkipBackup` only for a brand-new empty database. (These scripts have not been run end-to-end yet.)

## Data administration endpoints

`GET /api/data/export`, `POST /api/data/import` and `POST /api/data/backup-now` act on the whole database (every tenant), so on top of the household-tenant check they are gated as follows:

* **Recommended: set `DataAdmin__Key`** (>= 32 random characters, `openssl rand -hex 32`) on the API and the Web app. Callers must then send it as the `X-Admin-Token` header (the Web app does this automatically for `/api/data/*`), from any address. Add `DataAdmin__RequireLoopback=true` to also require a direct loopback caller.
* **Without a key** only a *direct* loopback caller is accepted. Requests that arrive through a reverse proxy are refused even when the proxy runs on the same host (they carry `X-Forwarded-*` headers), and `X-Forwarded-For` can never make a caller look local. `DataAdmin__AllowRemote=true` still disables the check entirely (not recommended; use the key instead).
* Import accepts at most 256 MB and stops extracting as soon as the real uncompressed byte count exceeds the cap.

The export zip is deleted from the server as soon as it has been sent.

## Disaster recovery (new machine, empty database)

1. Install the stack and let it start against an **empty** database (migrations run, the environment marker is stamped).
2. Restore from off-site: `restic restore latest --target <dir>` (needs the password file + B2 keys). The newest
   `backup-*` folder inside contains the CSVs and `manifest.json`; zip its contents (flat, no sub-folder).
3. `POST /api/data/import` (multipart field `file`). The server verifies every file hash, refuses if the database is not
   empty, applies migrations up to the snapshot's version, loads each table, checks row counts, then migrates forward.
4. Restore the raw archive directory from the same restic snapshot to `ObjectStorage:RootPath` to keep the free re-ingest
   path (`Mx:Provider:ReplayArchivePath`) working.
5. Run **Back up now** and confirm the Data & Backups page is green.

## Switching from demo/sandbox to real data

The database carries a `data_environment` marker (`demo`, `sandbox`, `production`). The API and Worker refuse to start when the
marker and `Mx:Enabled` / `Mx:Environment` disagree across the production boundary, and a database that already holds demo
data can never be adopted as production (unless you set `DataEnvironment__AdoptExistingAsProduction=true` on purpose).

1. Run **Back up now** (and confirm it is off-site).
2. Create a **fresh database** for real data. Under Aspire set on the AppHost `FlowLedger:DatabaseName` (e.g. `flowledger_prod`) and `FlowLedger:DataDir` (a separate raw-payload archive folder, e.g. `~/.flowledger-prod`), so production never shares a database or archive folder with the demo/sandbox data. Under compose use a new `POSTGRES_DB`/volume.
3. Set `Mx__Enabled=true`, `Mx__Environment=production`, the MX credentials, and the off-site backup variables.
4. Start. The header shows no DEMO/SANDBOX chip, "Connect & Sync" is replaced by **Sync now**, and the demo connect
   endpoint returns 409.

## API cost controls

* Every MX call is recorded in `provider_api_calls` (method, path, status, duration; never query strings) and shown per
  endpoint on Data & Backups.
* Hard budgets: `Mx__Provider__DailyCallBudget` (default 500) and `Mx__Provider__MonthlyCallBudget` (default 5000). When
  reached, **nothing is sent**. `Mx__Provider__CallsPaused=true` is an instant kill switch.
* Syncs: at most one per tenant at a time, and (real providers) at most one every 30 minutes
  (`Sync__MinIntervalMinutes`). A sync inside the window returns 429 with `Retry-After`; the provider is not called.
* MX bills per **aggregation** (refresh), which is already cooldown-guarded (`ManualRefreshCooldown`); plain data GETs are
  cheap but are still counted and capped.
* The incremental overlap (`Mx__Provider__IncrementalOverlapDays`, default 30) is intentionally **not** reduced: it is what
  catches late-posting and back-dated transactions, and trading it for fewer reads risks silent data loss. Revisit once MX's
  contractual billing unit is confirmed using the ledger numbers.
