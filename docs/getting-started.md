# Getting Started

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.301 or later)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (for integration tests and full-stack mode)
- (Optional) MX API credentials from [dashboard.mx.com](https://dashboard.mx.com)

## Quick Start

```powershell
# Windows
./eng/scripts/run.ps1
```

```bash
# Linux / macOS
./eng/scripts/run.sh
```

This is the single command that brings up the full stack. Aspire orchestrates Postgres,
Redis, API, Worker, and Web, then opens the dashboard.

Open the Aspire dashboard at `https://localhost:15888` — it shows all service URLs, logs,
and the login token required on first access.

> **Troubleshooting:** If `dotnet` crashes with exit code 0xC0000005, your .NET SDK has
> a corrupted ReadyToRun image. Reinstall the .NET 10 SDK from https://dot.net to fix it.
> This is a one-time repair; no environment variable workaround is needed.

Also ensure **Docker Desktop is running** before starting Aspire.

## Run Modes

### 1. Aspire Dev Loop (Recommended for development)

The fast inner loop. Orchestrates API, Worker, Web, Postgres, and Redis with service discovery and a dashboard.

```bash
dotnet run --project src/FlowLedger.AppHost
```

Open the Aspire dashboard at `https://localhost:15888` to see all services and their logs.

### 2. Docker Compose Full Stack (E2E / Self-Hosting)

A fully containerized stack for self-hosting and Playwright E2E tests. (Ensure Docker Desktop is running.)

```bash
docker compose -f docker-compose.full.yml up
```

## MX Integration

By default, FlowLedger uses simulated (fake) bank data — no credentials needed.

To switch to real MX.com data, set the `Mx:*` configuration keys in the **AppHost** project
using `dotnet user-secrets`. This is the single place to set — Aspire forwards the values
to both the API and Worker automatically.

```powershell
cd src/FlowLedger.AppHost
dotnet user-secrets set "Mx:Enabled"       "true"
dotnet user-secrets set "Mx:ApiKey"        "your-api-key"
dotnet user-secrets set "Mx:ClientId"      "your-client-id"
dotnet user-secrets set "Mx:BaseUrl"       "https://int-api.mx.com"
dotnet user-secrets set "Mx:WebhookSecret" "your-webhook-secret"
```

Then run `aspire run` (or `./eng/scripts/run.ps1`) as normal. The app will use real MX data.

Use `https://int-api.mx.com` for sandbox and `https://api.mx.com` for production.

To restore Simulated mode:

```powershell
cd src/FlowLedger.AppHost
dotnet user-secrets clear
```

> **Fail-fast:** If `Mx:Enabled` is `true` but any credential is missing, the app refuses
> to start and names the missing field. Supply all four credentials (ApiKey, ClientId,
> BaseUrl, WebhookSecret) before enabling.

See [docs/architecture/mx-integration.md](architecture/mx-integration.md) for the full integration guide.

## Next Steps

- [Testing Guide](development/testing.md)
- [MX Integration](architecture/mx-integration.md)
- [Architecture Overview](architecture/overview.md)
