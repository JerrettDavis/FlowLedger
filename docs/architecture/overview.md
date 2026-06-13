# Architecture Overview

## System components

```
┌──────────────────────────────────────────────────────────────────┐
│                         FlowLedger                               │
│                                                                  │
│  ┌──────────┐    HTTP    ┌──────────┐    SQL    ┌────────────┐  │
│  │  Web     │──────────▶│  API     │──────────▶│ PostgreSQL │  │
│  │ (Blazor) │           │ (ASP.NET)│           └────────────┘  │
│  └──────────┘           └────┬─────┘    Redis  ┌────────────┐  │
│                               │────────────────▶│   Redis    │  │
│  ┌──────────┐    SQL          │                 └────────────┘  │
│  │  Worker  │─────────────────┘                                 │
│  │ (Quartz) │                                                    │
│  └──────────┘                                                    │
└──────────────────────────────────────────────────────────────────┘
```

## Services

| Service | Technology | Purpose |
|---------|-----------|---------|
| **FlowLedger.Api** | ASP.NET Core 10, Minimal API | REST API; authentication, sync triggers, financial data CRUD |
| **FlowLedger.Web** | Blazor Server + WASM, MudBlazor | Dashboard UI; talks to API over HTTP |
| **FlowLedger.Worker** | .NET Generic Host, Quartz.NET | Scheduled background sync (every 4 hours by default) |

## Infrastructure

| Component | Version | Role |
|-----------|---------|------|
| PostgreSQL | 17 | Primary datastore (EF Core, Npgsql) |
| Redis | 7 | Distributed cache and session (Aspire StackExchange.Redis) |

## Authentication

API key authentication (Bearer token or `X-Api-Key` header).
See [ADR 0001](../adr/0001-auth-openiddict-deferred.md) for why full OpenIddict is deferred.

## Multi-tenancy

All data is scoped to a `TenantId` (Guid) via EF Core global query filters.
Tenant resolution:
- **Development** — `DevTenantContext`: reads `X-Tenant-Id` header with fallback to demo tenant.
- **Production** — `HeaderTenantContext`: requires `X-Tenant-Id` header; fails with 401 if absent.

Default demo tenant: `00000000-0000-0000-0000-000000000001`

## Financial data providers

| Provider | Config | Notes |
|----------|--------|-------|
| Simulated | `Mx__Enabled=false` (default) | Deterministic demo household; no API key required |
| MX | `Mx__Enabled=true` + credentials | Real open banking data; requires MX account |

## Domain layers

```
FlowLedger.Domain               — Entities, value objects, domain events
FlowLedger.SharedKernel         — Cross-cutting interfaces (ITenantContext, etc.)
FlowLedger.Application          — Command/query handlers, validators (MediatR-style)
FlowLedger.Infrastructure       — EF Core DbContext, repositories, sync service
FlowLedger.Integrations.*       — Provider adapters (Simulated, MX)
FlowLedger.ServiceDefaults      — Aspire: OpenTelemetry, health checks, service discovery
```

## Developer entry points

- **Aspire dev loop** — `./eng/scripts/run.ps1` (see [local-dev.md](../development/local-dev.md))
- **Docker Compose** — `docker compose --profile full up` (see [docker-compose.md](../self-hosting/docker-compose.md))
- **CI** — `.github/workflows/ci.yml` (build + test) and `.github/workflows/docker.yml` (image build + SBOM + scan)
