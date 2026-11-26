# Data inventory, retention and subprocessors

## Data inventory

| Data | Where | Source | Retention today |
|---|---|---|---|
| Accounts, balances, balance snapshots | PostgreSQL | Providers / user entry / import | Kept until the user deletes the account row; snapshots are append-only history |
| Transactions, categories, rules, aliases, budgets, goals, strategies | PostgreSQL | Providers / import / user | Until deleted by the user |
| Provider connections and credentials | PostgreSQL (`provider_connections`, integration store) | User linking a provider | Until the connection is removed. Sensitive fields are encrypted where an encryption key is configured (Home Assistant URL: `HomeAssistant__EncryptionKey`) |
| Raw provider payloads | Object archive (`/data/objects`) | Every provider response, byte-for-byte | Indefinite (ADR 0007, with the deletion exception below) |
| Provider call ledger | PostgreSQL | Internal | Indefinite (cost records, no financial content) |
| Backups | `~/.flowledger/backups`, restic off-site repo | Internal | Operator-defined (daily; restic retention policy) |
| Application logs | stdout / OTLP | Internal | Per operator log stack; amounts and account numbers must not be logged |
| Data Protection key ring | `web_keys` volume | Internal | Until rotated |

## Subprocessors and external recipients

| Party | Purpose | Data shared |
|---|---|---|
| MX (optional) | Aggregation | Provider user/member ids; receives bank credentials directly via its widget, never via FlowLedger |
| Quiltt (optional) | Embedded bank linking | Connection ids, session tokens |
| SimpleFIN (optional) | Aggregation | Access URL claimed by the user |
| Backblaze B2 / any restic backend (optional) | Encrypted off-site backups | Encrypted blobs only; the restic password never leaves the host |
| Home Assistant (optional) | Alert push | Alert text only, never raw balances (by design) |
| OIDC identity provider | Sign-in | Identity claims |

## Deletion and the tenant offboarding gap

Status: **not implemented.** No code path deletes a tenant's rows or archive objects, and ADR 0007 says
nothing in the codebase deletes archive objects. Until offboarding exists, a deletion request is handled
manually by the operator:

1. Export the data for the requester if asked (see the export features in the app).
2. Revoke provider access first (MX/Quiltt/SimpleFIN consoles) so no new data arrives.
3. Delete the tenant's rows from every tenant-scoped table (all carry `tenant_id`) in a transaction.
4. Delete archive objects whose path/metadata belongs to the tenant (`raw/{provider}/...`; objects are not
   currently indexed by tenant, so on a single-household install this means the whole archive).
5. Expire backups: backups contain the deleted data until they age out of the restic retention window.
   Document that window to the requester; crypto-shredding (destroying the restic password and the repo)
   is the only way to remove the data from older backups immediately.

Tracked work: implement tenant offboarding end to end (rows, archive objects, provider revocation) with a
test proving no tenant rows/objects remain, and amend ADR 0007 with a deletion exception.

## Breach handling

Follow the [incident runbook](../runbooks/incident-response.md). Notification timelines depend on the
operator's jurisdiction; FlowLedger is self-hosted, so the operator is the data controller.
