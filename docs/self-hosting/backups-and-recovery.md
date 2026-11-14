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
| `backup.ps1` | Independent `pg_dump` + archive copy (uses Docker); still available for manual full dumps. | `eng/scripts/` |

A snapshot is a consistent (single repeatable-read transaction) `COPY ... TO CSV` of every public table. It needs only the
app's own Postgres driver, no `pg_dump` on the host.

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

The repository is initialised automatically on first use. Other restic backends (S3, sftp, a NAS path) work the same way.
With `Backup__Restic__Repository` unset the UI shows "No off-site backup configured" and, in production, a red warning chip.

Other settings: `Backup__Enabled` (default true), `Backup__CronExpression` (default `0 15 3 * * ?`),
`Backup__Directory`, `Backup__KeepLocalSnapshots` (default 30; pruning happens only after a confirmed off-site copy and never
touches `premigrate-*` snapshots or the raw archive), `Backup__DrillAfterBackup` (default true).

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
2. Create a **fresh database** for real data (new Aspire volume / new database name).
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
