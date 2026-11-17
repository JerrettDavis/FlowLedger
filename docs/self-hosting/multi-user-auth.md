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

## 3. Keep your existing data
Set on the **API** and **Worker**: `Tenancy__LegacyOwnerEmail=<your email as the IdP reports it>`. The first sign-in
from that email adopts the existing household tenant (all current accounts/transactions). Everyone else gets an
empty tenant. Without this setting the *first* user to sign in adopts it.

## Notes
* Linked bank connections (SimpleFIN/MX) belong to the household owner only; other users start empty.
* The shared `Api__Key` is the Web↔API trust boundary: keep the API on a private network.
