# Upgrading and rolling back

## Policy

- Releases are tagged `vMAJOR.MINOR.PATCH` and listed in [CHANGELOG.md](../../CHANGELOG.md).
- **Database migrations are forward-only.** There are no down-migrations. Schema changes should follow expand/contract (add first, switch code, remove in a later release) so that version N-1 can still run against the schema of version N for one release.
- **Rollback = restore.** To go back to the previous version, restore the backup taken before the upgrade into the previous image version.

## Upgrade procedure

1. Read the CHANGELOG entry for breaking changes and new required settings.
2. Take and verify a backup: `./eng/scripts/backup.sh` (or `backup.ps1`), then `./eng/scripts/restore.sh <dir>` to prove it restores.
3. Pull/build the new version: `git fetch --tags && git checkout vX.Y.Z && docker compose --profile full build`.
4. Start it: `docker compose --profile full up -d`. The one-shot `migrate` service runs first and the api/worker wait for it (`docker compose logs migrate` shows the result; if it fails nothing else starts and the database is untouched beyond the verified snapshot). The api does not migrate by itself unless `DATABASE__MIGRATEONSTARTUP=true`. Migration is serialized by a Postgres advisory lock so two starters never migrate concurrently, and the app takes a verified pre-migration snapshot first when data exists.
5. Check `/alive`, the Data page (backup status) and one manual sync.

## Rolling back

1. `docker compose --profile full down`
2. `git checkout <previous tag>` and rebuild.
3. Restore the pre-upgrade backup into the live database (see [restore runbook](../runbooks/restore.md)) with api/worker stopped.
4. `docker compose --profile full up -d`

## Verifying release images (once release signing lands, #186)

Images are published to GHCR on tags. Until signing and provenance are in place, verify by building from the
tag yourself. Tracked work: cosign keyless signatures, SBOM/provenance attestations, a `docker-compose.release.yml`
pinned by digest, and version/commit on `/api/status`.
