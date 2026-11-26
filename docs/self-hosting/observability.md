# Observability and alerting

## What exists today

- OpenTelemetry traces/metrics for ASP.NET Core, HttpClient and the .NET runtime; OTLP export is opt-in (`OTEL_EXPORTER_OTLP_ENDPOINT`).
- Health endpoints: `/alive` (liveness) and `/health`. Note: `/health` currently only reports that the process is up; it is not a dependency readiness check (tracked in #184).
- Data page: last backup status and restore-drill result; provider call ledger.

## Not yet available (tracked in #193)

Correlation ids across web, api and worker; a custom `FlowLedger` meter (sync duration/failures, backup age,
job last success); Npgsql/EF/Quartz instrumentation; an append-only audit log; JSON console logs.

## Sample alert rules (Prometheus syntax, using external probes until the meter exists)

```yaml
groups:
  - name: flowledger
    rules:
      - alert: FlowLedgerApiDown
        expr: probe_success{job="flowledger-alive"} == 0
        for: 5m
        labels: { severity: page }
        annotations: { summary: "API /alive failing for 5 minutes" }

      - alert: FlowLedgerWebDown
        expr: probe_success{job="flowledger-web-alive"} == 0
        for: 5m
        labels: { severity: page }

      - alert: FlowLedgerBackupStale
        # Replace with the backup-age gauge once exported; until then use a file-age exporter on the backup directory.
        expr: time() - node_filesystem_files_last_modified_seconds{path="/backups/latest"} > 26 * 3600
        labels: { severity: warn }
        annotations: { summary: "No verified backup in 26 hours" }

      - alert: FlowLedgerSyncStale
        expr: time() - flowledger_sync_last_success_timestamp_seconds > 36 * 3600
        labels: { severity: warn }
        annotations: { summary: "No successful provider sync in 36 hours (needs the planned FlowLedger meter)" }

      - alert: FlowLedgerProviderAuthFailures
        expr: increase(flowledger_provider_auth_failures_total[1h]) > 0
        labels: { severity: warn }
        annotations: { summary: "Provider authentication failing; re-link the connection (see provider-outage runbook)" }
```

Rules that reference `flowledger_*` metrics are the contract the planned meter will satisfy; the probe-based
rules work today with the Prometheus blackbox exporter against `/alive`.
