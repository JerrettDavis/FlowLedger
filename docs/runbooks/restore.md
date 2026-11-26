# Runbook: restore from backup

The full procedure, including the in-app restore drill and the off-site (restic) path, is in
[backups-and-recovery.md](../self-hosting/backups-and-recovery.md). This page is the short operator checklist.

**Rollback policy:** migrations are forward-only. "Rolling back" a bad release means restoring the pre-upgrade
backup into the previous image version (see [upgrading](../self-hosting/upgrading.md)).

## Verify a backup (safe, never touches live data)

```bash
dir=$(./eng/scripts/backup.sh | sed -n 's/^Backup OK -> //p')   # Windows: eng\scripts\backup.ps1
./eng/scripts/restore.sh "$dir"                                  # restores into flowledger_restore_test, prints row counts
```

CI runs exactly this sequence against the compose stack on every push and PR (`DR drill` step of the Docker workflow).

## Disaster recovery on a new machine

1. Install Docker, clone the repository at the release you were running, copy your `.env`.
2. `docker compose --profile infra up -d` (Postgres + Redis only).
3. Restore the dump into the real database name (stop api/worker first): `pg_restore --clean --if-exists -d flowledger flowledger.dump` inside the postgres container.
4. Copy the backup `objects/` directory into the `objects_data` volume, and restore the `web_keys` volume if you have it (otherwise users just sign in again).
5. `docker compose --profile full up -d`. Compare row counts with the manifest/`restore.sh` output.
