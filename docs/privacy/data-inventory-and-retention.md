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
| Backups and exports | One CSV per table plus manifest; restore-drill scratch data | Local `Backup__Directory` (unencrypted); off-site restic repository (encrypted) | Off-site only | Local: last `Backup__KeepLocalSnapshots` (default 30) snapshots; pruning never touches `premigrate-*` snapshots, and no off-site pruning policy is configured by FlowLedger (set one in restic) |
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

- **Tenant offboarding: implemented (hard delete, no retention window).** The household admin calls `POST /api/data/tenants/{id}/offboard`
  (see `GET /api/data/tenants?status=orphaned` for tenants left without a login after a link-code re-point). It deletes the tenant's
  rows from every tenant-scoped table (including the outbox and the ad-hoc tables), the tenant's logins and link codes, and its
  raw archive objects (`raw/{provider}/{tenant}/...`); the `tenants` registry row stays, marked deleted, as the audit record. The household
  tenant is refused. Archive objects written before the tenant folder existed carry no tenant and are not removed by it. Provider
  revocation and existing backups are outside this path (see below).
- **Provider revocation is manual.** Remove the connection in the provider's dashboard (MX, Quiltt, SimpleFIN Bridge); FlowLedger
  does not call a revoke endpoint.
- **Backups.** Deleting data from the live database does not remove it from existing local or off-site backups. Off-site backups are
  encrypted, so destroying the restic password (crypto-shredding) is the only way to make old copies unreadable, and it renders
  all of them unreadable.
- **What an operator can do today:** for a single-household instance, delete the database volume, the archive volume (`objects_data`),
  local backup directories and the restic repository, and revoke provider links. Per-tenant deletion inside a multi-user instance
  uses the offboarding endpoint above.
- **Export:** a full snapshot export exists (`GET /api/data/export`, whole database, loopback only). A per-user export is tracked
  separately.

### Still required to meet a deletion SLA

1. Self-service deletion by the tenant owner (today only the household admin can offboard a tenant) and a stated deletion SLA.
2. Provider revocation from FlowLedger where the provider offers a revoke endpoint (today it is manual).
3. A documented backup expiry policy so deleted data ages out of backups within a stated period.

Done: row and file erasure per tenant, a per-tenant archive layout, and tests proving no tenant rows remain
(`tests/FlowLedger.Infrastructure.Tests/DataSafety/TenantErasureTests.cs`).

[ADR 0007](../adr/0007-raw-payload-retention.md) carries the amendments recording that retention is "keep forever" by default and that offboarding removes a tenant's archive objects.

## Breach handling

The disclosure process is in [SECURITY.md](../../SECURITY.md); the operator's incident steps are in
[../runbooks/incident-response.md](../runbooks/incident-response.md). FlowLedger has no automated breach detection or notification.
