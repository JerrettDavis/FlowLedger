# Changelog

All notable changes are recorded here. The project follows [Semantic Versioning](https://semver.org/) once
the first tag is cut; until then everything is "Unreleased".

## [Unreleased]

### Added
- `migrate` compose service (`FlowLedger.Api.dll --migrate`): schema changes are applied once before api/worker start; the API no longer auto-migrates in Production unless `DATABASE__MIGRATEONSTARTUP=true`.
- Object-storage writable and last-sync-age readiness probes; typed, start-up validated Backup/Sync/HomeAssistant options; `API__TENANTID` compose variable.
- `flowledger.sync.*`, `flowledger.outbox.messages` and `flowledger.webhook.received` metrics, Npgsql instrumentation, `Logging__Format=json`, and an append-only `audit_events` log for sensitive actions.
- Tag-triggered release signing (cosign keyless), SBOM and provenance attestations; `/api/status` returns the build version and commit.
- Linux `eng/scripts/backup.sh` / `restore.sh`; CI runs backup -> restore -> row-count compare against the compose stack.
- Persisted ASP.NET Data Protection key ring (`DataProtection__KeyPath`, `web_keys` volume).
- Advisory lock around start-up migrations; explicit cron misfire policy for scheduled jobs.
- Nightly verification workflow (suite x3, TRX upload, issue on failure).
- Threat model, data inventory/retention, runbooks, SLO/capacity, observability and upgrade documentation.
- Issue/PR templates, CODEOWNERS, third-party notices, licence and repository metadata.

- Durable `job_runs` history (shown in `/api/data/status`) and bounded retry with jitter for failed backup/valuation/Home Assistant jobs.
- Budget rollover (per-budget flag, carried amount stored per month), a Subscriptions page with price-change flags, and `alert.raised` webhook delivery (#70); Undo and an exact count for "apply to same merchant" on the transactions grid (#72); MX category coverage in the provider-category hints (#67); the last restore drill, with live vs restored row counts and the raw-archive check, on the Data page (#62); `/api/sync/estimate` counts only accounts the active connector feeds (#65).
- Owner self-service erasure (#189): `POST /api/account/delete-my-data` (body `{"confirmation":"DELETE MY DATA"}`) erases the caller's own tenant through the existing tenant erasure, audited as `tenant.self_delete`; the household tenant is refused. Bank connectors that expose a revoke API are asked first (best effort: MX deletes the tenant's MX user; Quiltt and SimpleFIN are reported as not supported). New `Backup__Retention__*` settings enforce a backup expiry policy: local `backup-*`/`premigrate-*` pruning and `restic forget --prune` with keep-daily/weekly/monthly (defaults 7/4/6) in the scheduled backup job. **Behaviour note:** local snapshot pruning now also runs when no off-site repository is configured, and `premigrate-*` snapshots older than 90 days (never the newest) are removed (`Backup__Retention__PremigrateMaxAgeDays=0` keeps them).

### Changed
- NuGet lock files (#190): every project (except the dev-only AppHost) commits `packages.lock.json`; CI workflows and all three Dockerfiles restore with `--locked-mode`, and the Dockerfile `# syntax` line is pinned by digest. After changing a package version run `dotnet restore FlowLedger.slnx --force-evaluate` (see CONTRIBUTING.md).
- **BEHAVIOUR CHANGE (#139):** `GET /api/data/export` now defaults to the CALLER'S tenant instead of dumping every tenant. The whole-database dump is the explicit `?scope=all` (disaster recovery only; still household tenant + `X-Admin-Token`; documented in `docs/self-hosting/backups-and-recovery.md` and `upgrading.md`). Scripts that fetched the export for disaster recovery must add `?scope=all`. The Data page now offers "Export my data" and "Full database dump (disaster recovery)".
- Multi-user secure defaults when OIDC is on (#135, #145, #159), each with a legacy opt-out documented in `docs/self-hosting/upgrading.md`: signed tenant assertion required (`API__TENANTSIGNINGKEY`), household tenant id must not be the well-known constant, sign-up defaults to InviteOnly (`AUTH__SIGNUP__MODE`, `AUTH__SIGNUP__LEGACYOPEN`), absolute session cap `AUTH__SESSION__ABSOLUTEMAXDAYS` (30), and a `__Host-` session cookie (signs everyone out once).
- GitHub Actions are pinned by commit SHA; Dependabot auto-merge skips major updates.
- Compose smoke test also runs on pull requests; tagged builds gate on fixable CRITICAL vulnerabilities.

### Removed
- A tracked one-off data-fix SQL script that contained personal identifiers (history scrub decision pending).
