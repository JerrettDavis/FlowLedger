# Observability

## Correlation ids

Every API and web response carries `X-Correlation-Id`. A well-formed inbound value (`[A-Za-z0-9._-]`, up to 64
characters) is kept; anything else is replaced by the request's trace id. The id is added to the Activity
(`correlation.id` tag) and to the logger scope, and the web app forwards it on its calls to the API, so one id links the
web and API log lines of a request. The W3C `traceparent` header also propagates between web and API through the
standard HTTP instrumentation, so traces join up in your OTLP backend.

Not done yet: the worker does not start a trace per job run, so worker log lines do not share an id with the request
that triggered them.

## Metrics

Meter `FlowLedger`, exported when `OTEL_EXPORTER_OTLP_ENDPOINT` is set:

| Instrument | Tags | Meaning |
|---|---|---|
| `flowledger.job.runs` | `job`, `outcome` (success, failure, vetoed) | Background job runs. `vetoed` means another process held the job lock. |
| `flowledger.job.duration` (s) | `job`, `outcome` | Run duration. |
| `flowledger.job.last_success_age` (s) | `job` | Seconds since the last successful run (absent until the first success after a restart). |
| `flowledger.provider.calls` | `provider`, `status` (2xx, 4xx, 5xx, error) | Outbound provider API calls. |

## Sample alert rules (Prometheus syntax, unverified)

Metric names below assume the usual OTLP-to-Prometheus conversion (dots become underscores, counters get `_total`,
histograms `_seconds_*`); check the names your collector actually exposes before loading these.

```yaml
groups:
  - name: flowledger
    rules:
      - alert: FlowLedgerBackupFailed
        expr: increase(flowledger_job_runs_total{job_name="ScheduledBackupJob",outcome="failure"}[1d]) > 0
        labels: { severity: page }
      - alert: FlowLedgerBackupStale
        expr: flowledger_job_last_success_age_seconds{job="ScheduledBackupJob"} > 129600   # 36 h
        labels: { severity: page }
      - alert: FlowLedgerSyncStale
        expr: flowledger_job_last_success_age_seconds{job="ScheduledSyncJob"} > 172800     # 48 h
        labels: { severity: warn }
      - alert: FlowLedgerProviderAuthFailing
        expr: increase(flowledger_provider_calls_total{status="4xx"}[1h]) > 10
        labels: { severity: warn }
      - alert: FlowLedgerNotReady
        expr: probe_success{instance=~".*/ready"} == 0                                       # blackbox exporter on GET /ready
        for: 2m
        labels: { severity: page }
```

Note the label for the job tag is `job` in the OTel tags and may be renamed by your scrape config (`job` is reserved by
Prometheus, so it commonly becomes `exported_job`); adjust the selectors to match.

## Not yet implemented

- Append-only audit log of sensitive actions (link/unlink, key changes, export/import/backup-now, destructive edits).
  It needs a new table, so it is tracked separately from this slice.
- JSON console logging in Production, Npgsql/EF/Quartz instrumentation, dashboards.
