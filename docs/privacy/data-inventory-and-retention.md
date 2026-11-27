# Data inventory, retention and subprocessors

This page states what personal and financial data FlowLedger stores, where, for how long, who else receives it and what can be
deleted today. It describes the code as it is; where a capability does not exist it says so. The operator of the instance is the
data controller; this project ships software, not a service.

## Data inventory

| Data | Examples | Where | Encrypted by the app | Retention today |
|---|---|---|---|---|
| Financial records | Accounts, balances, balance snapshots, transactions, splits, categories, recurring flows, assets and valuations, saved reports, bill sheets | PostgreSQL (`accounts`, `transactions`, `account_balance_snapshots`, `transaction_splits`, `categories`, `recurring_flows`, `planned_flow_occurrences`, `assets`, `asset_valuations`, `saved_reports`, `sheet_bills`, `sheet_payments` and others) | No (disk/volume encryption is the operator's job) | Until the database is deleted; balance snapshots are appended on every sync and kept |
| User identity | OIDC subject, issuer, email | PostgreSQL (`app_users`) | No | Until the database is deleted |
| Provider connection metadata | Provider name, member/connection id, institution, status; sync cursors | PostgreSQL (`provider_connections`, `sync_cursors`) | No | Until the database is deleted |
| Raw provider payloads | Full bank API responses and verified webhook bodies, byte-for-byte | Object archive (`ObjectStorage:RootPath`, compose volume `objects_data`) | No | Kept indefinitely by default; removed by tenant offboarding or the opt-in `Storage:RawArchiveRetentionDays` sweep ([ADR 0007](../adr/0007-raw-payload-retention.md)) |
| Integration and Home Assistant secrets | Webhook URLs, signing secrets, credentials | PostgreSQL | Yes, AES-256-GCM with the configured key | Until deleted by the user or the database is deleted |
| Provider credentials | MX keys, Quiltt keys, SimpleFIN access URL | Configuration / environment only | n/a (not stored in the database) | Operator-managed |
| Backups and exports | One CSV per table plus manifest; restore-drill scratch data | Local `Backup__Directory` (unencrypted); off-site restic repository (encrypted) | Off-site only | Bounded by `Backup:Retention:*` (see [Backup expiry](#backup-expiry-policy)): local `backup-*` snapshots newest 30 (never one younger than 7 days), `premigrate-*` 90 days (newest always kept), off-site restic 7 daily / 4 weekly / 6 monthly via `restic forget --prune` |
| Logs and telemetry | Application logs, OpenTelemetry traces/metrics/logs | Container stdout and the OTLP backend, if configured | No redaction is wired | Set by the operator's log/OTLP backend |
| Provider API call ledger | Method, path, status, duration (no query strings) | PostgreSQL (`provider_api_calls`) | No | Until the database is deleted |

## Subprocessors and third parties

Only the providers the operator enables receive data. Nothing is sent to any of them while the Simulated provider is in use.

| Party | Purpose | Data they receive or hold | Enabled by |
|---|---|---|---|
| MX | Bank data aggregation | Bank credentials entered in the MX Connect widget (held by MX, not FlowLedger); returns accounts and transactions | `Mx__Enabled` |
| Quiltt | Embedded bank linking | Per-tenant Quiltt profile and bank credentials entered in the Quiltt Connector (held by Quiltt); returns accounts and transactions | `Quiltt__Enabled` |
| SimpleFIN Bridge | Bank data aggregation | Bank linking happens at the Bridge; FlowLedger holds the access URL in configuration | `SimpleFin__Enabled` |
| Backblaze B2 (or another restic backend) | Off-site backups | Restic ciphertext of snapshots and the raw archive | `Backup__Restic__Repository` |
| OIDC identity provider | Sign-in | Authentication traffic; receives the user's login, returns subject and email | `Auth__Oidc__*` |
| Home Assistant (operator's own) | Alerts | Alert text pushed to the webhook the user configures | Home Assistant page |
| FRED (public series) | Market rates for the strategy engine | No user data is sent (public CSV fetch) | Strategy engine |

Check each provider's own terms for their retention and deletion commitments; they are outside this project's control.

## Deletion and data-subject requests: current state

- **Self-service deletion: implemented.** The signed-in owner of a tenant calls `POST /api/account/delete-my-data` with the body
  `{"confirmation": "DELETE MY DATA"}` (the exact phrase; anything else is refused with 400 and nothing is touched). The route needs no
  household-admin token and acts only on the caller's own tenant (the id comes from the verified tenant context, never from the body).
  It first asks each enabled connector whose API supports it to revoke (see below), then runs the same erasure as offboarding, and
  writes an `audit_events` row (`tenant.self_delete`) plus a log line with counts only. The household tenant is refused (409): it
  holds the instance's own data and is removed by the operator (delete the volumes). Runbook: [tenant deletion](../runbooks/tenant-deletion.md).
- **Tenant offboarding: implemented (hard delete, no retention window).** The household admin calls `POST /api/data/tenants/{id}/offboard`
  (see `GET /api/data/tenants?status=orphaned` for tenants left without a login after a link-code re-point). It deletes the tenant's
  rows from every tenant-scoped table (including the outbox and the ad-hoc tables), the tenant's logins and link codes, and its
  raw archive objects (`raw/{provider}/{tenant}/...`); the `tenants` registry row stays, marked deleted, as the audit record. The household
  tenant is refused. Archive objects written before the tenant folder existed carry no tenant and are not removed by it. Existing
  backups are outside this path (see below).
- **Provider revocation: best effort, before erasure.** Self-service deletion calls every enabled connector that implements the
  optional `IProviderTenantRevoker` capability and reports the outcome per connector (`revoked`, `nothingtorevoke`, `notsupported`,
  `failed`). **MX**: the user FlowLedger created for the tenant (external id `flowledger-{tenant}`) is deleted at MX, which removes its
  members and aggregated data. **Quiltt**, **SimpleFIN Bridge** and the Simulated provider have no revoke call in FlowLedger, so they
  report `notsupported`: the person removes the connection in the Quiltt dashboard or at the SimpleFIN Bridge. A provider failure is
  logged (connector id and tenant id only) and never blocks the erasure; the response says `failed` so the operator can retry at the provider.
  Connectors that run on operator-held shared credentials only ever act for the household tenant, which cannot self-delete, so they report `nothingtorevoke`.
  Offboarding by the household admin (`/api/data/tenants/{id}/offboard`) does not call providers.
- **Backups.** Deleting data from the live database does not remove it from existing local or off-site backups; it ages out under the
  [backup expiry policy](#backup-expiry-policy) below. Off-site backups are encrypted, so destroying the restic password
  (crypto-shredding) is the only way to make old copies unreadable immediately, and it renders all of them unreadable.
- **What an operator can do today:** for a single-household instance, delete the database volume, the archive volume (`objects_data`),
  local backup directories and the restic repository, and revoke provider links. Per-tenant deletion inside a multi-user instance
  uses the offboarding endpoint above.
- **Export:** `GET /api/data/export` exports the calling tenant by default; the whole-database dump is the explicit `?scope=all`
  (disaster recovery, household tenant plus admin token). See [backups and recovery](../self-hosting/backups-and-recovery.md#data-administration-endpoints).

### Deletion timeline

After `delete-my-data` returns 200 the tenant's rows, logins and archive files are gone from the live system immediately. Copies in
backups expire by the policy below, so the **maximum time until the data is gone from every copy is the longest enabled retention
window**: with the defaults, about 6 months in the off-site repository (the 6th monthly snapshot), 90 days for `premigrate-*`
snapshots and roughly 30 daily backups locally. An operator who promises a shorter SLA lowers the keep settings accordingly.

### Backup expiry policy

Configured by `Backup__Retention__*` (environment) / `Backup:Retention:*`, enforced by the scheduled backup job only (an on-demand
"Back up now" never deletes). Pruning runs after a clean run, and with an off-site repository only once that copy is confirmed.

| Setting | Default | Effect |
|---|---|---|
| `Backup__Retention__KeepLocalSnapshots` (legacy `Backup__KeepLocalSnapshots`) | 30 | Newest scheduled `backup-*` snapshots kept on the host; none younger than 7 days is ever deleted |
| `Backup__Retention__PremigrateMaxAgeDays` | 90 | `premigrate-*` snapshots older than this are deleted; the newest is always kept as the rollback point; 0 keeps them forever |
| `Backup__Retention__ResticForget` | true | After each confirmed off-site copy run `restic forget --prune` for this job's tag and host |
| `Backup__Retention__ResticKeepDaily` / `ResticKeepWeekly` / `ResticKeepMonthly` | 7 / 4 / 6 | restic `--keep-daily` / `--keep-weekly` / `--keep-monthly`; 0 omits a rule; not all three may be 0 while `ResticForget` is on |

Snapshot age is read from the timestamp in the folder name, so copying or restoring the backup folder does not reset it. A failing
`restic forget` does not fail the backup (the new copy is safe) but is appended to the run's off-site detail (`retention FAILED: ...`)
and logged, so a retention gap is visible on the Data page. The raw payload archive itself is never pruned by this policy (see ADR 0007).

Still outstanding: a stated deletion SLA is the operator's to publish (the timeline above is what the software enforces); a per-user
(not per-tenant) export is tracked separately.

Done: row and file erasure per tenant, a per-tenant archive layout, owner self-service deletion, provider revocation where an API exists,
backup expiry, and tests (`TenantErasureTests`, `SelfServiceErasureTests`, `DeleteMyDataHandlerTests`, `ProviderTenantRevocationTests`,
`MxTenantRevocationTests`, `BackupRetentionTests`, `BackupRetentionRunTests`).

[ADR 0007](../adr/0007-raw-payload-retention.md) carries the amendments recording that retention is "keep forever" by default and that offboarding removes a tenant's archive objects.

## Breach handling

The disclosure process is in [SECURITY.md](../../SECURITY.md); the operator's incident steps are in
[../runbooks/incident-response.md](../runbooks/incident-response.md). FlowLedger has no automated breach detection or notification.
