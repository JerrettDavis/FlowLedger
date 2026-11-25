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

## Verified against Quiltt docs (2026-11-25), not yet against a live sandbox
- Session API: `POST auth.quiltt.io/v1/users/sessions`, Bearer API secret, `{userId}` for an existing profile; response `token/userId/expiresAt`.
- GraphQL: `accounts` is an **unpaginated list** with `kind` (DEPOSITORY/INVESTMENT/CREDIT/LOAN/INSURANCE), not `type`; `transactions` is Relay-paginated
  (`first<=100`, `filter.date_gte`, `sort: DATE_ASC`). Connection status enum: SYNCED, SYNCING, INITIALIZING, ERROR_REPAIRABLE/INSTITUTION/PROVIDER/SERVICE, DISCONNECTED.
- Webhooks: `Quiltt-Timestamp` + `Quiltt-Signature` = Base64(HMAC-SHA256(secret, `"1" + timestamp + body`)); body batches `events[]` each with `profile.id`.
- Connector JS: `cdn.quiltt.io/v1/connector.js`, `Quiltt.authenticate(token)`, `Quiltt.connect(id, {onExitSuccess,...}).open()`.

## Still unverified until keys arrive
`institution { name }` field list, webhook processing of multi-event/multi-profile batches (only the first event drives the sync),
and an end-to-end Connector link in the sandbox. Webhook-triggered syncs currently run for the household tenant (no profile-to-tenant lookup yet);
scheduled sync is the per-tenant backstop.

## Live-run checklist (first sandbox session)
1. Open Connect bank, link Quiltt's mock institution; confirm `ProviderConnections` row for `Quiltt` and accounts appear after a sync.
2. Watch for **session rate limits**: Quiltt allows 10 sessions/hour and 20/day per profile and every sync mints one. If hit (HTTP 429), cache the token
   until `expiresAt` or switch server-to-server GraphQL to Basic auth (`profileId:apiKeySecret`, documented as unlimited).
3. Send a test webhook from the Quiltt dashboard; only `connection.synced.successful*`, `connection.created` and `account.verified` trigger a sync (`IsSyncTrigger`).
4. Confirm `institution { name }` is accepted by the GraphQL schema (not listed in the docs we read).
