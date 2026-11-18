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
