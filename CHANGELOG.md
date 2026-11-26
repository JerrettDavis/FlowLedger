# Changelog

All notable changes are recorded here. The project follows [Semantic Versioning](https://semver.org/) once
the first tag is cut; until then everything is "Unreleased".

## [Unreleased]

### Added
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
