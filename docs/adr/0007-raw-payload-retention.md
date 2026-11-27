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
"Keep everything" is the default, not an absolute. A tenant's right to have their data deleted overrides it: tenant offboarding
removes that tenant's archive objects (built; see the tenant-partition amendment below), and existing backups must expire on a
stated schedule or be crypto-shredded (the operator's responsibility). Decision 2 above ("nothing in the codebase deletes or
overwrites archive objects") therefore holds only for the default configuration with no offboarding and no opt-in retention sweep.
See [data inventory and retention](../privacy/data-inventory-and-retention.md).

## Follow-ups
- DONE: `MxArchiveReplayHandler` serves archived responses (by method+path+query, newest wins) through the real client/mapper; wiring it as the HttpClient handler gives a zero-cost re-ingest. Selected via `Mx:Provider:ReplayArchivePath`. Verified live: 910 sandbox transactions replayed into a fresh tenant with MX unreachable.
- DONE: Pending→posted handling. Same provider id: the pending row is settled in place. New id: a posted record absorbs a matching pending row (same account/amount/direction, pending date within 7 days before posting) and adopts its fingerprint. Covered by `PendingSettlementTests`.

## Verification (MX sandbox, 2026-11-13)
- Sync 1 added 910 transactions / 6 accounts; sync 2 added 0 (310 re-read in the 30-day overlap, all deduped).
- Cursor persisted as a date watermark (page 1, from today-30d). 31 raw payloads + sidecars archived; sandbox tenant isolated from the simulated demo tenant.
- Widget member matched the single MX member; the saved connection resolves PENDING to it on each sync.
- Resolved: `IMemberIdResolver` lets the sync persist the real member id once, so later syncs skip ListMembers.

## Amendment: tenant partition, optional sealing, opt-in retention (#142, #189)
- New objects are keyed `raw/{provider}/{tenant|_unattributed}/{yyyy}/{MM}/{dd}/...`. Webhooks are attributed to a tenant through the same member lookup as sync
  (best effort; an unknown member stays `_unattributed`). Objects written before this change keep their old key and are read exactly as before
  (the replay handler accepts both layouts). The `.meta.json` sidecar now also records the tenant.
- **Optional sealing, off by default.** `Storage:EncryptionKey` (base64, 32 bytes; `Storage:EncryptionKeyPrevious` for rotation) seals new payloads as
  `FLRAWv2:` plus a SecretBox v2 envelope bound to tenant and provider. Plaintext objects stay readable (the header tells them apart); the sidecar holds request metadata only and stays plaintext. Replay of sealed objects needs the same key configured on the replaying host, otherwise it fails loudly.
- **Opt-in retention, off by default.** `Storage:RawArchiveRetentionDays` > 0 makes the scheduled backup job delete archive objects older than that. Decision 2 above is unchanged for the default configuration.
- **Offboarding** (`POST /api/data/tenants/{id}/offboard`) deletes the tenant's database rows and every archive object under its tenant folder. Objects written before the tenant folder existed carry no tenant and are not removed by it.
