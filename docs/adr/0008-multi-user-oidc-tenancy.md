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
  (`Tenancy:LegacyTenantId`, default `Api:TenantId`, default `…0001`). Adoption fails closed: only the identity named by
  `Tenancy:LegacyOwnerSubject` (+ optional issuer), or `Tenancy:LegacyOwnerEmail` with `email_verified=true`, adopts it.
  With nothing configured, nobody does.
* **Provider credentials stay with the household**: configured provider credentials (SimpleFIN access URL, MX) are
  synced only for the legacy tenant. Other tenants get an empty sync (`UnlinkedTenantSyncService`) so one user's bank
  data can never land in another's. Per-user institution linking is future work.
* **Background jobs** fan out over every tenant (`TenantFanOut`), one DI scope per tenant, failures isolated.

## Not done / follow-ups
* Per-user SimpleFIN/MX linking (needs encrypted per-tenant credential storage).
* Shared households (several users, one tenant) and an admin role.
* Direct JWT bearer on the API for non-browser clients.

## Addendum — several logins, one person (multi-login linking)
`app_users` rows are *logins*; several logins may point at the same `tenant_id`. A new login joins an existing tenant by, in order:
1. **Configured owner** (`Tenancy:LegacyOwnerSubject` / `LegacyOwnerEmail`, each a comma/semicolon list): joins the legacy household tenant, even after an earlier owner login already adopted it.
2. **Verified email** equal to an existing, verified login's email (`Tenancy:AutoLinkVerifiedEmail`, **opt-in, default off**: a weak IdP email claim would otherwise allow account takeover; prefer link codes). Unverified emails never auto-link unless `Tenancy:TrustUnverifiedEmail=true` — leave that off if users can edit their own email in the IdP, because it would let them join someone else's data.
3. Otherwise it gets a fresh empty tenant (a spouse's login lands here), and can be linked later.

**Link code** (self-service, any IdP): *Linked logins* page → "Generate code" on the main login; sign in with the other login and enter it. Codes are single-use, 15 minutes, stored hashed. Redeeming re-points only that login; the login's previous tenant and its rows are left untouched (nothing is merged or deleted). A login can be unlinked (gets a new empty tenant); a tenant's last login cannot be unlinked.

Migration is additive (`email_verified` column, indexes, `app_user_link_codes` table; `CREATE/ALTER ... IF NOT EXISTS`); existing rows keep their tenant. AppHost forwards `Tenancy__*` to Api and Worker and pins `Tenancy__LegacyTenantId` so a `Worker:TenantId` override cannot change which tenant owners adopt.
