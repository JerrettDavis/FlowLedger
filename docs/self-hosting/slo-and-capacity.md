# Service objectives and capacity notes

FlowLedger is a single-household, self-hosted system. These are targets to guide operations, not guarantees.

## Targets

| Objective | Target | How to observe |
|---|---|---|
| Web/API availability | 99% monthly (about 7 hours of downtime) | `/alive` probe from outside; reverse-proxy logs |
| Data freshness | Every connected account synced within 24 hours (scheduled sync default is daily 12:00) | Last-synced time per connection; `Sync` job logs |
| Backup freshness | A verified backup newer than 26 hours | Data page backup status; daily job (default 03:15) |
| Recovery point (RPO) | 24 hours | Daily backup cadence |
| Recovery time (RTO) | 4 hours on the same host, 1 working day on a new machine | Quarterly [restore](../runbooks/restore.md) drill |

## Capacity assumptions

- One API replica, **one worker replica**. Quartz uses the in-memory store and the worker is not safe to scale out (tracked: #187). Per-tenant sync is protected by a Postgres advisory lock, so an accidental second worker cannot sync the same tenant twice, but scheduled backup/alerts are not.
- Web needs a persisted Data Protection key ring to run more than one replica (`DataProtection__KeyPath` on a shared volume).
- Tested scale is personal/household: tens of thousands to hundreds of thousands of transactions (the API caps stored transactions per tenant at 500k on manual creation).
- Disk grows without bound because raw payloads are retained forever (ADR 0007): budget roughly the size of the object archive times the number of backups kept locally.
- Postgres: 1 vCPU / 1 GB RAM is sufficient at this scale; Redis holds only transient locks/cache.

## Alerting

Sample alert rules are in [observability.md](observability.md).
