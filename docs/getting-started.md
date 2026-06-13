# Getting Started

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (for integration tests and full-stack mode)
- (Optional) MX API credentials from [dashboard.mx.com](https://dashboard.mx.com)

## Local SDK Note (Windows)

Before running tests or the dev server on Windows, set these environment variables to work around a known JIT regression in certain .NET 10 SDK images:

```powershell
$env:DOTNET_ReadyToRun = "0"
$env:COMPlus_ReadyToRun = "0"
```

If `dotnet` crashes, set these before running any commands. CI (Linux) does not need these.

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

To switch to real MX.com data:

1. Get credentials from [dashboard.mx.com](https://dashboard.mx.com)
2. Set the following in `appsettings.json` or environment variables:

```json
{
  "Mx": {
    "Enabled": true,
    "ApiKey": "your-api-key",
    "ClientId": "your-client-id",
    "BaseUrl": "https://int.mx.com",
    "WebhookSecret": "your-webhook-secret"
  }
}
```

3. Restart the app

Use `https://int.mx.com` for sandbox and `https://api.mx.com` for production.

See [docs/architecture/mx-integration.md](architecture/mx-integration.md) for the full integration guide.

## Next Steps

- [Testing Guide](development/testing.md)
- [MX Integration](architecture/mx-integration.md)
- [Architecture Overview](architecture/overview.md)
