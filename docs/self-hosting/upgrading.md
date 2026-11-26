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

## Verifying release images

Pushing a `vX.Y.Z` tag runs the Docker workflow, which builds the api, web and worker images once, gates them on Trivy, pushes
them to `ghcr.io/<owner>/flowledger-{api,web,worker}` and then, per image digest:

- signs the digest with cosign keyless (GitHub OIDC; the certificate names this repository's workflow and the tag ref),
- attaches an SPDX SBOM as a cosign attestation,
- attaches a GitHub build-provenance attestation (also pushed to the registry) and BuildKit's max-detail provenance.

The image carries the tag and commit (`FLOWLEDGER_VERSION`, `FLOWLEDGER_COMMIT`, OCI labels), and `GET /api/status` returns
`{ status, version, commit }`. Verify before deploying:

```bash
cosign verify ghcr.io/<owner>/flowledger-api@sha256:<digest> \
  --certificate-identity-regexp '^https://github.com/<owner>/FlowLedger/\.github/workflows/docker\.yml@refs/tags/v' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
cosign verify-attestation --type spdxjson <same arguments as above> ghcr.io/<owner>/flowledger-api@sha256:<digest>
gh attestation verify oci://ghcr.io/<owner>/flowledger-api@sha256:<digest> --repo <owner>/FlowLedger
```

Not yet provided: a `docker-compose.release.yml` pinned by digest (the digests exist only once a tag has been pushed) and
a published release-notes policy. This workflow has not run yet: the first tag is its first test.
