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

## Breaking changes

- **`GET /api/data/export` default scope (#139).** It used to dump every tenant; it now exports only the caller's tenant. The whole-database dump is the explicit `GET /api/data/export?scope=all` (disaster recovery only; still needs the household tenant and the admin token). Update any script or cron that downloads the export for disaster recovery, and note that single-tenant bundles cannot be restored with `POST /api/data/import`. The Data page has separate "Export my data" and "Full database dump (disaster recovery)" buttons.

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

## Upgrading to the multi-user secure defaults (#135, #145, #159)

These three changes apply **only when OIDC sign-in is configured** (`AUTH__OIDC__AUTHORITY` / `AUTH__OIDC__CLIENTID`). Single-household and
`AUTH__ALLOWANONYMOUS=true` stacks are not affected. They are secure by default, so an OIDC stack that changes nothing will either start in
a stricter mode or refuse to start with a message naming the setting. Each row below has a one-line opt-out that restores the old behaviour;
every active opt-out logs a startup Warning.

| What changes for an OIDC stack | Symptom after upgrading | Fix (preferred) | Legacy opt-out (not recommended) |
|---|---|---|---|
| **Signed tenant assertion is required** outside Development (#135). | api and web refuse to start: `Api:TenantSigningKey` missing, shorter than 32 characters, or equal to `Api:Key`. | Set `API__TENANTSIGNINGKEY` (`openssl rand -hex 32`); compose passes it to api and web. Restart both together. | `API__REQUIRESIGNEDTENANT=false` |
| **The household tenant id may not be the well-known constant** `00000000-0000-0000-0000-000000000001` (#135). | New install (empty database): nothing, the api generates and stores a random id. Existing install with data and no configured id (or `API__TENANTID` set to the constant): api (and web, when it is configured as the constant) refuse to start: "household tenant id is the well-known constant" / "already holds data". | Existing install: your data lives under the well-known id, so keep it (changing it hides all data) and acknowledge it, or set `API__TENANTID` to the id your data uses. | `TENANCY__ALLOWWELLKNOWNLEGACYTENANTID=true` |
| **Sign-up defaults to InviteOnly** (#145). Only allowlisted emails get a NEW account. Existing logins and the `TENANCY__LEGACYOWNEREMAIL`/`LegacyOwnerSubject` owner keep signing in. | A new person is sent to the sign-in-failed page ("Sign-up is closed"). With no allowlist at all, startup logs a Warning that nobody new can join. | Set `AUTH__OIDC__ALLOWEDEMAILS__0` / `AUTH__OIDC__ALLOWEDEMAILDOMAINS__0`, or `AUTH__SIGNUP__MODE=Closed` to make it explicit. | `AUTH__SIGNUP__LEGACYOPEN=true` (same as `AUTH__SIGNUP__MODE=Open`) |
| **Billable provisioning under open sign-up** (#145). When sign-up is Open, only the household tenant may create a Quiltt profile. (InviteOnly/Closed tenants are all invited, so they are unaffected.) | `POST /api/integrations/quiltt/session` returns 403 for non-household tenants. | Use InviteOnly. | `Quiltt__AllowOpenSignupTenants=true` (api, via `docker-compose.override.yml`) |
| **Trusting unverified email with auto-link** (#145). `Tenancy:TrustUnverifiedEmail=true` now warns; combined with `Tenancy:AutoLinkVerifiedEmail=true` it is refused outside Development. | api refuses to start naming both options. | Set `TENANCY__LEGACYOWNERISSUER` to pin the IdP, or drop one option. | `AUTH__SIGNUP__LEGACYEMAILTRUST=true` |
| **Absolute session lifetime** (#159). A session ends `Auth:Session:AbsoluteMaxDays` (default **30**) after sign-in even if used daily. | Users sign in again after 30 days. Existing cookies are stamped when first seen, so the clock for them starts at the upgrade. | `AUTH__SESSION__ABSOLUTEMAXDAYS=<days>` | `AUTH__SESSION__ABSOLUTEMAXDAYS=0` (no cap) |
| **`__Host-` session cookie** (#159). Outside Development the cookie is `__Host-flowledger.auth` (Secure, `Path=/`, no `Domain`), so a sibling subdomain cannot plant or shadow it. | **Every user is signed out once** at the first start (the old cookie name is ignored). | Nothing; sign in again. | `AUTH__SESSION__LEGACYCOOKIENAME=true` |

Notes:

- Compose forwards every variable above with an empty default, so an unset variable means the secure default. Settings it does not forward
  (for example `Quiltt__AllowOpenSignupTenants`) go in `docker-compose.override.yml`.
- Non-compose installs (Aspire, bare processes): the **api** must also be told that OIDC is on (`Auth:Oidc:Authority`, `Auth:Oidc:ClientId`) or it
  cannot default `Api:RequireSignedTenant` to true and cannot enforce closed sign-up on `/api/users/resolve`. Alternatively set
  `Api__RequireSignedTenant=true` explicitly. The web always knows and enforces its own checks.
- Rolling: restart api and web together after setting the signing key; a web without the key against an api that requires it gets 401s.
- Revocation: unlinking a login ends its session within **5 minutes** (HTTP requests and open Blazor circuits are both re-validated against the
  API at that interval; an unreachable API never signs anyone out).
- Tenant signature v2 (#135). The Web app now signs `v2.{timestamp}.{nonce}.{mac}` over tenant, timestamp, a random nonce, the HTTP method and the
  request path; the API accepts each nonce once inside the 60-second window (bounded in-memory cache, 100,000 entries) and rejects a header replayed
  or presented for another endpoint. The query string is not signed. **Upgrade the API first**: it accepts both v1 and v2 while
  `Api:AcceptLegacyTenantSignature` is true (the default for this release; compose: `API__ACCEPTLEGACYTENANTSIGNATURE`), so an old web keeps working. A new
  web against an old API gets 401s. Once api and web are both upgraded set `API__ACCEPTLEGACYTENANTSIGNATURE=false`; the default flips in a later release.
  The replay cache is per API process, so run one API replica (the supported topology) or accept replay protection per replica.
- Household tenant id for new installs (#135). With OIDC on and **no** `Api:TenantId`/`Tenancy:LegacyTenantId`/`Worker:TenantId` configured, an empty database
  (no accounts, no tenants) gets a random id generated on the first API start and stored in `system_settings` (`household_tenant_id`); the api and worker
  read it from there on every start. Compose no longer defaults `API__TENANTID` for the api and worker, so a new OIDC install needs no tenant setting at all.
  An install that already holds data and has no configured id is refused at start unless `TENANCY__ALLOWWELLKNOWNLEGACYTENANTID=true` (its data lives
  under the constant). An explicitly configured id always wins. Single-household (non-OIDC) installs are unchanged: api and web must agree on a
  configured id, and the default stays the constant. The worker only reads the stored id; start the api once before the worker on a brand-new OIDC install.
