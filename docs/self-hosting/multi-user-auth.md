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
* **Restrict who can sign up.** By default any login your IdP authenticates is provisioned its own empty tenant. If the IdP is open to the public, set `Auth__Oidc__AllowedEmails__0=you@example.com` and/or `Auth__Oidc__AllowedEmailDomains__0=example.com`; when either list is non-empty only logins with a *verified* matching email are admitted. Prefer also restricting the application at the IdP.
* Paid external lookups (MX connect, named asset-valuation feeds such as RealtyAPI) run on the operator's keys and are limited to the household tenant; CSV/sheet imports are capped at 5 MB per request.
* Run the API with `ASPNETCORE_ENVIRONMENT=Production` and a real `Api__Key` when onboarding others: the Development tenant context falls back to the household tenant when no tenant header is sent, and Development ships a public dev key.
* **Fail closed without leaving Development.** Set AppHost user-secret `Tenancy:RequireTenantHeader=true` (env `Tenancy__RequireTenantHeader=true` on the API) so a request with no valid `X-Tenant-Id` gets 401 instead of the household tenant. Use only with OIDC sign-in enabled.
* **Signed tenant (recommended for multi-user).** Set `Api__TenantSigningKey` to a long random value *different from* `Api__Key` on **both** the API and the Web app (under Aspire: user-secret `Api:TenantSigningKey`, forwarded to both). The Web app then signs every request's `X-Tenant-Id` (HMAC, 60-second validity; the key must be at least 32 characters and differ from `Api__Key`, or the API refuses to start) and the API rejects any tenant it did not vouch for, so a leaked `Api__Key` alone can no longer read or write other tenants. Rotate both together.
