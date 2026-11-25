# 0017 - Quiltt embedded connector

Status: accepted (code complete; waiting on production keys)

## Context
MX and SimpleFIN are operator-held, household-only connectors. We want users to create accounts and link banks entirely
inside FlowLedger. Quiltt provides an embeddable Connector (browser) plus a per-user "profile" model and a GraphQL read API.

## Decision
- New project `FlowLedger.Integrations.Quiltt` implementing `IFinancialDataProvider`, registered with `SharedCredentials: false`:
  one Quiltt profile per tenant is the "member"; Quiltt isolates each user's data, so every tenant may link banks.
- `POST /api/integrations/quiltt/session` mints a short-lived profile session (server API key never reaches the browser);
  `GET /api/integrations/quiltt/config` tells the UI whether to open the in-app Connector (`wwwroot/quiltt-interop.js`).
- `POST /api/integrations/quiltt/webhooks`: `Quiltt-Signature` (`t=..,v1=hmac(t.body)`) verified with replay window, archived, then triggers a sync.
- Sync skips tenants with no Quiltt connection (never auto-creates profiles); incremental cursor = last posted date with an overlap window.
- Safe by default: `Quiltt:Enabled=false` (demo/Simulated). `Quiltt:Environment=production` marks the database PRODUCTION (DataEnvironmentGuard); sandbox marks it Sandbox.

## Config
`Quiltt:Enabled, ApiKeySecret, ConnectorId, WebhookSecret, Environment` (AppHost user-secrets, forwarded to api + worker).

## Unverified until keys arrive
GraphQL field names/filters (`QuilttApiClient` constants), webhook header/payload shape (`QuilttWebhookVerifier`, `ParseWebhookAsync`), Connector JS API
(`quiltt-interop.js`), account-type and status strings. All are isolated in those files and covered by stubbed tests.
Webhook-triggered syncs currently run for the household tenant (no profile-to-tenant lookup yet); scheduled sync is the per-tenant backstop.
