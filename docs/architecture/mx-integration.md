# MX Integration

## Overview

FlowLedger integrates with [MX.com](https://www.mx.com/) for real bank data. The integration is behind a feature flag so development and CI work without credentials.

## The Provider Seam

The abstraction lives in `FlowLedger.Integrations.Abstractions`:

```
IFinancialDataProvider
  ├── SimulatedProvider   (default, always on, generates deterministic fake data)
  └── MxProvider          (real MX.com API, activated by Mx:Enabled=true)
```

The application core only talks to `IFinancialDataProvider`. Swapping providers is a config change, not a code change.

## Feature Flag

```json
{
  "Mx": {
    "Enabled": false
  }
}
```

- `false` (default): SimulatedProvider is registered. No API calls, no charges.
- `true`: MxProvider is registered. Real MX.com API is used.

## Plug-In Steps

1. Get MX API credentials from [dashboard.mx.com](https://dashboard.mx.com)
2. Set these in `appsettings.json` or environment variables:

| Key | Description |
|-----|-------------|
| `Mx:Enabled` | `true` to activate real MX |
| `Mx:ApiKey` | Your MX API key |
| `Mx:ClientId` | Your MX client ID |
| `Mx:BaseUrl` | `https://int.mx.com` (sandbox) or `https://api.mx.com` (prod) |
| `Mx:WebhookSecret` | Used for HMAC verification of webhook payloads |

3. Restart the app.

## Webhook Setup

Configure your MX dashboard to POST events to:

```
POST /api/webhooks/mx
```

The `Mx:WebhookSecret` is used to verify the HMAC signature on each webhook payload.

## Sync Cursor

Each connected member's sync position is persisted in the `SyncCursors` table. This ensures:

- Syncs resume from the last point after a restart
- No duplicate transaction imports on retry
- Incremental sync (only new transactions since last cursor)

See [ADR 0004](../adr/0004-sync-cursor-persistence.md) for the full rationale.

## Cost Controls

MX API charges per call. To avoid accidental charges:

- Keep `Mx:Enabled=false` in development and CI (default)
- Use `https://int.mx.com` (sandbox) for integration testing
- Use `https://api.mx.com` (production) only in production deployments

The `FlowLedger.Integrations.Tests` project has live-sandbox tests that are skipped by default (skip trait) and require real sandbox credentials.
