# SLO and capacity notes

FlowLedger is self-hosted software, so there is no vendor SLO. This page records what the design targets and what has been
measured, without inventing numbers.

## Targets (design intent, not measured guarantees)

| Item | Value | Basis |
|---|---|---|
| Data loss window (RPO) | up to 24 h | Daily logical backup at 03:15 ([database.md](../self-hosting/database.md)) |
| Recovery time (RTO) | manual restore, unmeasured | Time your own restore ([restore.md](restore.md)) |
| Provider sync freshness | minimum 30 min between syncs per tenant for real providers | `Sync__MinIntervalMinutes` |
| Availability | single instance, no HA | One API, one Worker, one Postgres in compose |

No availability or latency SLO has been defined or monitored. Health endpoints exist (`/health`, `/alive`) and OpenTelemetry can be
exported with `OTEL_EXPORTER_OTLP_ENDPOINT`, but no alert rules ship with the project.

## Capacity facts that exist in the code

- Worker syncs tenants serially by default; `Worker__MaxParallelTenants` (1-16) raises concurrency, bounded by the Postgres pool.
- Per-tenant caps: transactions 500,000, budgets 500, goals 200, categorization rules 1,000, merchant aliases 1,000.
- CSV import is capped at 5 MB and 50,000 rows; snapshot import refuses bundles expanding beyond 8 GB.
- Several endpoints scan up to 50,000 rows per request (see the performance items in
  [audit-2026-11-18.md](../development/audit-2026-11-18.md)); there is no load-test result behind a users-per-instance figure.
- The raw payload archive grows without bound (ADR 0007).

## Missing

Load testing, a capacity model, alert thresholds and an uptime target. Add them here when measured.
