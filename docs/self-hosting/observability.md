# Observability

## Correlation ids

Every API and web response carries `X-Correlation-Id`. A well-formed inbound value (`[A-Za-z0-9._-]`, up to 64
characters) is kept; anything else is replaced by the request's trace id. The id is added to the Activity
(`correlation.id` tag) and to the logger scope, and the web app forwards it on its calls to the API, so one id links the
web and API log lines of a request. The W3C `traceparent` header also propagates between web and API through the
standard HTTP instrumentation, so traces join up in your OTLP backend.

Background jobs: every worker job run is its own root trace (`job <name>`, span source `FlowLedger.Worker`, with a `tenant work` child span per tenant). The run's correlation id is the trace id: it is the span tag `correlation.id`, W3C baggage (so internal HTTP calls forward it as `X-Correlation-Id`) and the `CorrelationId` log scope of every log line in the run. Quartz's own firing spans and meter are exported as well.

## Metrics

Meter `FlowLedger`, exported when `OTEL_EXPORTER_OTLP_ENDPOINT` is set:

| Instrument | Tags | Meaning |
|---|---|---|
| `flowledger.job.runs` | `job`, `outcome` (success, failure, vetoed) | Background job runs. `vetoed` means another process held the job lock. |
| `flowledger.job.duration` (s) | `job`, `outcome` | Run duration. |
| `flowledger.job.last_success_age` (s) | `job` | Seconds since the last successful run (absent until the first success after a restart). |
| `flowledger.job.retries` | `job` | Retries scheduled after a failed job run (see below). |
| `flowledger.provider.calls` | `provider`, `status` (2xx, 4xx, 5xx, error) | Outbound provider API calls. |
| `flowledger.sync.runs` | `outcome` (success, failure, throttled, already_running) | Financial syncs. |
| `flowledger.sync.duration` (s) | `outcome` | Duration of syncs that actually ran. |
| `flowledger.outbox.messages` | `outcome` (delivered, retried, dead_lettered) | Outbox deliveries; `dead_lettered` needs a human. |
| `flowledger.webhook.received` | `provider`, `outcome` (accepted, duplicate, invalid_signature, too_large) | Inbound provider webhooks. A burst of `invalid_signature` is someone probing the endpoint. |
| `flowledger.audit.events` | `action` | Audit events written. |

The `Npgsql` meter (connection pool and command metrics) and the `Npgsql` trace source are exported too.

EF Core has no stable built-in ActivitySource; its queries are traced through the `Npgsql` command spans (one per SQL command, parented to the request or job run). No third-party EF instrumentation package is used.

## Job history and retries

Every scheduled job run is also written to the `job_runs` table (`job, started_utc, finished_utc, success, error, attempt`;
90 days kept), so history survives a worker restart. `GET /api/data/status` returns the 20 newest as `jobRuns`.

A failed backup, asset-valuation or Home Assistant run is retried with exponential back-off and +/-50% jitter:
`Worker:JobRetry:MaxAttempts` (total runs, default 3, 1 disables), `BaseDelaySeconds` (300) and `MaxDelaySeconds` (3600).
The sync job is not retried here on purpose (every retry is a billable provider call; it applies its own per-connection
back-off). A retry is a one-off trigger carrying `retryAttempt`; the per-job advisory lock still prevents overlap.

### Missed fire times and replicas (#187)

The scheduler keeps its triggers in memory (Quartz RAMJobStore), and a persistent store (AdoJobStore with the QRTZ_ tables and
cluster check-ins) was deliberately not adopted: every trigger here is a static cron entry rebuilt from configuration on each start,
there is no job or trigger state worth persisting, and a clustered store adds schema, clock-skew and recovery settings to a
single-container compose deployment. Two small guarantees are built on one table instead, `job_fire_marks` (`job`, `last_fire_utc`,
`updated_utc`; created by an ordinary EF migration, so the `migrate` service applies it):

- **Once per fire time across replicas.** After taking the per-job advisory lock, a firing claims its cron slot with one conditional
  upsert (`last_fire_utc` only ever moves forward). A second replica that fires the same slot, even seconds after the first
  finished (when the lock is free again), is vetoed. A misfired firing is mapped back to the slot it was due for. Retries of a
  slot do not claim again.
- **Catch-up after downtime.** On startup, each cron job whose latest due slot is newer than its mark is run ONCE (never a burst),
  through the same claim. The very first start on a database only records the current slot. `Worker:CatchUp:Enabled=false` turns it
  off.

The claim is taken when a run starts, so a run that crashes mid-way is not repeated automatically for that slot (the existing
retry settings cover failures the job reports; the sync job also has its own per-connection back-off). If the marks table cannot be
reached the job runs anyway rather than never running.

## JSON logs

`Logging__Format=json` makes every host write one JSON object per line (timestamp UTC, level, category, message, and the
scopes, which carry the correlation id). The compose file sets it by default; `LOGGING__FORMAT=simple` restores plain
console output. Unset, the hosts keep the human-readable console.

## Audit log

Sensitive actions append a row to `audit_events` (created on first use, included in backups and restored with them):
`occurred_utc, tenant_id, actor, action, target, remote_ip, trace_id, status_code`. `trace_id` is the same W3C trace id that
appears in the logs, so a row can be joined to the log lines of that request. Audited actions: `connector.link`,
`connector.unlink`, `connector.connect`, `connector.mx_connect`, `connector.quiltt_session`, `data.export`, `data.import`,
`data.backup_now`, `home_assistant.save`, `integration.create|update|delete|rotate_secret`, `user.link_code|link|unlink`,
`secrets.reencrypt` (key rotation), `archive.seal`, `tenant.offboard`, `account.deactivate`, `asset.deactivate`, `recurring.deactivate`, `category.delete`, `category.clear_auto`, `transaction.merge_duplicate`, `budget.delete`, `goal.delete`.
Failed and denied attempts are recorded as well (`status_code`). The table is append-only by construction (the application has
no update or delete path), not by a database trigger; restrict the database role if you need it enforced. A failed audit write
is logged and does not change the response.

```sql
SELECT occurred_utc, actor, action, target, status_code FROM audit_events ORDER BY id DESC LIMIT 50;
```

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

- Failed API-key authentication attempts are not written to `audit_events` (an anonymous caller could otherwise fill the table);
  they are visible in the request logs only.
- Not every destructive edit is audited: deleting a single transaction, saved report, sheet row or rule is not (the actions listed above are).
- Dashboards (the metrics and spans are exported; no dashboard is shipped).
