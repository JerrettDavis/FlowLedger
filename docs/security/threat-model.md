# Threat model (STRIDE per trust boundary)

Scope: a self-hosted FlowLedger stack (Web, API, Worker, PostgreSQL, Redis, object archive) handling
household financial data. This is a living document; each row names the control that exists today and the
residual gap. Controls marked "(verify)" are described in ADRs/docs but have no dedicated automated test yet.

## Assets

| Asset | Sensitivity |
|---|---|
| Transactions, balances, account names | High (financial PII) |
| Provider credentials / access URLs (MX, Quiltt, SimpleFIN) | Critical |
| Raw provider payload archive (ADR 0007) | High; contains everything the provider returned |
| API key, OIDC client secret, Data Protection key ring, HA encryption key | Critical |
| Backups (DB dumps + archive) | Same as the live data |

## Trust boundaries and threats

### 1. Browser to Web (OIDC cookie session)

| STRIDE | Threat | Control | Gap |
|---|---|---|---|
| S | Session forgery | Cookie auth over OIDC; persisted Data Protection key ring (`DataProtection__KeyPath`) | Key ring is a secret: protect and back up the volume |
| T | CSRF on interactive forms | Blazor antiforgery | none known |
| R | Cannot show who did what | Serilog request logs only | No append-only audit log (#193) |
| I | Cross-tenant data via UI | Household claim gate (`HouseholdAccess`), tenant mapping validator | Tests cover mapping; no end-to-end tenant isolation test through the UI |
| D | Login flood | Rate limiting on API; none on Web | Put a reverse proxy rate limit in front |
| E | Anonymous access outside Development | Web refuses to start without OIDC unless `Auth__AllowAnonymous=true` (tested: `SignInRequiredTests`) | LAN-only escape hatch exists by design |

### 2. Web to API (shared API key + tenant header)

| STRIDE | Threat | Control | Gap |
|---|---|---|---|
| S | Stolen API key | Key from env/secret store, production secret guard in API startup | Single shared key, no per-caller identity; rotate per [key-rotation](../runbooks/key-rotation.md) |
| T/I | Caller picks any `X-Tenant-Id` | API key is the tenant authority boundary; EF global query filters by tenant | Anyone holding the API key can address any tenant: keep the API port off untrusted networks |
| D | Request flood | Fixed-window rate limits (api/write/webhook) | none |
| E | Missing auth on a new endpoint | Fallback authorization policy requires auth by default | none |

### 3. API/Worker to PostgreSQL and Redis

| STRIDE | Threat | Control | Gap |
|---|---|---|---|
| I | DB exposed to network | Compose publishes Postgres/Redis on loopback only | Redis has no auth: never expose |
| T | Cross-tenant write | EF global filters; tenant context per request/job | Raw SQL paths (duplicates, advisory locks) must pass tenant explicitly (review on change) |
| D | Concurrent migrations/syncs | Advisory locks (sync per tenant, migration) | Quartz is in-memory: run a single worker replica (#187) |

### 4. API/Worker to external providers (MX, Quiltt, SimpleFIN, generic REST/SOAP/webhooks)

| STRIDE | Threat | Control | Gap |
|---|---|---|---|
| S | Forged inbound webhook | Signature verification before parse; unverified bodies are not archived | none known |
| T | Tampered payload | TLS; payloads archived byte-for-byte before parsing | none |
| I | SSRF via user-supplied URL (HA webhook, REST/SOAP connectors) | SSRF guard on outbound targets (see commit 077231b) | Re-test when adding new URL-accepting features |
| I | Credentials in logs | Secrets are not logged by convention (verify) | No log-scrubbing test |
| D | Provider outage or quota burn | Min sync interval, provider call ledger, bounded fan-out | See [provider outage runbook](../runbooks/provider-outage.md) |

### 5. Operator host (disk, backups, off-site copy)

| STRIDE | Threat | Control | Gap |
|---|---|---|---|
| I | Backup theft | Off-site copy is encrypted with restic; local dumps are not | Encrypt the local backup directory (disk encryption) |
| I | Raw archive holds PII indefinitely | Directory permissions; ADR 0007 | Deletion path is a tracked gap (see [data inventory](../privacy/data-inventory-and-retention.md)) |
| T | Image supply chain | Actions pinned by SHA, Trivy scan, SBOM; digest pinning of base images pending (#190) | No image signing yet (#186) |

## Out of scope

Compromise of the host OS, of the identity provider, or of a provider's own systems.

## Review cadence

Revisit on every new connector, new URL-accepting feature, new trust boundary, or auth change.
