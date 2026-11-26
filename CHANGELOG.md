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

### Changed
- GitHub Actions are pinned by commit SHA; Dependabot auto-merge skips major updates.
- Compose smoke test also runs on pull requests; tagged builds gate on fixable CRITICAL vulnerabilities.

### Removed
- A tracked one-off data-fix SQL script that contained personal identifiers (history scrub decision pending).
