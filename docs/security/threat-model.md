# Threat model (STRIDE per trust boundary)

Scope: a self-hosted FlowLedger stack (Web, API, Worker, PostgreSQL, Redis, raw payload archive, optional off-site backup)
holding financial data for one household or several users. This document records the controls that exist in the code today and,
separately, the known gaps. It is deliberately conservative: a control is listed only if it can be found in the repository. See
[SECURITY.md](../../SECURITY.md) for the disclosure policy and the short summary.

Status: first version, not externally reviewed. The pre-onboarding audit in
[../development/audit-2026-11-18.md](../development/audit-2026-11-18.md) lists shipped hardening and open items.

## Assets

- Financial data: accounts, balances, transactions, recurring flows, assets, goals (PostgreSQL).
- Raw provider payloads, byte-for-byte, kept indefinitely (object archive, [ADR 0007](../adr/0007-raw-payload-retention.md)).
- Credentials and secrets: provider API keys and webhook secrets (configuration/environment), the SimpleFIN access URL
  (configuration), integration and Home Assistant secrets (AES-256-GCM in the database), `API__KEY`, OIDC client secret.
- User identities (`app_users`: subject, issuer, email) and tenant membership.
- Backups and exports (unencrypted CSV snapshots locally; restic-encrypted off-site copy).

## Trust boundaries and data flows

1. Browser -> Web (Blazor, OIDC cookie session).
2. Web -> API (`API__KEY` bearer, optional signed tenant assertion `Api:TenantSigningKey`).
3. API and Worker -> PostgreSQL / Redis / archive volume.
4. API and Worker -> providers (MX, Quiltt, SimpleFIN Bridge) over HTTPS.
5. Providers -> API inbound webhooks (MX, Quiltt, generic signed integration webhooks).
6. API/Worker -> user-configured outbound URLs (Home Assistant webhook, integrations).
7. Worker -> off-site backup (restic to Backblaze B2 or another restic backend).
8. Operator -> data admin endpoints (export, import, backup-now), loopback only by default.

## STRIDE by boundary

### 1. Browser -> Web

| Threat | Existing control | Gap / note |
|---|---|---|
| Spoofing: unauthenticated access | OIDC sign-in; the Web UI refuses to start outside Development without OIDC unless `AUTH__ALLOWANONYMOUS=true` | Anonymous mode has no login at all; only for a trusted single-user host |
| Spoofing: login CSRF / open redirect | `/auth/relogin` rejects cross-site requests (`Sec-Fetch-Site`); `returnUrl` rejects control characters; SameSite cookie | |
| Tampering / XSS | Security headers (`src/FlowLedger.Web/Security/SecurityHeaders.cs`, tested in `tests/FlowLedger.Web.Tests/Security/SecurityHeadersTests.cs`) | No CSP review has been done in this document |
| Information disclosure | Cookie is `Secure`-only outside Development | Requires a TLS proxy for remote use |
| Denial of service | Request caps on CSV and bill-sheet import (5 MB, 50,000 rows) | No account lockout or per-user quota beyond entity caps |
| Repudiation | | No audit log of user actions |
| Elevation of privilege | Optional sign-up allowlist (`Auth:Oidc:AllowedEmails`, `AllowedEmailDomains`) | |

### 2. Web -> API

| Threat | Existing control | Gap / note |
|---|---|---|
| Spoofing a tenant | `API__KEY` bearer; optional HMAC-signed tenant assertion (`X-Tenant-Sig`, 60 s) so a leaked API key alone cannot pick a tenant | Assertion is opt-in. Anyone holding both secrets, or the Web process, can act as any tenant |
| Weak or default key | API refuses to start outside Development with the dev default key; startup refuses a weak or reused signing key | |
| Cross-tenant access | EF Core global query filters; per-feature tests | No systematic route-by-route isolation test (INV-01, see [invariants.md](../architecture/invariants.md)) |
| Denial of service | Rate limiter partitions (`RateLimitPartitionTests`) | Limits are not documented per route |
| Repudiation | | No per-request audit trail |

### 3. API and Worker -> database and archive

| Threat | Existing control | Gap / note |
|---|---|---|
| Information disclosure at rest | Integration and Home Assistant secrets encrypted with AES-256-GCM | Database, backups, exports and the raw archive are plaintext; rely on disk/volume encryption |
| Tampering with history | Raw payloads are append-only by convention (nothing in the code deletes them) | No integrity protection or immutability enforcement |
| Excess privilege | Docs advise a least-privilege database user | The application runs migrations with the same credentials |
| Data exposure via logs | Do not log sensitive values (convention) | No redaction is wired (see SECURITY.md); logs may contain institution names and webhook parse errors |

### 4 and 5. Providers and webhooks

| Threat | Existing control | Gap / note |
|---|---|---|
| Forged webhook | Signature verification (MX, Quiltt `Quiltt-Signature` with timestamp tolerance, signed integration webhooks); unverified bodies are not archived | |
| Webhook replay | MX replay guard (10-minute body-hash window); Quiltt timestamp window | No HTTP-level negative tests for every webhook endpoint (tracked in the testing backlog) |
| Credential theft | Provider credentials only in configuration or environment | Operator must protect the environment and secrets manager |
| Provider cost abuse | Daily/monthly call budgets, kill switch, per-tenant sync interval | |
| Provider-side data exposure | Provider receives only what the user links | See [privacy/data-inventory-and-retention.md](../privacy/data-inventory-and-retention.md) for subprocessors |

### 6. Outbound URLs (SSRF)

| Threat | Existing control | Gap / note |
|---|---|---|
| Server-side request forgery | `IntegrationUrl` and `HomeAssistantWebhookUrl`: link-local/metadata/multicast refused; loopback/private only for the household owner; https required for internet targets; destination re-checked at connect time | Coverage for other URL-accepting paths is not audited here |

### 7. Off-site backup

| Threat | Existing control | Gap / note |
|---|---|---|
| Disclosure | restic encrypts client-side; the provider sees ciphertext | Loss of the restic password makes backups unreadable; no documented rotation |
| Tampering / loss | Verified snapshot plus restore drill on every run | Local snapshots and exports are unencrypted |

### 8. Operator data admin endpoints

| Threat | Existing control | Gap / note |
|---|---|---|
| Remote bulk export/import | `/api/data/*` admin routes require the household tenant AND `DataAdmin__Key` / `X-Admin-Token` outside Development (loopback is accepted only in Development; `DataAdmin__AllowRemote=true` is an explicit opt-out) | Behind a reverse proxy on the same host, requests can appear as loopback; do not route these paths through the proxy |
| Import abuse | Import only into an empty database, hash and row-count verification, 8 GB expansion cap | |

## Top residual risks

1. Plaintext data at rest and indefinite raw archive with no deletion path (see the data inventory and ADR 0007).
2. Log content is not redacted.
3. Tenant isolation relies on query filters without a systematic proof.
4. The Web process holds enough secrets to act as any tenant.
5. No audit log of user actions.
