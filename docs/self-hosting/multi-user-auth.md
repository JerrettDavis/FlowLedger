# Multi-user sign-in (OIDC)

FlowLedger signs users in through **any OpenID Connect provider**. Each user gets a private tenant; users never
see each other's data.

## 1. Register FlowLedger in your IdP
authentik: *Applications → Create with Provider → OAuth2/OpenID*, slug `flowledger`, client type **Confidential**,
redirect URI `https://<flowledger-host>/signin-oidc`, post-logout `https://<flowledger-host>/signout-callback-oidc`,
scopes `openid profile email`. The issuer is `https://<authentik>/application/o/flowledger/`.
Other IdPs: use their issuer URL; same redirect URIs.

## 2. Configure the Web app
```
Auth__Oidc__Authority=https://auth.example.com/application/o/flowledger/
Auth__Oidc__ClientId=...
Auth__Oidc__ClientSecret=...
Auth__Oidc__RequireHttpsMetadata=true
```
(User-secrets work too: `dotnet user-secrets set "Auth:Oidc:Authority" ... --project src/FlowLedger.Web`.)
Leave `Authority` empty for single-household mode with **no sign-in** (development only; logged as a warning).

## 3. Keep your existing data (required, fails closed)
Existing accounts/transactions live in the household tenant. Nobody adopts it automatically. Bind it to yourself on the
**API** and **Worker**, preferably by IdP subject (find it in the authentik user's "attributes"/`sub`, or the `app_users`
table after a first sign-in):
```
Tenancy__LegacyOwnerSubject=<your sub>
Tenancy__LegacyOwnerIssuer=https://auth.example.com/application/o/flowledger/   # optional
```
or by email, honoured only when the IdP reports `email_verified=true`:
`Tenancy__LegacyOwnerEmail=you@example.com`. Adoption happens at your first sign-in; if you signed in before setting
this, point `app_users.tenant_id` for your row at the household tenant (or ask for a helper).

## Notes
* Linked bank connections (SimpleFIN/MX) belong to the household owner only; other users start empty. Scheduled syncs
  for other tenants are a no-op; they never touch the household's SimpleFIN/MX credentials.
* **Data & Backups** (`/data`, `/api/data/*`: backup state, export/import/backup-now of the *whole* database) is limited
  to logins mapped to the household tenant; other tenants get 403 and the menu item is hidden. With OIDC on and no
  `Tenancy__LegacyOwner*` configured, nobody is mapped to the household tenant, so nobody can use it (fail closed).
* The shared `Api__Key` is the Web↔API trust boundary: keep the API on a private network.
* **Who can sign up (#145).** Default `Auth__SignUp__Mode=InviteOnly`: only logins whose *verified* email matches `Auth__Oidc__AllowedEmails__0=you@example.com` and/or `Auth__Oidc__AllowedEmailDomains__0=example.com` get a new account; existing logins and the `Tenancy__LegacyOwner*` owner always sign in; with no allowlist nobody new can join. `Closed` admits nobody new. `Auth__SignUp__LegacyOpen=true` (or `Mode=Open`) restores the old behaviour where any login the IdP authenticates gets its own tenant (startup Warning). Prefer also restricting the application at the IdP. Never combine `Tenancy__TrustUnverifiedEmail=true` with `Tenancy__AutoLinkVerifiedEmail=true` without pinning `Tenancy__LegacyOwnerIssuer` (account takeover if the IdP lets users choose their email).
* Paid external lookups (MX connect, named asset-valuation feeds such as RealtyAPI) run on the operator's keys and are limited to the household tenant; CSV/sheet imports are capped at 5 MB per request.
* Run the API with `ASPNETCORE_ENVIRONMENT=Production` and a real `Api__Key` when onboarding others: the Development tenant context falls back to the household tenant when no tenant header is sent, and Development ships a public dev key.
* **Fail closed without leaving Development.** Set AppHost user-secret `Tenancy:RequireTenantHeader=true` (env `Tenancy__RequireTenantHeader=true` on the API) so a request with no valid `X-Tenant-Id` gets 401 instead of the household tenant. Use only with OIDC sign-in enabled.
* **Signed tenant (required with OIDC, #135).** Set `Api__TenantSigningKey` (compose: `API__TENANTSIGNINGKEY`) to a long random value *different from* `Api__Key` on **both** the API and the Web app (under Aspire: user-secret `Api:TenantSigningKey`, forwarded to both, and tell the API OIDC is on via `Auth:Oidc:*` or set `Api:RequireSignedTenant=true`). The Web app signs every request's `X-Tenant-Id` (HMAC bound to the HTTP method and path with a single-use nonce, 60-second validity; at least 32 characters, different from `Api__Key`; the old unbound format stays accepted while `Api__AcceptLegacyTenantSignature` is true, so upgrade the API first) and the API rejects any tenant it did not vouch for, so a leaked `Api__Key` alone can no longer read or write other tenants. With OIDC on outside Development both services refuse to start without it (opt-out `Api__RequireSignedTenant=false`). The household tenant id must also not be the well-known constant: a new install gets a random id generated and stored on the first api start (nothing to set); an existing install sets `Api__TenantId` to its id, or `Tenancy__AllowWellKnownLegacyTenantId=true` to keep the constant. Rotate both keys together. Sessions: `Auth__Session__AbsoluteMaxDays` (default 30) and the `__Host-flowledger.auth` cookie; see [upgrading.md](upgrading.md#upgrading-to-the-multi-user-secure-defaults-135-145-159).
