# ADR 0008 — Provider-agnostic multi-user (OIDC) with per-user tenants

Status: accepted (supersedes the "deferred" part of ADR 0001 for sign-in)

## Decision
* **Sign-in** is generic OpenID Connect in the Web app (the trusted back end / BFF). Any compliant IdP works
  (authentik, Keycloak, Entra ID, Google, Zitadel): configure only `Auth:Oidc:Authority/ClientId/ClientSecret`.
  Nothing in the code names a provider. Authorization-code + PKCE; cookie session.
* **Identity → tenant**: `app_users (issuer, subject) → tenant_id`, created on first login by the API
  (`POST /api/users/resolve`, API-key only). Each user owns exactly one tenant; all domain tables were already
  partitioned by `tenant_id`, so segmentation is the existing tenant filter, now driven by the signed-in user.
* **Web → API**: the Web server calls the API with the shared API key plus `X-Tenant-Id` taken from the
  signed-in user's `fl_tenant` claim, bound per Blazor circuit. With OIDC enabled a missing claim sends no tenant
  header, so the API fails closed.
* **Existing data**: the pre-multi-user household data lives in the legacy tenant
  (`Tenancy:LegacyTenantId`, default `Api:TenantId`, default `…0001`). The first user to sign in adopts it, or only
  `Tenancy:LegacyOwnerEmail` if set (recommended).
* **Provider credentials stay with the household**: configured provider credentials (SimpleFIN access URL, MX) are
  synced only for the legacy tenant. Other tenants get an empty sync (`UnlinkedTenantSyncService`) so one user's bank
  data can never land in another's. Per-user institution linking is future work.
* **Background jobs** fan out over every tenant (`TenantFanOut`), one DI scope per tenant, failures isolated.

## Not done / follow-ups
* Per-user SimpleFIN/MX linking (needs encrypted per-tenant credential storage).
* Shared households (several users, one tenant) and an admin role.
* Direct JWT bearer on the API for non-browser clients.
