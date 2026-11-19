# Operator checklist: inviting other people

Work through this before giving anyone else a login. Each item links to the detail.

## 1. Lock down who can reach and use the stack
- [ ] **Production mode.** Run the API and Web with `ASPNETCORE_ENVIRONMENT=Production` and a real, random `Api__Key`
  ([production.md](production.md)). If you must stay in Development (e.g. under Aspire), set
  `Tenancy:RequireTenantHeader=true` so a request without a tenant is refused instead of falling back to your household.
- [ ] **Signed tenants.** Set `Api__TenantSigningKey` (>= 32 random characters, different from `Api__Key`) on the API **and** the Web app.
  A leaked API key alone then cannot read or write another user's data ([multi-user-auth.md](multi-user-auth.md)).
- [ ] **Sign-up gate.** If your identity provider accepts anyone, set `Auth__Oidc__AllowedEmails__0` /
  `Auth__Oidc__AllowedEmailDomains__0`, and restrict the application at the provider too.
- [ ] **Keep the API private.** Only the Web app should reach it; do not publish its port.
- [ ] **TLS and proxy.** Terminate TLS in front; set `ForwardedHeaders__Enabled=true` on the Web app only if it is reachable solely
  through that proxy.

## 2. Protect your own data
- [ ] You are mapped to the household tenant (`Tenancy__LegacyOwner*`) so your history and the Data & Backups page stay yours.
- [ ] Backups run, a restore drill has passed, and an off-site copy is configured ([backups-and-recovery.md](backups-and-recovery.md)).
- [ ] Exports/backups contain **every** user's data including emails and are not encrypted at rest: keep the backup folder private.

## 3. Know what new users can and cannot do today
- New users start with an empty tenant. **They cannot link banks yet:** the SimpleFIN/MX connection belongs to the household
  owner. They can add accounts and import CSVs manually. Self-service linking is planned
  ([ADR 0009](../adr/0009-multi-provider-self-service-linking.md)).
- Paid lookups (MX connect, named asset-valuation feeds) are household-only; the built-in valuation model is available to all.
- Per-user limits: 5 MB per CSV upload and 50,000 rows per import; 1,000 rules, 1,000 merchant aliases, 500 budgets, 200 goals.
- There is no in-app "delete my account" yet. See the FAQ in [onboarding.md](../onboarding.md) for the manual steps.

## 4. Hand them the guide
- [ ] Send [onboarding.md](../onboarding.md) (first 30 minutes) and [user-guide-advanced.md](../user-guide-advanced.md).

## 5. After inviting
- Watch the API log for `SECURITY:` warnings and repeated 401/403s.
- Remaining known gaps and priorities: [audit-2026-11-18.md](../development/audit-2026-11-18.md).
