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

## Configuration Reference

All keys live under the `Mx` section. When set via environment variables, use `__`
(double underscore) as the section separator — e.g. `Mx__ApiKey` for `Mx:ApiKey`.

### Credentials (`Mx` section — `FinancialProviderOptions`)

| Key | Required when enabled | Description |
|-----|-----------------------|-------------|
| `Mx:Enabled` | — | `true` to activate real MX. Default: `false`. |
| `Mx:ApiKey` | Yes | Your MX API key (from [dashboard.mx.com](https://dashboard.mx.com)). |
| `Mx:ClientId` | Yes | Your MX client ID (from [dashboard.mx.com](https://dashboard.mx.com)). |
| `Mx:BaseUrl` | Yes | `https://int-api.mx.com` (sandbox) or `https://api.mx.com` (production). |
| `Mx:WebhookSecret` | Yes | HMAC secret used to verify inbound MX webhook payloads. |
| `Mx:Environment` | No | Deployment hint: `"sandbox"` (default) or `"production"`. |

### Provider tunables (`Mx:Provider` section — `MxProviderOptions`)

| Key | Default | Description |
|-----|---------|-------------|
| `Mx:Provider:DefaultInstitutionCode` | `mxbank` | Institution code used during the MX Connect widget hand-off. `mxbank` is MX's sandbox test institution. In production, users pick their institution inside the Connect widget — override only when your deployment needs a fixed code. |
| `Mx:Provider:RecordsPerPage` | `100` | Page size for accounts/transactions API calls (MX allows up to 1000). |
| `Mx:Provider:ManualRefreshCooldown` | `00:15:00` | Minimum time between user-triggered refreshes per (tenant, member). Controls aggregation cost. |
| `Mx:Provider:MonthlyManualRefreshBudget` | `0` | Monthly cap on manual refreshes per tenant. `0` means no cap (reserved for future enforcement). |
| `Mx:Provider:MaxPages` | `100` | Safety cap on pages fetched per `GetAccountsAsync` call. |

## Swapping from the Simulated Provider to Real MX

> This is a configuration-only change — no code modifications are required.

**Prerequisite:** An MX.com account with sandbox credentials. Sign up at [dashboard.mx.com](https://dashboard.mx.com).

### For the Docker Compose stack (self-hosted / production)

1. Open your `.env` file (copy from `.env.example` if you have not already):

   ```bash
   cp .env.example .env
   ```

2. Set the MX variables:

   ```env
   Mx__Enabled=true
   Mx__ApiKey=<your-mx-api-key>
   Mx__ClientId=<your-mx-client-id>
   Mx__BaseUrl=https://int-api.mx.com        # sandbox
   # Mx__BaseUrl=https://api.mx.com          # production — change when ready
   Mx__WebhookSecret=<your-mx-webhook-secret>
   ```

3. In your MX dashboard, configure a webhook pointing to:

   ```
   POST https://<your-domain>/api/integrations/mx/webhooks
   ```

   Copy the signing secret MX shows you and set it as `Mx__WebhookSecret`.

4. Restart the stack:

   ```bash
   docker compose --profile full up --build
   ```

5. Verify startup — if any required credential is missing, the API **refuses to start**
   and logs which field is absent. There is no silent fallback to fake data when
   `Mx:Enabled=true`.

6. When ready for production traffic, change `Mx__BaseUrl` to `https://api.mx.com` and
   rotate secrets as described in [production.md](../self-hosting/production.md).

### For the Aspire dev loop (inner-loop development)

Use .NET user-secrets to keep credentials out of source control:

```powershell
cd src/FlowLedger.AppHost
dotnet user-secrets set "Mx:Enabled"       "true"
dotnet user-secrets set "Mx:ApiKey"        "<your-api-key>"
dotnet user-secrets set "Mx:ClientId"      "<your-client-id>"
dotnet user-secrets set "Mx:BaseUrl"       "https://int-api.mx.com"
dotnet user-secrets set "Mx:WebhookSecret" "<your-webhook-secret>"
```

Aspire forwards these values to API and Worker automatically — you do not need to set
secrets in the individual service projects.

To restore Simulated mode:

```powershell
cd src/FlowLedger.AppHost
dotnet user-secrets clear
```

> **Do not use user-secrets in production.** For production deployments, supply
> credentials via environment variables, an orchestrator secrets manager (Kubernetes
> Secrets, AWS Secrets Manager, Azure Key Vault), or HashiCorp Vault. See
> [production.md](../self-hosting/production.md).

## Webhook Setup

Configure your MX dashboard to POST events to:

```
POST /api/integrations/mx/webhooks
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
- Use `https://int-api.mx.com` (sandbox) for integration testing
- Use `https://api.mx.com` (production) only in production deployments

The `FlowLedger.Integrations.Tests` project has live-sandbox tests that are skipped by
default (skip trait) and require real sandbox credentials.
