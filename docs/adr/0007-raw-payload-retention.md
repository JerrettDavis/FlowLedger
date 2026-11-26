# ADR 0007 — Raw provider payloads are retained indefinitely

## Status
Accepted (supersedes the "short retention" default in PLAN §7.5 for raw payloads).

## Context
Real provider data is expensive: every MX call is billable and aggregated history cannot be
re-fetched for free. PLAN §7.5 suggested short, configurable retention of raw payloads. During the
demo-to-real-data transition, losing a response we already paid for — or being unable to re-ingest
after a mapping bug — is unacceptable.

## Decision
1. Every MX response body is written byte-for-byte to the `IRawPayloadArchive` (object storage,
   `raw/{provider}/{yyyy}/{MM}/{dd}/{ticks}-{operation}-{sha256}.json`) **before** it is parsed.
   If the write fails, the call fails. Verified inbound webhook bodies are archived before parsing.
2. Nothing in the codebase deletes or overwrites archive objects. Retention is "keep everything";
   any future pruning must be an explicit, separate operator action.
3. The provider member (MX user + member) is provisioned once per tenant and persisted
   (`provider_connections`, unique per tenant+provider). POST calls are not auto-retried.
4. Account balances are appended to `account_balance_snapshots` on every sync (history is kept;
   `Account.CurrentBalance` remains the latest value).
5. The archive directory is pinned to a persistent path (AppHost: `~/.flowledger/objects`; compose:
   `objects_data` volume) and `eng/scripts/backup.ps1` backs up DB + archive together.

## Amendment: deletion exception (tenant offboarding)
"Keep everything" applies to routine operation. When a tenant is offboarded or a data-subject deletion is
requested, the tenant's archive objects and rows MUST be removable by an explicit operator action; the
procedure and the current implementation gap are documented in
`docs/privacy/data-inventory-and-retention.md`. Backups age out under the restic retention window, or are
crypto-shredded by destroying the repository password.

## Consequences
- Disk grows without bound; acceptable for a personal-scale deployment. Secrets/PII live in the
  archive: protect the directory like the database.
- Unverified (bad-signature) webhooks are not archived, to avoid disk-fill by anonymous callers.

## Amendment: deletion exception (2026-11-26)
"Keep everything" is the default, not an absolute. A tenant's right to have their data deleted overrides it: when a tenant
offboarding path exists it must remove that tenant's archive objects, and existing backups must expire on a stated schedule
(or be crypto-shredded). **That path is not built yet**; until it is, nothing deletes archive objects and the archive layout
(`raw/{provider}/{yyyy}/{MM}/{dd}/...`) is not per tenant, so per-tenant deletion would need a layout or encryption change.
Decision 2 above ("nothing in the codebase deletes or overwrites archive objects") remains true of the code today. See
[data inventory and retention](../privacy/data-inventory-and-retention.md).

## Follow-ups
- DONE: `MxArchiveReplayHandler` serves archived responses (by method+path+query, newest wins) through the real client/mapper; wiring it as the HttpClient handler gives a zero-cost re-ingest. Selected via `Mx:Provider:ReplayArchivePath`. Verified live: 910 sandbox transactions replayed into a fresh tenant with MX unreachable.
- DONE: Pending→posted handling. Same provider id: the pending row is settled in place. New id: a posted record absorbs a matching pending row (same account/amount/direction, pending date within 7 days before posting) and adopts its fingerprint. Covered by `PendingSettlementTests`.

## Verification (MX sandbox, 2026-11-13)
- Sync 1 added 910 transactions / 6 accounts; sync 2 added 0 (310 re-read in the 30-day overlap, all deduped).
- Cursor persisted as a date watermark (page 1, from today-30d). 31 raw payloads + sidecars archived; sandbox tenant isolated from the simulated demo tenant.
- Widget member matched the single MX member; the saved connection resolves PENDING to it on each sync.
- Resolved: `IMemberIdResolver` lets the sync persist the real member id once, so later syncs skip ListMembers.
