# ADR 0009 — Multi-provider readiness (self-service bank linking)

Status: Accepted (design only; implementation deferred)

## Context
Today one provider (Simulated, SimpleFIN Bridge, or MX) is chosen process-wide by config
(`DependencyInjection.cs`) and injected as a singleton `IFinancialDataProvider`. SimpleFIN
Bridge links the operator's own household; every tenant resolves to that one connection.

For wider onboarding we expect to add an aggregator that lets each user link their own banks
without the operator being involved (e.g. Quiltt, or a Plaid/Teller/MX-style widget flow).
That requires per-tenant, per-connection provider selection and per-user credentials.

## What already works in our favour
- `IFinancialDataProvider` + `ProviderCapabilities` is already provider-neutral.
- `ProviderConnectionRecord` is tenant-scoped and keyed by `ProviderName`
  (`IProviderConnectionStore`), sync cursors are keyed by `(ProviderName, providerAccountId)`,
  and the provider call ledger/budget and raw-payload archive take a provider name.
- Provider exceptions (`NeedsUserAction`, `RateLimited`, `Transient`, `Fatal`) are normalised.

## Gaps to close before a second self-service provider ships
1. **Provider resolution.** Replace the singleton with an `IFinancialDataProviderFactory`
   (`Resolve(providerName)`), registering every enabled provider side by side. `FinancialSyncService`
   and the endpoints (`/api/integrations/provider`, `/api/sync/status`, MX connect-token) must
   iterate the tenant's connections and resolve the provider per connection instead of using
   `_provider.ProviderName`. Remove the "Mx and SimpleFin are mutually exclusive" startup check.
2. **Per-connection credentials.** Per-user access tokens (SimpleFIN access URL, Plaid/Quiltt
   session tokens) must live encrypted on the connection record (ASP.NET Data Protection or a KMS),
   never in global config. Never log or return them from the API.
3. **Connection UX contract.** `/api/integrations/provider` currently special-cases SimpleFIN by name.
   Express this via capabilities/descriptor (`UsesWidget`, `ManageUrl`, `SupportsWebhooks`) so the UI
   renders "Link a bank" for whichever providers are enabled.
4. **Multiple connections per tenant/provider.** `GetMemberProviderIdAsync(providerName)` returns a
   single row. A user will link several institutions; key by connection id and add an
   institution/label. Unique index on `(TenantId, ProviderName, MemberProviderId)`.
5. **Household guard.** The "connection belongs to the household" check in `FinancialSyncService`
   must apply only to the shared (operator-owned) provider, not to self-service connections.
6. **Cost and abuse controls.** Per-tenant call budgets already exist; add per-provider global caps
   and rate limits on connect/sync endpoints for self-service providers (billable).
7. **Webhooks.** Verify signatures per provider, route to the owning tenant by connection id,
   and make handlers idempotent.
8. **Data lifecycle.** Deleting a user/tenant must revoke upstream consent with the provider and
   purge archived raw payloads (ADR 0007).

## Decision
Keep the current config-selected provider for the operator's household. Do not build the factory
until a second provider is chosen; new code must not add further `ProviderName == "SimpleFIN"`
string checks or assume a single global provider. When adding a provider, implement
`IFinancialDataProvider` in its own `FlowLedger.Integrations.<Name>` project and follow items 1-8.

## Consequences
- No behaviour change now; the checklist above is the definition of "ready" for self-service linking.
- Provider-specific branching currently in `SyncEndpoints` and `FinancialSyncService` is known debt.
