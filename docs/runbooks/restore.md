# Runbook: restore from backup

The authoritative procedure is [Disaster recovery](../self-hosting/backups-and-recovery.md#disaster-recovery-new-machine-empty-database)
in the backups guide; this page adds the decision points.

## Choose the source

| Situation | Use |
|---|---|
| Bad migration at startup | The `premigrate-*` snapshot under `~/.flowledger/backups/` (compose: take your own `pg_dump` first, `eng/scripts/migrate.*` does) |
| Corrupt or lost database, host still available | Newest local `backup-*` snapshot |
| Host lost | Off-site restic copy (`restic restore latest --target <dir>`; needs the repository password and B2 keys) |
| You need data newer than the last snapshot | Not available unless you run your own WAL archiving: the built-in RPO is up to 24 hours ([database.md](../self-hosting/database.md)) |

## Steps

1. Stand up the stack against an **empty** database. Import refuses a non-empty database.
2. Zip the contents of the chosen `backup-*` folder flat (CSVs plus `manifest.json`, no sub-folder).
3. `POST /api/data/import` from the host itself (loopback) with multipart field `file`. The server verifies hashes and row counts and migrates forward.
4. Restore the raw archive directory to `ObjectStorage:RootPath` from the same restic snapshot.
5. Run **Back up now**; confirm Data & Backups is green and a sync succeeds.

## Verify

- Row counts match `manifest.json` (the import checks this and fails otherwise).
- The `data_environment` marker matches your provider settings or the API will refuse to start.
- Record the actual elapsed time: restore duration has not been measured by the project, so your own number is the RTO evidence.
