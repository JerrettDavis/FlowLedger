# ADR-0001: Defer OpenIddict Integration to Phase 6 SaaS Milestone

**Date:** 2026-06-12  
**Status:** Accepted  
**Deciders:** FlowLedger maintainers

## Context

FlowLedger requires authentication to protect tenant financial data. OpenIddict packages (`OpenIddict.AspNetCore`, `OpenIddict.EntityFrameworkCore`) are pinned in `Directory.Packages.props` anticipating full OAuth2/OIDC implementation.

However, full OpenIddict integration requires:
- Authorization server with token endpoints
- Client/application registration tables
- Token introspection and validation
- Refresh token rotation
- Login UI or client-credentials flow

This represents significant surface area, higher risk, and is fundamentally a SaaS-milestone concern. The Phase 5 target is a self-hosted, single-household deployment where "plug in a key and roll" simplicity is more valuable than multi-client OAuth infrastructure.

## Decision

Phase 5 implements a minimal, config-driven API-key authentication scheme:

- **Scheme:** ASP.NET Core `AuthenticationHandler<AuthenticationSchemeOptions>` named "ApiKey"
- **Key source:** Pre-shared key from configuration (`Api:Key` via environment variable or user-secrets)
- **Transport:** Supplied as `Authorization: Bearer {key}` or `X-Api-Key: {key}` header
- **Validation:** Fixed-time comparison to prevent timing attacks

**Complementary controls:**
- Fail-closed tenant resolution (no demo fallback outside Development)
- Authorization enforced on all `/api/*` endpoints (health/liveness and HMAC-verified MX webhook excepted)
- Built-in rate limiting
- Secure response headers + HSTS
- Production startup guard for secret hygiene
- Serilog log redaction

**Deferral:** OpenIddict integration is deferred to the SaaS milestone (Phase 6).

## Consequences

### Positive
- Zero added authentication dependencies (framework types only)
- Trivial to test and reason about
- The ApiKey handler implements the standard `IAuthenticationHandler` contract; swapping to JWT Bearer or OpenIddict validation is a localized change in `AddAuthentication()`
- `ITenantContext` is decoupled from authentication, so Phase 6 can populate `TenantId`/`UserId` from JWT claims without touching EF query filters or domain logic

### Negative
- API keys do not expire or rotate automatically
- No per-client scopes or audience validation
- Shared secret must be distributed securely
- Not suitable for public multi-tenant scenarios

### Neutral
- OpenIddict package pins remain in `Directory.Packages.props` for the future milestone but are referenced by no project in Phase 5

## Phase 6 Migration Path

1. Add `OpenIddict.AspNetCore` PackageReference to `FlowLedger.Api.csproj`
2. Replace the ApiKey scheme registration with OpenIddict validation (`AddOpenIddict().AddValidation()`)
3. Update tenant context implementation to read `TenantId`/`UserId` from JWT claims instead of the `X-Tenant-Id` header
4. Remove `Api:Key` from configuration and the startup guard
5. Add OpenIddict application registration and authorization-server endpoints

The `ITenantContext` interface contract remains unchanged—only the concrete implementation and DI registration change.
